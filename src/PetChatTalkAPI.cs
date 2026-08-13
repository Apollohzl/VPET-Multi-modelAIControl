using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Panuon.WPF.UI;
using VPet.Plugin.PetChat.Models;
using VPet.Plugin.PetChat.Services;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.PetChat;

public class PetChatTalkAPI : TalkBox
{
    public PetChatTalkAPI(PetChatPlugin plugin) : base(plugin)
    {
        Plugin = plugin;
    }

    protected PetChatPlugin Plugin;

    public override string APIName => "智能多模型对话";

    private static object? GetMain(object? mw)
    {
        if (mw == null) return null;
        try
        {
            var pi = mw.GetType().GetProperty("Main",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            return pi?.GetValue(mw);
        }
        catch
        {
            return null;
        }
    }

    private static void CallMainSayStream(object main, SayInfoWithStream sis)
    {
        main.GetType().InvokeMember("Say",
            BindingFlags.InvokeMethod | BindingFlags.Instance | BindingFlags.Public,
            null, main,
            new object[] { sis });
    }

    private static void CallMainSayRnd(object main, string text)
    {
        main.GetType().InvokeMember("SayRnd",
            BindingFlags.InvokeMethod | BindingFlags.Instance | BindingFlags.Public,
            null, main,
            new object[] { text, false, string.Empty });
    }

    public override void Responded(string content)
    {
        if (string.IsNullOrWhiteSpace(content) && !Plugin.HasPendingAttachments)
            return;

        // 根据供应商选择对应的 API Key 和模型
        bool isDeepseek = Plugin.IsDeepseekProvider;
        bool isFileRestricted = isDeepseek;

        string activeApiKey, activeModel, activeChatUrl;
        if (isDeepseek)
        {
            activeApiKey = Plugin.SavedDeepseekApiKey;
            activeModel = Plugin.SavedDeepseekModel;
            activeChatUrl = Plugin.DeepseekChatUrl;
        }
        else
        {
            activeApiKey = Plugin.SavedApiKey;
            activeModel = Plugin.SavedModel;
            activeChatUrl = Plugin.ChatUrl;
        }

        if (string.IsNullOrWhiteSpace(activeApiKey) || string.IsNullOrWhiteSpace(activeModel))
        {
            Dispatcher.Invoke(() =>
            {
                var main = GetMain(Plugin.MW);
                if (main != null)
                    CallMainSayRnd(main, "请先在设置中配置 API Key 和模型");
                else
                    DisplayThinkToSayRnd("请先在设置中配置 API Key 和模型");
            });
            return;
        }

        Dispatcher.Invoke(() => this.IsEnabled = false);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Task.Run(async () =>
        {
            try
            {
                // 1. 获取宠物核心
                object? main = null;
                Dispatcher.Invoke(() =>
                {
                    main = GetMain(Plugin.MW);
                });

                if (main == null)
                {
                    throw new InvalidOperationException("无法获取宠物核心 (Main)");
                }

                // 2. 创建流式说话信息
                var sis = new SayInfoWithStream();

                // 3. 调用 main.Say(sis) —— 告诉游戏开始流式显示（必须在 UI 线程）
                Dispatcher.Invoke(() =>
                {
                    CallMainSayStream(main, sis);
                });

                // 4. 获取暂存的附件(文件限制供应商不支持附件)
                var pendingAttachments = isFileRestricted
                    ? new System.Collections.Generic.List<Models.ContentItem>()
                    : Plugin.GetPendingAttachments();

                // 5. 使用 ChatBridge 处理对话（含工具调用和附件）
                //    文件限制供应商使用 Bearer Token 认证,且不支持任何文件输入
                var chatBridge = new ChatBridge(
                    activeApiKey,
                    activeModel,
                    Plugin.SavedSystemPrompt,
                    Plugin.MW,
                    activeChatUrl,
                    Plugin.SkillManager,
                    Plugin.HistoryStore,
                    isFileRestricted ? false : Plugin.SavedModelSupportsImage,
                    isFileRestricted ? false : Plugin.SavedModelSupportsAudio,
                    isFileRestricted ? false : Plugin.SavedModelSupportsVideo,
                    isFileRestricted
                );

                if (pendingAttachments.Count > 0)
                {
                    // 有附件时使用带附件的发送方法
                    await chatBridge.SendChatWithAttachmentsAsync(content, pendingAttachments, sis, cts.Token);
                    Logger.Info($"[Chat] 发送带 {pendingAttachments.Count} 个附件的消息");
                }
                else
                {
                    // 无附件时使用普通发送方法
                    await chatBridge.SendChatAsync(content ?? string.Empty, sis, cts.Token);
                }

                Logger.Info($"[Chat] 对话完成, 供应商={(isDeepseek ? "Deepseek" : "pollenations")}, 模型={activeModel}");
                // ChatBridge 已在对话过程中完成保存，无需重复保存
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    var main = GetMain(Plugin.MW);
                    if (main != null)
                        CallMainSayRnd(main, "请求超时，请检查网络或 API 配置");
                    else
                        DisplayThinkToSayRnd("请求超时，请检查网络或 API 配置");
                });
            }
            catch (Exception ex)
            {
                Logger.Error("流式对话失败", ex);
                Dispatcher.Invoke(() =>
                {
                    var main = GetMain(Plugin.MW);
                    if (main != null)
                        CallMainSayRnd(main, "API调用失败: " + ex.Message);
                    else
                        DisplayThinkToSayRnd("API调用失败: " + ex.Message);
                });
            }
            finally
            {
                Dispatcher.Invoke(() => this.IsEnabled = true);
            }
        });
    }

    private static string MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length <= 8) return "****";
        return key.Substring(0, 4) + "****" + key.Substring(key.Length - 4);
    }

    private static string FormatHistoryForLog(List<ChatMessage> messages)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(messages,
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            return json.Length > 3000 ? json.Substring(0, 3000) + "...(已截断)" : json;
        }
        catch
        {
            return $"{messages.Count} 条消息（序列化失败）";
        }
    }

    public override void Setting() => Plugin.OpenSettingWindow();

}
