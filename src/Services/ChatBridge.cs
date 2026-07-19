using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using VPet.Plugin.PetChat.Models;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 对话桥接器。支持 AI 工具调用(Function Calling)的完整对话流程,以及文件拖拽多模态输入。
/// 每次对话都从 chat_history.json 文件读取历史,确保数据一致性。
/// </summary>
public class ChatBridge
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _systemPrompt;
    private readonly IMainWindow _mw;
    private readonly string _chatUrl;
    private readonly SkillManager _skillManager;
    private readonly ToolExecutor _toolExecutor;
    private readonly ChatHistoryStore _historyStore;
    private readonly GameDataInjector _gameDataInjector;
    private readonly Dispatcher _dispatcher;

    private readonly bool _supportsImageInput;
    private readonly bool _supportsAudioInput;
    private readonly bool _supportsVideoInput;

    public ChatBridge(
        string apiKey,
        string model,
        string systemPrompt,
        IMainWindow mw,
        string chatUrl,
        SkillManager skillManager,
        ChatHistoryStore historyStore,
        bool supportsImageInput = true,
        bool supportsAudioInput = false,
        bool supportsVideoInput = false)
    {
        _apiKey = apiKey;
        _model = model;
        _systemPrompt = systemPrompt;
        _mw = mw;
        _chatUrl = chatUrl;
        _skillManager = skillManager;
        _toolExecutor = new ToolExecutor(skillManager, apiKey, supportsImageInput, supportsAudioInput, supportsVideoInput);
        _historyStore = historyStore;
        _gameDataInjector = new GameDataInjector(mw);
        _dispatcher = GetDispatcher(mw);
        _supportsImageInput = supportsImageInput;
        _supportsAudioInput = supportsAudioInput;
        _supportsVideoInput = supportsVideoInput;
    }

    /// <summary>
    /// 发送对话(含工具调用支持)。
    /// 流程: 读取文件 → 添加用户消息 → 保存文件 → 重新读取 → 发送请求
    /// </summary>
    public async Task SendChatAsync(string userInput, SayInfoWithStream sis, CancellationToken ct)
    {
        var fullText = new StringBuilder();
        
        // 1. 获取宠物核心
        object? main = null;
        _dispatcher.Invoke(() =>
        {
            main = GetMain(_mw);
        });

        if (main == null)
        {
            _dispatcher.Invoke(() => sis.UpdateText("错误: 无法获取宠物核心"));
            return;
        }

        // 2. 构建初始 user 消息(包含游戏数据)
        string petName = GetPetName(_mw);
        string renderedPrompt = PromptRenderer.Render(_systemPrompt, petName);
        string gameData = _gameDataInjector.BuildGameDataBlock();

        Logger.Info($"[Chat] 用户输入: {userInput}");
        Logger.Info($"[Chat] 游戏数据整合后: {gameData}");

        // 3. 读取文件历史 → 添加用户消息 → 保存文件
        var history = _historyStore.Load(_systemPrompt);
        history.AppendUser(userInput + gameData);
        _historyStore.Save(history);
        Logger.Info($"[Chat] 用户消息已保存到文件");

        // 4. 重新读取文件
        history = _historyStore.Load(_systemPrompt);

        // 5. 开始对话循环(支持连续工具调用)
        await StreamAndProcessAsync(main, sis, ct, fullText, history);
    }

    private async Task StreamAndProcessAsync(object main, SayInfoWithStream sis, CancellationToken ct, StringBuilder fullText, ConversationHistory history)
    {
        while (true)
        {
            // 获取已启用的工具(根据模型是否支持图片输入过滤不兼容工具)
            var tools = _skillManager.GetEnabledTools(_supportsImageInput);

            Logger.Info($"[StreamChat] 请求接口: {_chatUrl}");
            Logger.Info($"[StreamChat] 模型: {_model}");

            // 发送请求并处理流
            bool hadToolCall = false;
            string lastToolName = "";
            string lastToolCallId = "";
            var argumentsBuilder = new StringBuilder();

            try
            {
                await foreach (var chunk in ApiClient.StreamChatAsync(
                    _chatUrl, _apiKey, _model, history.Messages, ct, tools))
                {
                    switch (chunk.Type)
                    {
                        case StreamChunkType.Text:
                            if (!string.IsNullOrEmpty(chunk.Text))
                            {
                                fullText.Append(chunk.Text);
                                _dispatcher.Invoke(() => sis.UpdateText(chunk.Text));
                            }
                            break;

                        case StreamChunkType.ToolCall:
                            hadToolCall = true;
                            lastToolName = chunk.ToolName ?? "";
                            lastToolCallId = chunk.ToolCallId ?? "";
                            if (chunk.ToolArguments == "")  // 开始接收参数
                            {
                                argumentsBuilder.Clear();
                            }
                            else  // 参数接收完成
                            {
                                string argsJson = argumentsBuilder.Length > 0 ? argumentsBuilder.ToString() : chunk.ToolArguments ?? "{}";

                                var tool = _skillManager.GetToolByName(lastToolName);
                                string summary = tool?.Summary ?? lastToolName;
                                _dispatcher.Invoke(() =>
                                {
                                    CallMainSayRnd(main, $"正在调用「{summary}」工具...");
                                });

                                Logger.ToolCall(lastToolName, argsJson);

                                var result = await _toolExecutor.ExecuteAsync(lastToolName, argsJson);

                                string resultTypeLog = result.Count > 0 ? result[0].Type : "empty";
                                string resultTextLog = result.Count > 0 && result[0].Type == "text" ? result[0].Text ?? "" : $"[{result.Count} 个 content 项]";
                                Logger.ToolResult(lastToolName, resultTypeLog, resultTextLog);

                                history.AppendAssistantToolCall(new FunctionCallInfo
                                {
                                    Type = "function",
                                    Id = lastToolCallId,
                                    Name = lastToolName,
                                    Arguments = argsJson
                                });
                                history.AppendTool(lastToolCallId, result);

                                _historyStore.Save(history);
                                Logger.Info($"[Chat] 工具调用结果已保存到文件");

                                Logger.ToolAiContinue(history.Messages.Count);
                            }
                            break;

                        case StreamChunkType.Done:
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 取消异常向上抛，由调用方处理
                throw;
            }
            catch (Exception ex)
            {
                // AI 请求报错: 写入错误消息到历史并保存
                Logger.Error("[StreamChat] AI 请求异常，写入错误消息到历史", ex);
                history.AppendAssistant("AI响应错误");
                _historyStore.Save(history);
                Logger.Info("[Chat] 错误消息已写入历史文件");

                _dispatcher.Invoke(() => sis.UpdateText("AI响应错误"));
                _dispatcher.Invoke(() => sis.FinishGenerate());
                throw;
            }

            // 6. 判断是普通对话结束还是工具调用后需要继续
            if (hadToolCall)
            {
                // 工具调用完成,追加最终的 assistant 回复并继续循环
                string finalText = fullText.ToString();
                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    history.AppendAssistant(finalText);
                }

                _historyStore.Save(history);
                Logger.Info($"[Chat] 助手回复已保存到文件");

                fullText.Clear();
            }
            else
            {
                // 普通对话结束
                string finalText = fullText.ToString();
                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    history.AppendAssistant(finalText);
                }

                _historyStore.Save(history);
                Logger.Info($"[Chat] 对话结束，已保存到文件");

                Logger.Info($"[StreamChat] 接口正常输出: {finalText}");
                Logger.Info("对话完成,模型=" + _model);

                _dispatcher.Invoke(() => sis.FinishGenerate());
                return;
            }
        }
    }

    private static object? GetMain(IMainWindow mw)
    {
        var pi = mw.GetType().GetProperty("Main",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        return pi?.GetValue(mw);
    }

    private static string GetPetName(IMainWindow mw)
    {
        try
        {
            var saveProp = mw.GetType().GetProperty("GameSave",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase) ??
                mw.GetType().GetProperty("Save",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            var save = saveProp?.GetValue(mw);
            if (save != null)
            {
                var nameProp = save.GetType().GetProperty("Name",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (nameProp != null)
                {
                    var val = nameProp.GetValue(save);
                    if (val != null) return val.ToString() ?? "";
                }
            }
        }
        catch { }
        return "";
    }

    private static Dispatcher GetDispatcher(IMainWindow mw)
    {
        try
        {
            var dispProp = mw.GetType().GetProperty("Dispatcher",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (dispProp != null)
            {
                var val = dispProp.GetValue(mw);
                if (val is Dispatcher d) return d;
            }
        }
        catch { }
        // Fallback: try Application.Current.Dispatcher
        if (System.Windows.Application.Current != null)
            return System.Windows.Application.Current.Dispatcher;
        throw new InvalidOperationException("无法获取 Dispatcher");
    }

    private static void CallMainSayRnd(object main, string text)
    {
        main.GetType().InvokeMember("SayRnd",
            BindingFlags.InvokeMethod | BindingFlags.Instance | BindingFlags.Public,
            null, main,
            new object[] { text, false, "" });
    }

    // ============================================================
    //  文件拖拽处理
    // ============================================================
    // 注: 文件拖拽的事件订阅与处理已移至 PetChatPlugin 插件层(避免 ChatBridge
    //     每次 Responded 创建实例时重复订阅导致事件泄漏)。
    //     ChatBridge 仍保留 SendChatWithAttachmentsAsync 供插件层调用。

    /// <summary>
    /// 发送带附件的对话(支持附件-only,text 可为 null)。
    /// 流程: 读取文件 → 添加用户消息 → 保存文件 → 重新读取 → 发送请求
    /// </summary>
    public async Task SendChatWithAttachmentsAsync(
        string? text,
        List<ContentItem> attachments,
        SayInfoWithStream sis,
        CancellationToken ct)
    {
        var fullText = new StringBuilder();

        // 获取宠物核心
        object? main = null;
        _dispatcher.Invoke(() => { main = GetMain(_mw); });

        if (main == null)
        {
            _dispatcher.Invoke(() => sis.UpdateText("错误: 无法获取宠物核心"));
            return;
        }

        // 构建初始 user 消息
        string petName = GetPetName(_mw);
        string renderedPrompt = PromptRenderer.Render(_systemPrompt, petName);
        string gameData = _gameDataInjector.BuildGameDataBlock();

        // text 可为 null(仅附件发送); 若有 text 则拼接 gameData
        string? finalText = string.IsNullOrEmpty(text) ? null : (text + gameData);

        Logger.Info($"[ChatAttach] 发送带附件消息, text={(finalText == null ? "(空)" : finalText)}, 附件数: {attachments.Count}");
        foreach (var att in attachments)
        {
            Logger.Info($"[ChatAttach] 附件类型: {att.Type}");
        }

        // 读取文件历史 → 添加用户消息 → 保存文件
        var history = _historyStore.Load(_systemPrompt);
        history.AppendUser(finalText, attachments);
        _historyStore.Save(history);
        Logger.Info($"[ChatAttach] 用户消息已保存到文件");

        // 重新读取文件
        history = _historyStore.Load(_systemPrompt);

        // 开始对话循环
        await StreamAndProcessAsync(main, sis, ct, fullText, history);
    }

    private static void CallMainSayStream(object main, SayInfoWithStream sis)
    {
        main.GetType().InvokeMember("Say",
            BindingFlags.InvokeMethod | BindingFlags.Instance | BindingFlags.Public,
            null, main,
            new object[] { sis });
    }
}