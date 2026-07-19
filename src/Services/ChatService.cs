using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VPet.Plugin.PetChat.Models;
using VPet_Simulator.Windows.Interface;
using VPet_Simulator.Core;

namespace VPet.Plugin.PetChat.Services;

public class ChatService
{
    private readonly ConversationHistory _history;
    private readonly GameDataInjector _gameData;
    private readonly IMainWindow _mw;

    public event Action<string>? OnTextReceived;
    public event Action<string>? OnError;

    public string Model { get; set; }
    public string ApiKey { get; set; }
    public string ChatUrl { get; set; }

    public ConversationHistory History => _history;

    public ChatService(string apiKey, string model, string systemPrompt, IMainWindow mw, string chatUrl)
    {
        ApiKey = apiKey ?? string.Empty;
        Model = model ?? string.Empty;
        ChatUrl = chatUrl ?? string.Empty;
        _history = new ConversationHistory(systemPrompt ?? string.Empty);
        _mw = mw;
        _gameData = new GameDataInjector(mw);
    }

    public void UpdateSystemPrompt(string newPrompt) =>
        _history.UpdateSystemPrompt(newPrompt ?? string.Empty);

    public async Task SendChatAsync(string userInput, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                OnError?.Invoke("错误: 未配置 API Key");
                return;
            }
            if (string.IsNullOrWhiteSpace(Model))
            {
                OnError?.Invoke("错误: 未选择模型");
                return;
            }
            if (string.IsNullOrWhiteSpace(ChatUrl))
            {
                OnError?.Invoke("错误: 未配置对话接口");
                return;
            }

            string currentSystem = GetSystemPromptText(_history);

            string gameData = _gameData.BuildGameDataBlock();
            Logger.Info($"[Chat] 用户输入: {userInput}");
            Logger.Info($"[Chat] 游戏数据整合后: {gameData}");
            _history.AppendUser((userInput ?? string.Empty) + gameData);

            OnTextReceived?.Invoke("思考中...");

            var fullText = new StringBuilder();
            await foreach (var chunk in ApiClient.StreamChatAsync(ChatUrl, ApiKey, Model, _history.Messages, ct))
            {
                if (chunk.Type == StreamChunkType.Text && !string.IsNullOrEmpty(chunk.Text))
                {
                    fullText.Append(chunk.Text);
                    OnTextReceived?.Invoke(fullText.ToString());
                }
            }

            string response = fullText.ToString();
            _history.AppendAssistant(response);

            if (!string.IsNullOrWhiteSpace(response) && _mw != null)
            {
                try
                {
                    var main = GetMainPet(_mw);
                    if (main != null)
                    {
                        main.GetType().InvokeMember("Say",
                            BindingFlags.InvokeMethod | BindingFlags.Instance | BindingFlags.Public,
                            null, main,
                            new object[] { response, string.Empty, true, string.Empty });
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("让宠物说话失败", ex);
                }
            }

            Logger.Info("对话完成,模型=" + Model);
        }
        catch (OperationCanceledException)
        {
            OnError?.Invoke("对话已取消");
        }
        catch (Exception ex)
        {
            Logger.Error("对话失败", ex);
            OnError?.Invoke("[错误] " + ex.Message);
            _history.AppendAssistant("[错误] " + ex.Message);
        }
    }

    public async Task SendChatWithStreamAsync(string userInput, SayInfoWithStream sis, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                sis.UpdateAllText("错误: 未配置 API Key");
                sis.FinishGenerate();
                return;
            }
            if (string.IsNullOrWhiteSpace(Model))
            {
                sis.UpdateAllText("错误: 未选择模型");
                sis.FinishGenerate();
                return;
            }
            if (string.IsNullOrWhiteSpace(ChatUrl))
            {
                sis.UpdateAllText("错误: 未配置对话接口");
                sis.FinishGenerate();
                return;
            }

            string currentSystem = GetSystemPromptText(_history);

            string gameData = _gameData.BuildGameDataBlock();
            Logger.Info($"[Chat] 用户输入: {userInput}");
            Logger.Info($"[Chat] 游戏数据整合后: {gameData}");

            _history.AppendUser((userInput ?? string.Empty) + gameData);

            var fullText = new StringBuilder();
            await foreach (var chunk in ApiClient.StreamChatAsync(ChatUrl, ApiKey, Model, _history.Messages, ct))
            {
                if (chunk.Type == StreamChunkType.Text && !string.IsNullOrEmpty(chunk.Text))
                {
                    fullText.Append(chunk.Text);
                    sis.UpdateText(chunk.Text);
                }
            }

            string response = fullText.ToString();
            _history.AppendAssistant(response);
            Logger.Info("流式对话完成,模型=" + Model);
        }
        catch (OperationCanceledException)
        {
            sis.UpdateAllText("对话已取消");
            sis.FinishGenerate();
        }
        catch (Exception ex)
        {
            Logger.Error("流式对话失败", ex);
            sis.UpdateAllText("API调用失败: " + ex.Message);
            sis.FinishGenerate();
            _history.AppendAssistant("[错误] " + ex.Message);
        }
    }

    private static string GetSystemPromptText(ConversationHistory history)
    {
        if (history.Messages.Count == 0) return string.Empty;
        var firstMsg = history.Messages[0];
        if (firstMsg.Content == null || firstMsg.Content.Count == 0) return string.Empty;
        foreach (var item in firstMsg.Content)
        {
            if (item.Type == "text" && !string.IsNullOrEmpty(item.Text))
                return item.Text!;
        }
        return string.Empty;
    }

    private static object? GetMainPet(IMainWindow mw)
    {
        try
        {
            var mainProp = mw.GetType().GetProperty("Main",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (mainProp != null)
                return mainProp.GetValue(mw);
        }
        catch
        {
        }
        return null;
    }
}
