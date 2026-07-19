using System;
using System.Collections.Generic;
using VPet.Plugin.PetChat.Models;

namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 维护用户与助手之间的对话历史记录,负责验证消息交替结构。
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
    /// 添加工具调用的 assistant 消息。
    /// </summary>
    public void AppendAssistantToolCall(FunctionCallInfo fc)
    {
        Messages.Add(new ChatMessage
        {
            Role = "assistant",
            FunctionCall = fc
        });
    }

    /// <summary>
    /// 添加 tool 消息（文本类型）。
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