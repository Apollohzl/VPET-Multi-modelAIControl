using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VPet.Plugin.PetChat.Models;

namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 流式返回的 chunk 类型。
/// </summary>
public enum StreamChunkType
{
    Text,       // 普通文本
    ToolCall,   // 工具调用完成
    Done,       // 流结束
    Usage       // usage 信息
}

/// <summary>
/// 流式返回的 chunk。
/// </summary>
public class StreamChunk
{
    /// <summary>
    /// chunk 类型。
    /// </summary>
    public StreamChunkType Type { get; init; }

    /// <summary>
    /// 文本内容 (Type=Text 时)。
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// 工具函数名 (Type=ToolCall 时)。
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// 工具调用 ID (Type=ToolCall 时)。
    /// </summary>
    public string? ToolCallId { get; init; }

        /// <summary>
        /// 工具参数字符串 (Type=ToolCall 时)。
        /// </summary>
        public string? ToolArguments { get; init; }

        /// <summary>
        /// 是否为"工具调用完成"chunk。Type=ToolCall 时:false 表示参数开始(仅提示),
        /// true 表示参数已完整,可以执行工具。用此标志区分开始/完成,避免依赖空参数判断。
        /// </summary>
        public bool IsToolCallComplete { get; init; }
    }

    /// <summary>
    /// 流式解析期间累积的单个工具调用(支持同一响应内并行多个)。
    /// </summary>
    internal class PendingToolCall
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Args { get; } = new();
    }

