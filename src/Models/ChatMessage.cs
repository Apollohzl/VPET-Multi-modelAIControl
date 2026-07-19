using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VPet.Plugin.PetChat.Models;

/// <summary>
/// 表示对话中的一条消息,包含角色与内容。
/// 根据 API.md, 消息分为四种 role: system / user / assistant / tool
/// 所有消息的 content 都以数组形式存储，与 API.md 格式一致。
/// 所有 JSON 属性名使用全小写/snake_case，与 API.md 一致。
/// </summary>
public record ChatMessage
{
    /// <summary>角色: system / user / assistant / tool</summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    /// <summary>消息内容数组，每个元素是一个 content 项（text/image_url/video_url/input_audio/file 等）。
    /// 与 API.md 中 content 数组格式完全一致。</summary>
    [JsonPropertyName("content")]
    public List<ContentItem>? Content { get; init; }

    /// <summary>工具调用信息。仅 assistant 消息且为工具调用时使用。</summary>
    [JsonPropertyName("function_call")]
    public FunctionCallInfo? FunctionCall { get; init; }

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
/// 工具调用函数信息。
/// </summary>
public record FunctionCallInfo
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

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
