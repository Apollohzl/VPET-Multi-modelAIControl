using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VPet.Plugin.PetChat.Models;

/// <summary>
/// 表示对话中的一条消息,采用标准 OpenAI 格式。
/// 四种 role: system / user / assistant / tool
/// 所有 JSON 属性名使用全小写/snake_case,严格对齐 OpenAI API。
/// </summary>
public record ChatMessage
{
    /// <summary>角色: system / user / assistant / tool</summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    /// <summary>消息内容数组 (user 消息),字符串格式时为纯文本(assistant/tool/system)。
    /// 发送时在 ConvertMessageToApiFormat 中按 role 做对应转换。</summary>
    [JsonPropertyName("content")]
    public List<ContentItem>? Content { get; init; }

    /// <summary>工具调用数组(标准 OpenAI 格式)。仅 assistant 消息且含有工具调用时使用。
    /// 数组元素为 {id,type:"function",function:{name,arguments}}。
    /// 支持并行多个工具调用。</summary>
    [JsonPropertyName("tool_calls")]
    public List<ToolCallInfo>? ToolCalls { get; init; }

    /// <summary>工具调用 ID。仅 role=tool 消息使用。</summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    // 无参构造函数，支持对象初始化器
    public ChatMessage() { }

    // 便捷构造：纯文本消息
    public ChatMessage(string role, string text)
    {
        Role = role;
        Content = new List<ContentItem>
        {
            new ContentItem { Type = "text", Text = text }
        };
    }

    // 便捷构造：带内容数组的消息
    public ChatMessage(string role, List<ContentItem> content)
    {
        Role = role;
        Content = content;
    }
}

/// <summary>
/// 标准 OpenAI 工具调用元素。对应 tool_calls 数组中的一项。
/// 每个元素包含 { id, type, function: { name, arguments } }
/// </summary>
public record ToolCallInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public ToolCallFunction Function { get; init; } = new();
}

/// <summary>
/// 标准 OpenAI 工具调用中的 function 子对象。
/// </summary>
public record ToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = string.Empty;
}

// ============================================================
//  content 数组元素 (与 API.md 中 content 数组元素格式完全一致)
// ============================================================

/// <summary>
/// content 数组的一个元素。对应 API.md 中 content 数组的一项。
/// 根据 type 字段区分具体类型，其余字段为 null 时不序列化。
/// 所有 JSON 属性名使用全小写/snake_case，与 API.md 一致。
/// </summary>
public record ContentItem
{
    /// <summary>元素类型: text / image_url / video_url / input_audio / file</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    /// <summary>文本内容 (type=text 时)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>图片 URL 对象 (type=image_url 时)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("image_url")]
    public ImageUrlObj? ImageUrl { get; init; }

    /// <summary>视频 URL 对象 (type=video_url 时)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("video_url")]
    public VideoUrlObj? VideoUrl { get; init; }

    /// <summary>音频对象 (type=input_audio 时)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("input_audio")]
    public InputAudioObj? InputAudio { get; init; }

    /// <summary>文件对象 (type=file 时)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("file")]
    public FileObj? File { get; init; }

    /// <summary>MIME 类型 (tool 消息返回结果时可能有)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("mime_type")]
    public string? MimeType { get; init; }
}

/// <summary>
/// 图片 URL 对象。
/// </summary>
public record ImageUrlObj
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;
}

/// <summary>
/// 视频 URL 对象。
/// </summary>
public record VideoUrlObj
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;
}

/// <summary>
/// 音频对象。
/// </summary>
public record InputAudioObj
{
    [JsonPropertyName("data")]
    public string Data { get; init; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; init; } = "mp3";
}

/// <summary>
/// 文件对象。
/// </summary>
public record FileObj
{
    /// <summary>必须: "文件绝对路径\n文件内容"</summary>
    [JsonPropertyName("file_data")]
    public string FileData { get; init; } = string.Empty;

    /// <summary>必须: 文件名</summary>
    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;

    /// <summary>非必须: 文件 ID</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("file_id")]
    public string? FileId { get; init; }

    /// <summary>非必须: 文件 URL</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("file_url")]
    public string? FileUrl { get; init; }
}