/// <summary>
/// 用于与 Pollinations 兼容网关进行 HTTP 通信的客户端封装,仅使用 BCL 的 System.Net.Http 与 System.Text.Json。
/// </summary>
internal static class ApiClient
{
    // 使用单个静态 HttpClient 实例可避免 Socket 耗尽并复用连接
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "VPet-PetChat/1.0" },
        },
    };

    // 流式请求专用 HttpClient，超时更短以便快速失败
    private static readonly HttpClient _httpStream = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders =
        {
            { "User-Agent", "VPet-PetChat/1.0 (stream)" },
        },
    };

    // JsonSerializer 的序列化选项
    private static JsonSerializerOptions JsonOpts { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // JsonDocument.Parse 使用的独立选项类型
    private static JsonDocumentOptions DocOpts { get; } = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // 辅助方法:使用 Utf8 字节流解析 JSON,避免 net6.0 中对 string 重载的签名差异
    private static JsonDocument ParseJson(string json)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        return JsonDocument.Parse(bytes, DocOpts);
    }

    /// <summary>
    /// 获取可选择的文本生成模型列表。
    /// </summary>
    /// <param name="modelsUrl">返回模型列表的接口地址。</param>
    /// <returns>符合条件的模型选项。</returns>
    public static async Task<List<ModelOption>> GetModelsAsync(string modelsUrl)
    {
        var result = new List<ModelOption>();
        using var resp = await _http.GetAsync(modelsUrl);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = ParseJson(json);
        // 顶层 data 数组: [{ name, title, output_modalities, paid_only, ... }]
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                // 过滤:输出模态需包含 text,且不是付费专属
                bool isText = false;
                if (item.TryGetProperty("output_modalities", out var mods))
                {
                    if (mods.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in mods.EnumerateArray())
                        {
                            if (m.GetString() == "text") { isText = true; break; }
                        }
                    }
                    else if (mods.GetString() == "text")
                    {
                        isText = true;
                    }
                }
                if (!isText) continue;

                bool paidOnly = item.TryGetProperty("paid_only", out var paid) && paid.GetBoolean();
                if (paidOnly) continue;

                // 检查模型支持的输入模态: image / audio / video
                bool supportsImage = false, supportsAudio = false, supportsVideo = false;
                if (item.TryGetProperty("input_modalities", out var inputMods))
                {
                    if (inputMods.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in inputMods.EnumerateArray())
                        {
                            var v = m.GetString();
                            if (v == "image") supportsImage = true;
                            else if (v == "audio") supportsAudio = true;
                            else if (v == "video") supportsVideo = true;
                        }
                    }
                    else
                    {
                        var v = inputMods.GetString();
                        if (v == "image") supportsImage = true;
                        else if (v == "audio") supportsAudio = true;
                        else if (v == "video") supportsVideo = true;
                    }
                }

                string? modelName = item.TryGetProperty("name", out var name) ? name.GetString() : null;
                string? title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (string.IsNullOrEmpty(modelName)) continue;
                result.Add(new ModelOption(modelName!, title ?? modelName!, supportsImage, supportsAudio, supportsVideo));
            }
        }
        return result;
    }

    /// <summary>
    /// 查询账户余额(花粉)。
    /// 使用 GET 请求，API Key 通过 Authorization Header 传递。
    /// balanceUrl 来自配置 endpoints.lps 的 balance_url（与 chat/models 一致的网关），
    /// 缺失时回退到 pollinations 官方地址；apiKeyInBody=true 时额外把 key 作为 API_Key 查询参数发送（兼容部分网关）。
    /// </summary>
    public static async Task<(bool ok, decimal pollen, string error)> GetBalanceAsync(
        string apiKey,
        string balanceUrl = "https://gen.pollinations.ai/account/balance",
        bool apiKeyInBody = false)
    {
        // 余额查询接口地址：优先使用调用方传入的（来自 endpoints.lps 的 balance_url），缺失时回退到默认地址
        if (string.IsNullOrWhiteSpace(balanceUrl))
            balanceUrl = "https://gen.pollinations.ai/account/balance";

        if (string.IsNullOrWhiteSpace(apiKey)) return (false, 0, "错误: 未提供 API Key");

        // 部分网关（api_key_in_body=true）要求把 key 作为 API_Key 查询参数传递
        string requestUrl = balanceUrl;
        if (apiKeyInBody && !string.IsNullOrWhiteSpace(apiKey))
        {
            requestUrl += (requestUrl.Contains('?') ? '&' : '?') + "API_Key=" + Uri.EscapeDataString(apiKey);
        }

        Logger.Info($"[Balance] 开始查询余额, URL={requestUrl}, API Key={MaskApiKey(apiKey)}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            // 保留 Bearer 头以兼容 pollinations 官方接口
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            Logger.Info($"[Balance] 发送 GET 请求, Headers: Authorization=Bearer {MaskApiKey(apiKey)}");

            using var resp = await _http.SendAsync(req);
            var statusCode = (int)resp.StatusCode;
            var reasonPhrase = resp.ReasonPhrase;
            var jsonStr = await resp.Content.ReadAsStringAsync();

            Logger.Info($"[Balance] 响应状态: {statusCode} {reasonPhrase}");
            Logger.Info($"[Balance] 响应内容: {TruncateForLog(jsonStr)}");

            if (!resp.IsSuccessStatusCode)
            {
                Logger.Error($"[Balance] 请求失败: {statusCode} {reasonPhrase}, 响应: {TruncateForLog(jsonStr)}");
                return (false, 0, $"错误: {statusCode} {reasonPhrase}");
            }

            var result = ParsePollen(jsonStr);
            if (result.ok)
                Logger.Info($"[Balance] 查询成功, 花粉={result.pollen}");
            else
                Logger.Error($"[Balance] 解析失败: {result.err}");
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Balance] 查询异常: {ex.Message}\n{ex.StackTrace}");
            return (false, 0, $"错误: {ex.Message}");
        }
    }

    // 从多种字段名读取花粉余额
    private static (bool ok, decimal pollen, string err) ParsePollen(string json)
    {
        if (string.IsNullOrEmpty(json)) return (false, 0, "错误: 响应为空");
        using var doc = ParseJson(json);
        var root = doc.RootElement;
        // 尝试多种字段位置:pollen / balance / data.pollen / data.balance
        if (TryGetDecimal(root, "pollen", out var pollen1)) return (true, pollen1, string.Empty);
        if (TryGetDecimal(root, "balance", out var pollen2)) return (true, pollen2, string.Empty);
        if (root.TryGetProperty("data", out var data))
        {
            if (TryGetDecimal(data, "pollen", out var pollen3)) return (true, pollen3, string.Empty);
            if (TryGetDecimal(data, "balance", out var pollen4)) return (true, pollen4, string.Empty);
        }
        return (false, 0, "错误: 无法解析余额");
    }

    private static bool TryGetDecimal(JsonElement element, string propertyName, out decimal value)
    {
        if (element.TryGetProperty(propertyName, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out value)) return true;
            if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), out value)) return true;
        }
        value = 0;
        return false;
    }

    /// <summary>
    /// 以流式方式发起对话,按顺序返回每一段生成的文本片段。
    /// 同时兼容 SSE 格式和普通 JSON 响应。
    /// useBearerAuth=true 时使用 Bearer Token 认证(Deepseek),false 时使用 body 中的 API_Key(pollenations)。
    /// </summary>
    public static async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        string url,
        string apiKey,
        string model,
        List<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct,
        List<object>? tools = null,
        bool useBearerAuth = false)
    {
        string maskedKey = MaskApiKey(apiKey);
        string messagesJson = JsonSerializer.Serialize(messages, JsonOpts);

        // 日志：请求开始
        Logger.Info($"[StreamChat] 请求接口: {url}");
        Logger.Info($"[StreamChat] 模型: {model}");
        Logger.Info($"[StreamChat] 认证方式: {(useBearerAuth ? "Bearer Token" : "API_Key in body")}");
        Logger.Info($"[StreamChat] APIKey参数值: {maskedKey}");
        Logger.Info($"[StreamChat] 提交历史记录: {TruncateForLog(messagesJson)}");

        // 构建请求体(useBearerAuth 时不放 API_Key 到 body)
        // Deepseek V4 模型默认开启 thinking 模式，工具调用后必须回传 reasoning_content。
        // 我们的实现不保存 reasoning_content，因此强制关闭 thinking 模式避免 400 错误。
        // 参考: https://api-docs.deepseek.com/guides/thinking_mode
        var bodyDict = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = SanitizeAndConvert(messages),
            ["safe"] = false,
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
            ["API_Key"] = useBearerAuth ? null : apiKey,
        };
        if (useBearerAuth)
        {
            bodyDict["thinking"] = new { type = "disabled" };
        }
        if (tools != null && tools.Count > 0)
        {
            bodyDict["tool_choice"] = "auto";
            bodyDict["tools"] = tools;
            bodyDict["parallel_tool_calls"] = true;
        }
        var jsonBody = JsonSerializer.Serialize(bodyDict, JsonOpts);

        // 记录完整请求体(超长时截断),便于调试
        Logger.Info($"[StreamChat] 请求体长度: {jsonBody.Length} 字符");
        if (jsonBody.Length < 3000)
        {
            Logger.Info($"[StreamChat] 请求体: {jsonBody}");
        }
        else
        {
            Logger.Info($"[StreamChat] 请求体(前1500字符): {jsonBody.Substring(0, 1500)}");
            Logger.Info($"[StreamChat] 请求体(后1000字符): {jsonBody.Substring(jsonBody.Length - 1000)}");
        }

        using var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        // Bearer Token 认证(Deepseek)
        if (useBearerAuth && !string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _httpStream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var reason = response.ReasonPhrase ?? string.Empty;
            var errBody = await response.Content.ReadAsStringAsync();
            var errMsg = $"错误: {(int)response.StatusCode} {reason} | 响应: {TruncateForLog(errBody)}";
            Logger.Error($"[StreamChat] 接口报错: {errMsg}");
            throw new HttpRequestException(errMsg);
        }

        Logger.Info($"[StreamChat] 接口状态码: {(int)response.StatusCode} {response.ReasonPhrase}");

        // net6.0 的 ReadAsStreamAsync / ReadLineAsync 不接受 CancellationToken,在此单独注册取消回调以在请求取消时释放流
        using var ctr = ct.Register(() => { try { response.Dispose(); } catch { } });
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

        // 工具调用解析所需的局部变量（支持同一响应内并行多个工具调用）
        var pendingToolCalls = new List<PendingToolCall>();

        string? line;
        bool isSSE = false;
        var outputBuilder = new System.Text.StringBuilder();
        while ((line = await ReadLineWithCancelAsync(reader, ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // 判断是否为 SSE 格式 (以 "data: " 开头)
            const string dataPrefix = "data: ";
            if (line.StartsWith(dataPrefix, StringComparison.Ordinal))
            {
                isSSE = true;
                var payload = line.Substring(dataPrefix.Length).Trim();
                if (payload == "[DONE]") break;
                if (string.IsNullOrEmpty(payload)) continue;

                // 解析 chunk，支持文本和并行工具调用
                var chunks = ParseStreamChunk(payload, pendingToolCalls);
                foreach (var chunk in chunks)
                {
                    if (chunk.Type == StreamChunkType.Text && !string.IsNullOrEmpty(chunk.Text))
                    {
                        outputBuilder.Append(chunk.Text);
                    }
                    yield return chunk;
                }
            }
            else
            {
                // 非 SSE 格式：尝试直接解析为 JSON 响应
                if (!isSSE)
                {
                    // 检查是否看起来像 JSON 对象（非空、非 SSE 行）
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("{") && !string.IsNullOrEmpty(trimmed))
                    {
                        var chunks = ParseStreamChunk(trimmed, pendingToolCalls);
                        foreach (var chunk in chunks)
                        {
                            if (chunk.Type == StreamChunkType.Text && !string.IsNullOrEmpty(chunk.Text))
                            {
                                outputBuilder.Append(chunk.Text);
                            }
                            yield return chunk;
                        }
                        break; // 非 SSE 响应通常一次性返回，解析完即可退出
                    }
                }
            }
        }

        Logger.Info($"[StreamChat] 接口正常输出: {TruncateForLog(outputBuilder.ToString())}");
    }

    /// <summary>
    /// 解析流式 chunk，支持文本与并行多个工具调用。
    /// pendingToolCalls 跨多次调用累积,在 finish_reason=="tool_calls" 时为每个工具调用
    /// 产生一个完成的 ToolCall chunk(携带完整参数与 IsToolCallComplete=true)。
    /// </summary>
    private static List<StreamChunk> ParseStreamChunk(
        string payload,
        List<PendingToolCall> pendingToolCalls)
    {
        var chunks = new List<StreamChunk>();

        using var doc = ParseJson(payload);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return chunks;

        var en = choices.EnumerateArray();
        if (!en.MoveNext())
            return chunks;

        var choice = en.Current;

        // 检查 finish_reason
        string? finish_reason = null;
        if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            finish_reason = fr.GetString();

        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            // 检查 delta.content (普通文本)
            if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            {
                var text = contentEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(text))
                {
                    chunks.Add(new StreamChunk { Type = StreamChunkType.Text, Text = text });
                }
            }

            // 检查 delta.tool_calls（可能并行多个）
            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    string callId = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    string funcName = "";
                    string funcArgs = "";
                    if (tc.TryGetProperty("function", out var funcEl) && funcEl.ValueKind == JsonValueKind.Object)
                    {
                        if (funcEl.TryGetProperty("name", out var n)) funcName = n.GetString() ?? "";
                        if (funcEl.TryGetProperty("arguments", out var a)) funcArgs = a.GetString() ?? "";
                    }

                    // 找到(或创建)对应的 pending 工具调用
                    PendingToolCall? pending = null;
                    foreach (var p in pendingToolCalls)
                    {
                        if (!string.IsNullOrEmpty(callId) && p.Id == callId) { pending = p; break; }
                    }
                    if (pending == null)
                    {
                        pending = new PendingToolCall { Id = callId, Name = funcName };
                        pendingToolCalls.Add(pending);
                        // 首次出现且有函数名时,发出一个"开始"提示 chunk(空参数)
                        if (!string.IsNullOrEmpty(funcName))
                        {
                            chunks.Add(new StreamChunk
                            {
                                Type = StreamChunkType.ToolCall,
                                ToolName = funcName,
                                ToolCallId = callId,
                                ToolArguments = "",
                                IsToolCallComplete = false
                            });
                        }
                    }
                    else if (string.IsNullOrEmpty(pending.Name) && !string.IsNullOrEmpty(funcName))
                    {
                        // 函数名迟到(部分流先给 id,后给 name):补一个"开始"提示
                        pending.Name = funcName;
                        chunks.Add(new StreamChunk
                        {
                            Type = StreamChunkType.ToolCall,
                            ToolName = funcName,
                            ToolCallId = callId,
                            ToolArguments = "",
                            IsToolCallComplete = false
                        });
                    }

                    // 累积该调用的参数片段
                    pending.Args.Append(funcArgs);
                }
            }
        }

        // 流结束时,为每个 pending 工具调用输出完整的 ToolCall(携带完整参数)
        if (finish_reason == "tool_calls")
        {
            foreach (var pending in pendingToolCalls)
            {
                chunks.Add(new StreamChunk
                {
                    Type = StreamChunkType.ToolCall,
                    ToolName = pending.Name,
                    ToolCallId = pending.Id,
                    ToolArguments = pending.Args.ToString(),
                    IsToolCallComplete = true
                });
            }
            pendingToolCalls.Clear();
            chunks.Add(new StreamChunk { Type = StreamChunkType.Done });
        }

        return chunks;
    }

    /// <summary>
    /// 复制消息列表并修复 tool_calls / tool 消息配对:确保每个带 tool_calls 的 assistant
    /// 消息之后都存在对应 tool_call_id 的 tool 消息;若缺失则插入占位 tool 消息。
    /// 只修改用于发送的副本,不污染调用方的历史文件。
    /// </summary>
    private static object SanitizeAndConvert(List<ChatMessage> messages)
    {
        var apiMessages = messages.ConvertAll(m => m);
        EnsureToolMessages(apiMessages);
        return apiMessages.ConvertAll(m => ConvertMessageToApiFormat(m));
    }

    /// <summary>
    /// 确保每个带 tool_calls 的 assistant 消息之后都存在对应每个 tool_call_id 的 tool 消息。
    /// 严格供应商(如 Deepseek)要求 assistant(tool_calls=[...]) 后必须紧跟对应 id 的 tool 消息,
    /// 缺失则返回 400: "An assistant message with 'tool_calls' must be followed by tool messages..."。
    /// 支持标准 OpenAI 格式的并行工具调用(一条 assistant 可含多个 tool_calls)。
    /// </summary>
    private static void EnsureToolMessages(List<ChatMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Role != "assistant" || m.ToolCalls == null || m.ToolCalls.Count == 0)
                continue;

            // 标准格式: 遍历 tool_calls 数组的每一项,分别查找对应 tool 消息
            var requiredIds = new HashSet<string>();
            foreach (var tc in m.ToolCalls)
            {
                if (!string.IsNullOrEmpty(tc.Id))
                    requiredIds.Add(tc.Id);
            }
            if (requiredIds.Count == 0) continue;

            var foundIds = new HashSet<string>();
            for (int j = i + 1; j < messages.Count; j++)
            {
                var after = messages[j];
                // 工具消息必须紧跟在 assistant 之后,遇到其它角色即视为缺失
                if (after.Role == "assistant" || after.Role == "user" || after.Role == "system") break;
                if (after.Role == "tool" && !string.IsNullOrEmpty(after.ToolCallId))
                    foundIds.Add(after.ToolCallId);
            }

            // 缺失的 id 在其后插入占位 tool 消息(保持原有顺序)
            int insertIdx = i + 1;
            foreach (var tc in m.ToolCalls)
            {
                if (string.IsNullOrEmpty(tc.Id)) continue;
                if (foundIds.Contains(tc.Id)) continue;
                messages.Insert(insertIdx++, new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = tc.Id,
                    Content = new List<ContentItem> { new ContentItem { Type = "text", Text = "(工具结果缺失,已自动补齐)" } }
                });
                i++; // 跳过刚插入的占位消息
            }
        }
    }

    private static string MaskApiKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "(空)";
        if (key.Length <= 8) return "****";
        return key.Substring(0, 4) + "****" + key.Substring(key.Length - 4);
    }

    private static string TruncateForLog(string text)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        return text.Length > 2000 ? text.Substring(0, 2000) + "...(已截断)" : text;
    }

    private static bool TryExtractContentFromPayload(string payload, out string content)
    {
        content = string.Empty;
        try
        {
            using var doc = ParseJson(payload);
            var root = doc.RootElement;

            // 优先读取 choices[0].delta.content
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                var en = choices.EnumerateArray();
                if (en.MoveNext())
                {
                    var choice = en.Current;
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                        {
                            content = contentEl.GetString() ?? string.Empty;
                            return true;
                        }
                    }
                    // 兼容非流式响应: choices[0].message.content
                    if (choice.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var msgContent) &&
                        msgContent.ValueKind == JsonValueKind.String)
                    {
                        content = msgContent.GetString() ?? string.Empty;
                        return true;
                    }
                }
            }

            // 兼容 OpenAI 风格: content 字段（直接返回的内容）
            if (root.TryGetProperty("content", out var directContent) && directContent.ValueKind == JsonValueKind.String)
            {
                content = directContent.GetString() ?? string.Empty;
                return true;
            }
        }
        catch
        {
            // JSON 解析失败，忽略该行
        }
        return false;
    }

    // net6.0 中 StreamReader.ReadLineAsync 不接受 CancellationToken,包装一个基于 Task 的兼容实现
    private static async Task<string?> ReadLineWithCancelAsync(StreamReader reader, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var readTask = reader.ReadLineAsync();
        var completed = await Task.WhenAny(readTask, tcs.Task);
        if (completed == tcs.Task)
            return null;
        return await readTask;
    }

    /// <summary>
    /// 将 ChatMessage 转换为标准 OpenAI API 格式的消息对象。
    /// 严格对齐 OpenAI 规范:
    ///   - tool / assistant / system 消息的 content 为字符串
    ///   - user 消息的 content 为数组格式(支持多模态输入)
    ///   - assistant 工具调用使用 tool_calls 数组: [{id,type:"function",function:{name,arguments}}]
    /// </summary>
    private static object ConvertMessageToApiFormat(ChatMessage m)
    {
        // role 为 tool 的消息: 标准格式 { role, tool_call_id, content(string) }
        if (m.Role == "tool")
        {
            return new
            {
                role = m.Role,
                tool_call_id = m.ToolCallId ?? "",
                content = ExtractTextFromContent(m.Content)
            };
        }

        // role 为 assistant 且含 tool_calls(标准 OpenAI 格式,支持并行多个工具调用)
        if (m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count > 0)
        {
            // 按标准格式输出 tool_calls 数组
            var toolCalls = m.ToolCalls.ConvertAll(tc => new
            {
                id = tc.Id,
                type = tc.Type ?? "function",
                function = new
                {
                    name = tc.Function?.Name ?? "",
                    arguments = tc.Function?.Arguments ?? ""
                }
            });

            // assistant 消息可同时含 content 和 tool_calls(均为标准格式)
            string textContent = ExtractTextFromContent(m.Content);
            if (!string.IsNullOrEmpty(textContent))
            {
                return new
                {
                    role = m.Role,
                    content = textContent,
                    tool_calls = toolCalls
                };
            }
            return new
            {
                role = m.Role,
                tool_calls = toolCalls
            };
        }

        // role 为 assistant 纯文本消息: content 必须是字符串
        if (m.Role == "assistant")
        {
            return new
            {
                role = m.Role,
                content = ExtractTextFromContent(m.Content)
            };
        }

        // role 为 system: content 必须是字符串
        if (m.Role == "system")
        {
            return new
            {
                role = m.Role,
                content = ExtractTextFromContent(m.Content)
            };
        }

        // role 为 user: content 保持数组格式(支持多模态: 图片/音频/视频/文件)
        return new
        {
            role = m.Role,
            content = m.Content ?? new List<ContentItem>()
        };
    }

    /// <summary>
    /// 从 ContentItem 列表中提取纯文本内容。
    /// DeepSeek 等严格供应商要求 tool/assistant 消息的 content 为字符串类型,
    /// 而非数组格式。此方法将 content 数组中的 text 元素拼接为字符串。
    /// </summary>
    private static string ExtractTextFromContent(List<ContentItem>? content)
    {
        if (content == null || content.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var item in content)
        {
            if (item.Type == "text" && !string.IsNullOrEmpty(item.Text))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(item.Text);
            }
        }
        return sb.ToString();
    }
}
