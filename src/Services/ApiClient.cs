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
    /// 固定使用 POST 请求，API Key 通过 Authorization Header 传递。
    /// </summary>
    public static async Task<(bool ok, decimal pollen, string error)> GetBalanceAsync(string apiKey)
    {
        // 固定使用官方余额查询接口，不允许通过参数覆盖
        const string balanceUrl = "https://gen.pollinations.ai/account/balance";

        if (string.IsNullOrWhiteSpace(apiKey)) return (false, 0, "错误: 未提供 API Key");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, balanceUrl);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, 0, "错误: " + (int)resp.StatusCode + " " + resp.ReasonPhrase);
            }
            var jsonStr = await resp.Content.ReadAsStringAsync();
            return ParsePollen(jsonStr);
        }
        catch (Exception ex)
        {
            return (false, 0, "错误: " + ex.Message);
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
    /// </summary>
    public static async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        string url,
        string apiKey,
        string model,
        List<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct,
        List<object>? tools = null)
    {
        string maskedKey = MaskApiKey(apiKey);
        string messagesJson = JsonSerializer.Serialize(messages, JsonOpts);

        // 日志：请求开始
        Logger.Info($"[StreamChat] 请求接口: {url}");
        Logger.Info($"[StreamChat] 模型: {model}");
        Logger.Info($"[StreamChat] APIKey参数值: {maskedKey}");
        Logger.Info($"[StreamChat] 提交历史记录: {TruncateForLog(messagesJson)}");

        // 构建请求体
        object bodyObj;
        if (tools != null && tools.Count > 0)
        {
            bodyObj = new
            {
                model,
                messages = messages.ConvertAll(m => ConvertMessageToApiFormat(m)),
                safe = false,
                stream = true,
                stream_options = new { include_usage = true },
                API_Key = apiKey,
                tool_choice = "auto",
                tools = tools,
                parallel_tool_calls = true,
            };
        }
        else
        {
            bodyObj = new
            {
                model,
                messages = messages.ConvertAll(m => ConvertMessageToApiFormat(m)),
                safe = false,
                stream = true,
                stream_options = new { include_usage = true },
                API_Key = apiKey,
            };
        }
        var jsonBody = JsonSerializer.Serialize(bodyObj, JsonOpts);

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

        // 工具调用解析所需的局部变量
        StringBuilder accumulatedToolArgs = new();
        string lastToolName = "";
        string lastToolCallId = "";

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

                // 解析 chunk，支持文本和工具调用
                var chunks = ParseStreamChunk(payload, accumulatedToolArgs, lastToolName, lastToolCallId, out lastToolName, out lastToolCallId);
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
                        var chunks = ParseStreamChunk(trimmed, accumulatedToolArgs, lastToolName, lastToolCallId, out lastToolName, out lastToolCallId);
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
    /// 解析流式 chunk，支持文本和工具调用。
    /// </summary>
    private static List<StreamChunk> ParseStreamChunk(
        string payload,
        StringBuilder accumulatedToolArgs,
        string lastToolName,
        string lastToolCallId,
        out string outLastToolName,
        out string outLastToolCallId)
    {
        var chunks = new List<StreamChunk>();
        outLastToolName = lastToolName;
        outLastToolCallId = lastToolCallId;

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

        // 检查 delta.tool_calls
        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var func = tc.GetProperty("function");
                    string funcName = func.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string funcArgs = func.TryGetProperty("arguments", out var a) ? a.GetString() ?? "" : "";
                    string callId = tc.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";

                    // 如果是第一次遇到这个工具调用，yield return ToolCall 类型(空arguments表示开始)
                    if (!string.IsNullOrEmpty(funcName) && accumulatedToolArgs.Length == 0)
                    {
                        outLastToolName = funcName;
                        outLastToolCallId = callId;
                        chunks.Add(new StreamChunk
                        {
                            Type = StreamChunkType.ToolCall,
                            ToolName = funcName,
                            ToolCallId = callId,
                            ToolArguments = ""
                        });
                    }

                    // 累积 arguments
                    accumulatedToolArgs.Append(funcArgs);
                }
            }

            // 检查 delta.content (普通文本)
            if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            {
                var text = contentEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(text))
                {
                    chunks.Add(new StreamChunk { Type = StreamChunkType.Text, Text = text });
                }
            }
        }

        // 在 finish_reason == "tool_calls" 时输出完整的工具调用
        if (finish_reason == "tool_calls")
        {
            chunks.Add(new StreamChunk
            {
                Type = StreamChunkType.ToolCall,
                ToolName = lastToolName,
                ToolCallId = lastToolCallId,
                ToolArguments = accumulatedToolArgs.ToString()
            });
            accumulatedToolArgs.Clear();
            chunks.Add(new StreamChunk { Type = StreamChunkType.Done });
        }

        return chunks;
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
    /// 将 ChatMessage 转换为 OpenAI API 格式的消息对象。
    /// 注意：ChatMessage.Content 已经是 API 原生格式（List<ContentItem>），直接透传即可。
    /// </summary>
    private static object ConvertMessageToApiFormat(ChatMessage m)
    {
        // role 为 tool 的消息
        if (m.Role == "tool")
        {
            return new
            {
                role = m.Role,
                tool_call_id = m.ToolCallId ?? "",
                content = m.Content ?? new List<ContentItem>()
            };
        }

        // role 为 assistant 且有工具调用
        if (m.Role == "assistant" && m.FunctionCall != null)
        {
            var fc = m.FunctionCall;
            var toolCalls = new object[]
            {
                new
                {
                    id = fc.Id,
                    type = "function",
                    function = new
                    {
                        name = fc.Name,
                        arguments = fc.Arguments
                    }
                }
            };
            // 带 tool_calls 的 assistant 消息也可以有 content
            if (m.Content != null && m.Content.Count > 0)
            {
                return new
                {
                    role = m.Role,
                    content = m.Content,
                    tool_calls = toolCalls
                };
            }
            return new
            {
                role = m.Role,
                tool_calls = toolCalls
            };
        }

        // 普通消息 (system / user / assistant纯文本): content 直接透传
        return new
        {
            role = m.Role,
            content = m.Content ?? new List<ContentItem>()
        };
    }
}
