using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VPet.Plugin.PetChat.Models;

namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 对话历史持久化服务：负责将用户与助手的对话记录读写到 JSON 文件。
/// 存储格式与 API.md 完全一致，content 为数组。
/// 支持旧格式（Attachments 字段）的兼容加载。
/// </summary>
public class ChatHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;

    /// <summary>当前已持久化的消息轮数（不含 system 提示）。</summary>
    public int LastSavedTurns { get; private set; }

    public ChatHistoryStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// 获取默认历史文件路径（plugin/chat_history.json）。
    /// </summary>
    public static string GetDefaultPath()
    {
        string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
        return Path.Combine(pluginDir, "chat_history.json");
    }

    /// <summary>
    /// 从 JSON 文件加载对话历史。若文件不存在或解析失败，则返回仅含系统提示的历史。
    /// 支持新格式（content 数组）和旧格式（Content 字符串 + Attachments）。
    /// </summary>
    public ConversationHistory Load(string systemPrompt)
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                Logger.Info("[History] 历史文件不存在，使用空历史");
                return new ConversationHistory(systemPrompt);
            }

            string json = File.ReadAllText(_filePath, Encoding.UTF8);

            if (string.IsNullOrWhiteSpace(json) || json == "[]")
            {
                Logger.Info("[History] 历史文件为空，使用空历史");
                return new ConversationHistory(systemPrompt);
            }

            using var doc = JsonDocument.Parse(json);

            var history = new ConversationHistory(systemPrompt);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    var msg = ParseMessageFromJsonElement(elem);
                    if (msg != null)
                    {
                        history.Messages.Add(msg);
                    }
                }
            }

            int turns = (history.Messages.Count - 1) / 2;
            LastSavedTurns = turns;
            Logger.Info($"[History] 加载完成，共 {turns} 轮对话 ({history.Messages.Count - 1} 条消息)");
            return history;
        }
        catch (Exception ex)
        {
            Logger.Error("[History] 加载历史失败，尝试从文件恢复", ex);
            return new ConversationHistory(systemPrompt);
        }
    }

    /// <summary>
    /// 从 JsonElement 解析一条消息，兼容新旧格式。
    /// 新格式：content 为数组
    /// 旧格式：Content 为字符串，可能有 Attachments
    /// </summary>
    private static ChatMessage? ParseMessageFromJsonElement(JsonElement elem)
    {
        if (!elem.TryGetProperty("Role", out var roleElem) && !elem.TryGetProperty("role", out roleElem))
            return null;

        string? role = roleElem.GetString();
        if (string.IsNullOrEmpty(role)) return null;

        var contentItems = new List<ContentItem>();

        // 解析 content：可能是数组（新格式）或字符串（旧格式）
        if (elem.TryGetProperty("Content", out var contentElem) || elem.TryGetProperty("content", out contentElem))
        {
            if (contentElem.ValueKind == JsonValueKind.Array)
            {
                // 新格式：数组
                foreach (var itemElem in contentElem.EnumerateArray())
                {
                    var item = ParseContentItem(itemElem);
                    if (item != null)
                        contentItems.Add(item);
                }
            }
            else if (contentElem.ValueKind == JsonValueKind.String)
            {
                // 旧格式：字符串 → 转为 text 元素
                string? text = contentElem.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    contentItems.Add(new ContentItem { Type = "text", Text = text });
                }
            }
        }

        // 旧格式：Attachments 数组 → 转换为 content 元素
        if (elem.TryGetProperty("Attachments", out var attElem) || elem.TryGetProperty("attachments", out attElem))
        {
            if (attElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var attItem in attElem.EnumerateArray())
                {
                    var item = ParseAttachmentFromJson(attItem);
                    if (item != null)
                        contentItems.Add(item);
                }
            }
        }

        // tool 消息：旧格式 ToolContent → 转换为 content
        if (role == "tool")
        {
            if (elem.TryGetProperty("ToolContent", out var toolContentElem) || elem.TryGetProperty("tool_content", out toolContentElem))
            {
                if (toolContentElem.ValueKind == JsonValueKind.Object)
                {
                    string? tcType = null;
                    if (toolContentElem.TryGetProperty("type", out var t)) tcType = t.GetString();
                    else if (toolContentElem.TryGetProperty("Type", out t)) tcType = t.GetString();
                    tcType ??= "text";

                    if (tcType == "text")
                    {
                        string? tcText = null;
                        if (toolContentElem.TryGetProperty("text", out var tt)) tcText = tt.GetString();
                        else if (toolContentElem.TryGetProperty("Text", out tt)) tcText = tt.GetString();
                        if (!string.IsNullOrEmpty(tcText) && contentItems.Count == 0)
                        {
                            contentItems.Add(new ContentItem { Type = "text", Text = tcText });
                        }
                    }
                    else if (tcType == "image_url")
                    {
                        string? imgUrl = null;
                        if (toolContentElem.TryGetProperty("image_url", out var iu) && iu.ValueKind == JsonValueKind.Object)
                        {
                            if (iu.TryGetProperty("url", out var u)) imgUrl = u.GetString();
                            else if (iu.TryGetProperty("Url", out u)) imgUrl = u.GetString();
                        }
                        else if (toolContentElem.TryGetProperty("ImageUrl", out var iu2) && iu2.ValueKind == JsonValueKind.Object)
                        {
                            if (iu2.TryGetProperty("Url", out var u)) imgUrl = u.GetString();
                        }
                        if (!string.IsNullOrEmpty(imgUrl))
                        {
                            contentItems.Add(new ContentItem
                            {
                                Type = "image_url",
                                ImageUrl = new ImageUrlObj { Url = imgUrl }
                            });
                        }
                    }
                }
            }
        }

        string? toolCallId = null;
        if (elem.TryGetProperty("ToolCallId", out var tciElem) || elem.TryGetProperty("tool_call_id", out tciElem))
            toolCallId = tciElem.GetString();

        // ====== 标准 OpenAI 格式: 优先读取 tool_calls 数组 ======
        List<ToolCallInfo>? toolCalls = null;
        if (elem.TryGetProperty("ToolCalls", out var tcsElem) || elem.TryGetProperty("tool_calls", out tcsElem))
        {
            if (tcsElem.ValueKind == JsonValueKind.Array)
            {
                toolCalls = new List<ToolCallInfo>();
                foreach (var tcElem in tcsElem.EnumerateArray())
                {
                    if (tcElem.ValueKind != JsonValueKind.Object) continue;
                    string tcId = "";
                    if (tcElem.TryGetProperty("id", out var idEl) || tcElem.TryGetProperty("Id", out idEl))
                        tcId = idEl.GetString() ?? "";
                    string tcType = "function";
                    if (tcElem.TryGetProperty("type", out var tpEl) || tcElem.TryGetProperty("Type", out tpEl))
                        tcType = tpEl.GetString() ?? "function";
                    string funcName = "";
                    string funcArgs = "";
                    if (tcElem.TryGetProperty("function", out var funcEl) || tcElem.TryGetProperty("Function", out funcEl))
                    {
                        if (funcEl.ValueKind == JsonValueKind.Object)
                        {
                            if (funcEl.TryGetProperty("name", out var nmEl) || funcEl.TryGetProperty("Name", out nmEl))
                                funcName = nmEl.GetString() ?? "";
                            if (funcEl.TryGetProperty("arguments", out var agEl) || funcEl.TryGetProperty("Arguments", out agEl))
                                funcArgs = agEl.GetString() ?? "";
                        }
                    }
                    toolCalls.Add(new ToolCallInfo
                    {
                        Id = tcId,
                        Type = tcType,
                        Function = new ToolCallFunction { Name = funcName, Arguments = funcArgs }
                    });
                }
                if (toolCalls.Count == 0) toolCalls = null;
            }
        }

        // ====== 兼容旧格式: 读取单个 function_call 字段,转换为 tool_calls[1] 数组 ======
        if (toolCalls == null && (elem.TryGetProperty("FunctionCall", out var fcElem) || elem.TryGetProperty("function_call", out fcElem)))
        {
            if (fcElem.ValueKind == JsonValueKind.Object)
            {
                string fcType = "function";
                if (fcElem.TryGetProperty("type", out var t)) fcType = t.GetString() ?? "function";
                else if (fcElem.TryGetProperty("Type", out t)) fcType = t.GetString() ?? "function";

                string fcId = "";
                if (fcElem.TryGetProperty("id", out var id)) fcId = id.GetString() ?? "";
                else if (fcElem.TryGetProperty("Id", out id)) fcId = id.GetString() ?? "";

                string fcName = "";
                if (fcElem.TryGetProperty("name", out var n)) fcName = n.GetString() ?? "";
                else if (fcElem.TryGetProperty("Name", out n)) fcName = n.GetString() ?? "";

                string fcArgs = "";
                if (fcElem.TryGetProperty("arguments", out var a)) fcArgs = a.GetString() ?? "";
                else if (fcElem.TryGetProperty("Arguments", out a)) fcArgs = a.GetString() ?? "";

                toolCalls = new List<ToolCallInfo>
                {
                    new ToolCallInfo
                    {
                        Id = fcId,
                        Type = fcType,
                        Function = new ToolCallFunction { Name = fcName, Arguments = fcArgs }
                    }
                };
            }
        }

        // 根据 role 过滤
        if (role == "user")
        {
            if (contentItems.Count == 0) return null;
            return new ChatMessage { Role = role, Content = contentItems };
        }
        else if (role == "assistant")
        {
            if (toolCalls != null && toolCalls.Count > 0)
            {
                return new ChatMessage { Role = role, Content = contentItems.Count > 0 ? contentItems : null, ToolCalls = toolCalls };
            }
            if (contentItems.Count > 0)
            {
                return new ChatMessage { Role = role, Content = contentItems };
            }
            return null;
        }
        else if (role == "tool")
        {
            if (string.IsNullOrEmpty(toolCallId) || contentItems.Count == 0) return null;
            return new ChatMessage { Role = role, ToolCallId = toolCallId, Content = contentItems };
        }

        return null;
    }

    /// <summary>
    /// 从 JsonElement 解析一个 content 项。
    /// </summary>
    private static ContentItem? ParseContentItem(JsonElement elem)
    {
        string? type = null;
        if (elem.TryGetProperty("type", out var typeElem))
            type = typeElem.GetString();
        else if (elem.TryGetProperty("Type", out typeElem))
            type = typeElem.GetString();
        else if (elem.TryGetProperty("$type", out typeElem))
            type = typeElem.GetString();

        if (string.IsNullOrEmpty(type)) return null;

        switch (type)
        {
            case "text":
            {
                string? text = null;
                if (elem.TryGetProperty("text", out var t)) text = t.GetString();
                else if (elem.TryGetProperty("Text", out t)) text = t.GetString();
                if (string.IsNullOrEmpty(text)) return null;
                return new ContentItem { Type = "text", Text = text };
            }
            case "image_url":
            {
                string? url = null;
                if (elem.TryGetProperty("image_url", out var iu) && iu.ValueKind == JsonValueKind.Object)
                {
                    if (iu.TryGetProperty("url", out var u)) url = u.GetString();
                    else if (iu.TryGetProperty("Url", out u)) url = u.GetString();
                }
                else if (elem.TryGetProperty("ImageUrl", out var iu2) && iu2.ValueKind == JsonValueKind.Object)
                {
                    if (iu2.TryGetProperty("Url", out var u)) url = u.GetString();
                }
                if (string.IsNullOrEmpty(url)) return null;
                return new ContentItem { Type = "image_url", ImageUrl = new ImageUrlObj { Url = url } };
            }
            case "video_url":
            {
                string? url = null;
                if (elem.TryGetProperty("video_url", out var vu) && vu.ValueKind == JsonValueKind.Object)
                {
                    if (vu.TryGetProperty("url", out var u)) url = u.GetString();
                }
                if (string.IsNullOrEmpty(url)) return null;
                return new ContentItem { Type = "video_url", VideoUrl = new VideoUrlObj { Url = url } };
            }
            case "input_audio":
            {
                string? data = null;
                string? format = "mp3";
                if (elem.TryGetProperty("input_audio", out var ia) && ia.ValueKind == JsonValueKind.Object)
                {
                    if (ia.TryGetProperty("data", out var d)) data = d.GetString();
                    if (ia.TryGetProperty("format", out var f)) format = f.GetString();
                }
                if (string.IsNullOrEmpty(data)) return null;
                return new ContentItem { Type = "input_audio", InputAudio = new InputAudioObj { Data = data, Format = format ?? "mp3" } };
            }
            case "file":
            {
                string? fileData = null;
                string? fileName = null;
                string? fileId = null;
                string? fileUrl = null;
                if (elem.TryGetProperty("file", out var fe) && fe.ValueKind == JsonValueKind.Object)
                {
                    if (fe.TryGetProperty("file_data", out var d)) fileData = d.GetString();
                    else if (fe.TryGetProperty("FileData", out d)) fileData = d.GetString();
                    if (fe.TryGetProperty("file_name", out var n)) fileName = n.GetString();
                    else if (fe.TryGetProperty("FileName", out n)) fileName = n.GetString();
                    if (fe.TryGetProperty("file_id", out var id)) fileId = id.GetString();
                    if (fe.TryGetProperty("file_url", out var fu)) fileUrl = fu.GetString();
                }
                if (string.IsNullOrEmpty(fileData) || string.IsNullOrEmpty(fileName)) return null;
                return new ContentItem
                {
                    Type = "file",
                    File = new FileObj
                    {
                        FileData = fileData,
                        FileName = fileName,
                        FileId = fileId,
                        FileUrl = fileUrl
                    }
                };
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// 从旧格式 Attachments 的 JsonElement 解析为 ContentItem。
    /// </summary>
    private static ContentItem? ParseAttachmentFromJson(JsonElement elem)
    {
        // 旧格式用 $type 区分
        string? type = null;
        if (elem.TryGetProperty("$type", out var typeElem))
            type = typeElem.GetString();
        else if (elem.TryGetProperty("Type", out typeElem))
            type = typeElem.GetString();

        if (string.IsNullOrEmpty(type)) return null;

        switch (type)
        {
            case "text":
            case "TextAttachment":
            {
                string? text = null;
                if (elem.TryGetProperty("Text", out var t)) text = t.GetString();
                else if (elem.TryGetProperty("text", out t)) text = t.GetString();
                if (string.IsNullOrEmpty(text)) return null;
                return new ContentItem { Type = "text", Text = text };
            }
            case "image_url":
            case "ImageUrlAttachment":
            {
                string? url = null;
                if (elem.TryGetProperty("ImageUrl", out var iu) && iu.ValueKind == JsonValueKind.Object)
                {
                    if (iu.TryGetProperty("Url", out var u)) url = u.GetString();
                }
                if (string.IsNullOrEmpty(url)) return null;
                return new ContentItem { Type = "image_url", ImageUrl = new ImageUrlObj { Url = url } };
            }
            case "video_url":
            case "VideoUrlAttachment":
            {
                string? url = null;
                if (elem.TryGetProperty("VideoUrl", out var vu) && vu.ValueKind == JsonValueKind.Object)
                {
                    if (vu.TryGetProperty("Url", out var u)) url = u.GetString();
                }
                if (string.IsNullOrEmpty(url)) return null;
                return new ContentItem { Type = "video_url", VideoUrl = new VideoUrlObj { Url = url } };
            }
            case "input_audio":
            case "InputAudioAttachment":
            {
                string? data = null;
                string? format = "mp3";
                if (elem.TryGetProperty("InputAudio", out var ia) && ia.ValueKind == JsonValueKind.Object)
                {
                    if (ia.TryGetProperty("Data", out var d)) data = d.GetString();
                    if (ia.TryGetProperty("Format", out var f)) format = f.GetString();
                }
                if (string.IsNullOrEmpty(data)) return null;
                return new ContentItem { Type = "input_audio", InputAudio = new InputAudioObj { Data = data, Format = format ?? "mp3" } };
            }
            case "file":
            case "FileAttachment":
            {
                string? fileData = null;
                string? fileName = null;
                string? fileId = null;
                string? fileUrl = null;
                if (elem.TryGetProperty("File", out var fe) && fe.ValueKind == JsonValueKind.Object)
                {
                    if (fe.TryGetProperty("FileData", out var d)) fileData = d.GetString();
                    if (fe.TryGetProperty("FileName", out var n)) fileName = n.GetString();
                    if (fe.TryGetProperty("FileId", out var id)) fileId = id.GetString();
                    if (fe.TryGetProperty("FileUrl", out var fu)) fileUrl = fu.GetString();
                }
                if (string.IsNullOrEmpty(fileData) || string.IsNullOrEmpty(fileName)) return null;
                return new ContentItem
                {
                    Type = "file",
                    File = new FileObj
                    {
                        FileData = fileData,
                        FileName = fileName,
                        FileId = fileId,
                        FileUrl = fileUrl
                    }
                };
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// 将对话历史保存到 JSON 文件。保存 user/assistant/tool 消息（不含 system 提示，避免与配置重复）。
    /// 存储格式与 API.md 完全一致。
    /// </summary>
    public void Save(ConversationHistory history)
    {
        try
        {
            if (history == null) return;

            var toSave = new List<ChatMessage>();
            foreach (var m in history.Messages)
            {
                if (m.Role == "system") continue;

                // user: 有 content 数组且不为空
                if (m.Role == "user")
                {
                    if (m.Content != null && m.Content.Count > 0)
                        toSave.Add(m);
                }
                // assistant: 有 ToolCalls 数组(标准格式) 或有 content
                else if (m.Role == "assistant")
                {
                    if ((m.ToolCalls != null && m.ToolCalls.Count > 0) || (m.Content != null && m.Content.Count > 0))
                        toSave.Add(m);
                }
                // tool: 有 ToolCallId 且有 content
                else if (m.Role == "tool")
                {
                    if (!string.IsNullOrEmpty(m.ToolCallId) && m.Content != null && m.Content.Count > 0)
                        toSave.Add(m);
                }
            }

            string json = JsonSerializer.Serialize(toSave, JsonOptions);
            File.WriteAllText(_filePath, json, Encoding.UTF8);

            int turns = toSave.Count / 2;
            LastSavedTurns = turns;
            Logger.Info($"[History] 已保存，共 {turns} 轮对话 ({toSave.Count} 条消息) -> {_filePath}");
        }
        catch (Exception ex)
        {
            Logger.Error("[History] 保存历史失败", ex);
        }
    }

    /// <summary>
    /// 清空历史文件并重新创建仅含系统提示的历史。
    /// </summary>
    public ConversationHistory Clear(string systemPrompt)
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
            LastSavedTurns = 0;
            Logger.Info("[History] 历史已清空");
        }
        catch (Exception ex)
        {
            Logger.Error("[History] 清空历史失败", ex);
        }
        return new ConversationHistory(systemPrompt);
    }

    /// <summary>
    /// 返回当前历史文件的路径。
    /// </summary>
    public string FilePath => _filePath;
}
