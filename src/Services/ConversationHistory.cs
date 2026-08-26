using System;
using System.Collections.Generic;
using VPet.Plugin.PetChat.Models;

namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 维护用户与助手之间的对话历史记录,采用标准 OpenAI 消息格式。
/// assistant 工具调用使用 tool_calls 数组(支持并行多个工具)。
/// </summary>
public class ConversationHistory
{
    /// <summary>
    /// 当前全部对话消息(从 system 开始)。
    /// </summary>
    public List<ChatMessage> Messages { get; private set; }

    /// <summary>
    /// 使用指定的系统提示初始化对话历史。
    /// </summary>
    public ConversationHistory(string systemPrompt)
    {
        Messages = new List<ChatMessage> { new ChatMessage("system", systemPrompt) };
    }

    /// <summary>
    /// 追加一条用户消息(纯文本)。
    /// </summary>
    public void AppendUser(string text)
    {
        Messages.Add(new ChatMessage("user", text));
    }

    /// <summary>
    /// 追加一条带内容数组的用户消息。text 可为空(仅附件发送),items 不可为空。
    /// </summary>
    public void AppendUser(string? text, List<ContentItem> items)
    {
        var contentList = new List<ContentItem>();

        // 先加文本元素
        if (!string.IsNullOrEmpty(text))
        {
            contentList.Add(new ContentItem { Type = "text", Text = text });
        }

        // 再加其他元素
        contentList.AddRange(items);

        Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = contentList
        });
    }

    /// <summary>
    /// 追加一条助手消息。
    /// </summary>
    public void AppendAssistant(string text)
    {
        Messages.Add(new ChatMessage("assistant", text));
    }

    /// <summary>
    /// 添加工具调用的 assistant 消息(标准 OpenAI tool_calls 数组格式)。
    /// 单次调用单个工具时也包装为单元素数组以保持 API 一致。
    /// </summary>
    public void AppendAssistantToolCall(ToolCallInfo tc)
    {
        Messages.Add(new ChatMessage
        {
            Role = "assistant",
            ToolCalls = new List<ToolCallInfo> { tc }
        });
    }

    /// <summary>
    /// 添加工具调用的 assistant 消息(多个并行工具)。
    /// </summary>
    public void AppendAssistantToolCall(List<ToolCallInfo> toolCalls)
    {
        Messages.Add(new ChatMessage
        {
            Role = "assistant",
            ToolCalls = toolCalls
        });
    }

    /// <summary>
    /// 添加带文本内容和工具调用的 assistant 消息。
    /// 将AI在工具调用前生成的文本与 tool_calls 合并到同一条消息,
    /// 符合标准 OpenAI assistant 消息格式(可同时有 content 和 tool_calls)。
    /// </summary>
    public void AppendAssistantToolCall(ToolCallInfo tc, List<ContentItem>? content)
    {
        Messages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = content,
            ToolCalls = new List<ToolCallInfo> { tc }
        });
    }

    /// <summary>
    /// 添加带文本内容和多个工具调用的 assistant 消息(并行调用场景)。
    /// </summary>
    public void AppendAssistantToolCall(List<ToolCallInfo> toolCalls, List<ContentItem>? content)
    {
        Messages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = content,
            ToolCalls = toolCalls
        });
    }

    /// <summary>
    /// 添加 tool 消息（文本类型）。
    /// tool 消息对应标准格式: { role: "tool", tool_call_id: "...", content: "..." }
    /// </summary>
    public void AppendTool(string toolCallId, string text)
    {
        Messages.Add(new ChatMessage
        {
            Role = "tool",
            ToolCallId = toolCallId,
            Content = new List<ContentItem>
            {
                new ContentItem { Type = "text", Text = text }
            }
        });
    }

    /// <summary>
    /// 添加 tool 消息（ContentItem 列表）。
    /// 发送时会按标准格式转为字符串(tool 消息 content 为 string)。
    /// </summary>
    public void AppendTool(string toolCallId, List<ContentItem> content)
    {
        Messages.Add(new ChatMessage
        {
            Role = "tool",
            ToolCallId = toolCallId,
            Content = content
        });
    }

    /// <summary>
    /// 更新系统提示:若首条为 system 则替换其内容,否则在最前面插入一条新的 system 消息。
    /// </summary>
    public void UpdateSystemPrompt(string newPrompt)
    {
        if (Messages.Count > 0 && Messages[0].Role == "system")
            Messages[0] = new ChatMessage("system", newPrompt);
        else
            Messages.Insert(0, new ChatMessage("system", newPrompt));
    }
}