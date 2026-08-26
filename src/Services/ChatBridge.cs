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
    private readonly bool _useBearerAuth;

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
        bool supportsVideoInput = false,
        bool useBearerAuth = false)
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
        _useBearerAuth = useBearerAuth;
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
            bool inToolCallMode = false;   // 一旦检测到工具调用,不再接受 Text chunk(避免AI把工具调用当文本输出)
            // 记录每个 callId 对应的工具名(从"开始"chunk 获取),用于"完成"chunk 时 fallback
            // 解决 DeepSeek 流式响应中 function.name 可能只在首个 chunk 给出、后续 chunk 及完成 chunk 时为空的问题
            var toolCallNames = new Dictionary<string, string>();

            try
            {
                await foreach (var chunk in ApiClient.StreamChatAsync(
                    _chatUrl, _apiKey, _model, history.Messages, ct, tools, _useBearerAuth))
                {
                    switch (chunk.Type)
                    {
                        case StreamChunkType.Text:
                            // 进入工具调用模式后,不再接收 Text chunk 到流式显示,
                            // 但仍要累积到 fullText(作为 assistant tool_call 的 content 保留)
                            if (!string.IsNullOrEmpty(chunk.Text))
                            {
                                fullText.Append(chunk.Text);
                                if (!inToolCallMode)
                                {
                                    _dispatcher.Invoke(() => sis.UpdateText(chunk.Text));
                                }
                            }
                            break;

                        case StreamChunkType.ToolCall:
                            hadToolCall = true;
                            inToolCallMode = true;

                            // ============================================================
                            //  工具调用"开始"提示(仅用于 UI 反馈,不执行工具)
                            //  操作: 立刻清空调用前已输出的文本气泡 → 替换为工具调用状态
                            //  同时记录 callId → 工具名 映射,供"完成"chunk fallback 使用
                            // ============================================================
                            if (!chunk.IsToolCallComplete)
                            {
                                string startName = chunk.ToolName ?? "";
                                string startId = chunk.ToolCallId ?? "";
                                // 记录映射,供完成 chunk 时 fallback
                                if (!string.IsNullOrEmpty(startId) && !string.IsNullOrEmpty(startName))
                                {
                                    toolCallNames[startId] = startName;
                                }

                                var toolHint = _skillManager.GetToolByName(startName);
                                string summaryHint = toolHint?.Summary ?? (string.IsNullOrEmpty(startName) ? "工具" : startName);
                                string statusText = $"正在调用「{summaryHint}」工具...";
                                Logger.Info($"[Chat] 检测到工具调用开始: name={startName}, id={startId},清空当前气泡并显示状态");
                                _dispatcher.Invoke(() =>
                                {
                                    sis.UpdateAllText(statusText);
                                });
                                break;
                            }

                            // ============================================================
                            //  工具调用"完成": 真正执行工具 → 等待结果 → 写入历史
                            //  注意: 不再重复 sis.UpdateAllText(开始chunk已显示过),避免闪烁
                            // ============================================================
                            {
                                string callId = chunk.ToolCallId ?? "";
                                // 工具名 fallback: 优先用完成 chunk 的 ToolName,为空则查开始 chunk 记录的映射
                                string callName = chunk.ToolName ?? "";
                                if (string.IsNullOrEmpty(callName) && !string.IsNullOrEmpty(callId) && toolCallNames.TryGetValue(callId, out var fallbackName))
                                {
                                    callName = fallbackName;
                                    Logger.Info($"[Chat] 完成chunk工具名为空,从开始chunk记录恢复: {callName}");
                                }

                                string argsJson = string.IsNullOrEmpty(chunk.ToolArguments) ? "{}" : chunk.ToolArguments!;

                                if (string.IsNullOrEmpty(callName))
                                {
                                    Logger.Error($"[Chat] 工具调用完成但工具名为空(id={callId}),参数: {argsJson},跳过执行");
                                    // 写入失败结果到历史,让AI知道工具调用失败
                                    history.AppendAssistantToolCall(new ToolCallInfo
                                    {
                                        Id = callId,
                                        Type = "function",
                                        Function = new ToolCallFunction { Name = "", Arguments = argsJson }
                                    }, null);
                                    history.AppendTool(callId, "工具调用失败:工具名为空");
                                    _historyStore.Save(history);
                                    continue;
                                }

                                Logger.Info($"[Chat] 开始执行工具: {callName}, id={callId}, 参数长度={argsJson.Length}, 等待返回...");
                                Logger.ToolCall(callName, argsJson);

                                // 等待工具执行完成(await)再继续
                                var result = await _toolExecutor.ExecuteAsync(callName, argsJson);

                                string resultTypeLog = result.Count > 0 ? result[0].Type : "empty";
                                string resultTextLog = result.Count > 0 && result[0].Type == "text" ? result[0].Text ?? "" : $"[{result.Count} 个 content 项]";
                                Logger.ToolResult(callName, resultTypeLog, resultTextLog);
                                Logger.Info($"[Chat] 工具执行完成: {callName},结果类型: {resultTypeLog}");

                                // 将工具调用前累积的文本作为 assistant 消息的 content,
                                // 避免在 tool 消息之后追加多余的 assistant 文本消息导致 API 400 错误
                                List<Models.ContentItem>? toolCallContent = null;
                                string textBeforeToolCall = fullText.ToString().Trim();
                                if (!string.IsNullOrEmpty(textBeforeToolCall))
                                {
                                    toolCallContent = new List<Models.ContentItem>
                                    {
                                        new Models.ContentItem { Type = "text", Text = textBeforeToolCall }
                                    };
                                    fullText.Clear();
                                    Logger.Info($"[Chat] 工具调用前的文本已合并到 assistant tool_call 消息: {textBeforeToolCall}");
                                }

                                // 标准 OpenAI 格式: tool_calls 数组,每项为 { id, type, function: { name, arguments } }
                                history.AppendAssistantToolCall(new ToolCallInfo
                                {
                                    Id = callId,
                                    Type = "function",
                                    Function = new ToolCallFunction
                                    {
                                        Name = callName,
                                        Arguments = argsJson
                                    }
                                }, toolCallContent);
                                history.AppendTool(callId, result);

                                _historyStore.Save(history);
                                Logger.Info($"[Chat] 工具调用结果已保存到文件,准备回传给AI");

                                // 清空当前气泡,等待下一轮AI回复输出
                                _dispatcher.Invoke(() =>
                                {
                                    sis.UpdateAllText(string.Empty);
                                });

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

                _dispatcher.Invoke(() => sis.UpdateAllText("AI响应错误"));
                _dispatcher.Invoke(() => sis.FinishGenerate());
                throw;
            }

            // 6. 判断是普通对话结束还是工具调用后需要继续
            if (hadToolCall)
            {
                // 工具调用完成,工具调用前的文本已合并到 assistant tool_call 消息中。
                // 正常情况下(full_reason="tool_calls")工具调用后不会再有文本,
                // 无需追加多余的 assistant 文本消息(否则会导致API 400错误)。
                _historyStore.Save(history);
                Logger.Info($"[Chat] 工具调用轮次已保存到文件,继续循环等待AI回复");

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