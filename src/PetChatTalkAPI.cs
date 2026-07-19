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
using Vosk;
using NAudio.Wave;

namespace VPet.Plugin.PetChat;

public class PetChatTalkAPI : TalkBox
{
    public PetChatTalkAPI(PetChatPlugin plugin) : base(plugin)
    {
        Plugin = plugin;
        AddVoiceButton();
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

        if (string.IsNullOrWhiteSpace(Plugin.SavedApiKey) || string.IsNullOrWhiteSpace(Plugin.SavedModel))
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

                // 4. 获取暂存的附件
                var pendingAttachments = Plugin.GetPendingAttachments();

                // 5. 使用 ChatBridge 处理对话（含工具调用和附件）
                var chatBridge = new ChatBridge(
                    Plugin.SavedApiKey,
                    Plugin.SavedModel,
                    Plugin.SavedSystemPrompt,
                    Plugin.MW,
                    Plugin.ChatUrl,
                    Plugin.SkillManager,
                    Plugin.HistoryStore,
                    Plugin.SavedModelSupportsImage,
                    Plugin.SavedModelSupportsAudio,
                    Plugin.SavedModelSupportsVideo
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

    private Button? _btnVoice;
    private bool _isRecording;
    private Model? _voskModel;
    private WaveInEvent? _waveIn;
    private VoskRecognizer? _recognizer;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 在发送按钮右侧添加语音按钮。
    /// </summary>
    private void AddVoiceButton()
    {
        if (MainGrid == null) return;

        MainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        MainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _btnVoice = new Button
        {
            Content = "语音",
            BorderThickness = new Thickness(2),
            FontSize = 30,
            ToolTip = "点击开始语音输入",
        };

        if (btnSend?.Background != null)
            _btnVoice.Background = btnSend.Background;
        if (btnSend?.BorderBrush != null)
            _btnVoice.BorderBrush = btnSend.BorderBrush;
        if (btnSend?.Style != null)
            _btnVoice.Style = btnSend.Style;

        ButtonHelper.SetCornerRadius(_btnVoice, new CornerRadius(4));

        _btnVoice.Click += async (s, e) => await ToggleVoiceRecognition();

        Grid.SetColumn(_btnVoice, 4);
        Grid.SetRow(_btnVoice, 0);
        MainGrid.Children.Add(_btnVoice);
    }

    private async Task ToggleVoiceRecognition()
    {
        if (_isRecording)
        {
            await StopVoiceRecognition();
        }
        else
        {
            await StartVoiceRecognition();
        }
    }

    private async Task StartVoiceRecognition()
    {
        try
        {
            if (_btnVoice == null) return;

            _isRecording = true;
            _cts = new CancellationTokenSource();

            Dispatcher.Invoke(() =>
            {
                _btnVoice.Content = "停止";
                _btnVoice.ToolTip = "点击停止语音输入";
                if (tbTalk != null)
                    tbTalk.IsEnabled = false;
            });

            string modelPath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                "vosk-model-cn-0.22"
            );

            if (!Directory.Exists(modelPath))
            {
                Logger.Error($"[Voice] Vosk模型目录不存在: {modelPath}");
                await StopVoiceRecognition();
                return;
            }

            _voskModel = new Model(modelPath);
            _recognizer = new VoskRecognizer(_voskModel, 16000.0f);

            int deviceNumber = 0;
            if (!string.IsNullOrWhiteSpace(Plugin.SavedMicrophoneDevice) && int.TryParse(Plugin.SavedMicrophoneDevice, out int savedDeviceId))
            {
                deviceNumber = savedDeviceId;
            }

            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1)
            };

            Logger.Info($"[Voice] 使用麦克风设备: {deviceNumber}");

            _waveIn.DataAvailable += (s, e) =>
            {
                try
                {
                    if (_recognizer == null || _cts?.Token.IsCancellationRequested == true) return;

                    if (_recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
                    {
                        var result = _recognizer.Result();
                        var json = System.Text.Json.JsonDocument.Parse(result);
                        if (json.RootElement.TryGetProperty("text", out var textElem))
                        {
                            string recognizedText = textElem.GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(recognizedText))
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (tbTalk != null)
                                tbTalk.Text += recognizedText;
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("[Voice] 识别过程出错", ex);
                }
            };

            _waveIn.RecordingStopped += (s, e) =>
            {
                try
                {
                    if (_recognizer != null && !_cts?.Token.IsCancellationRequested == true)
                    {
                        var finalResult = _recognizer.FinalResult();
                        var json = System.Text.Json.JsonDocument.Parse(finalResult);
                        if (json.RootElement.TryGetProperty("text", out var textElem))
                        {
                            string recognizedText = textElem.GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(recognizedText))
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (tbTalk != null)
                                tbTalk.Text += recognizedText;
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("[Voice] 获取最终结果出错", ex);
                }
            };

            _waveIn.StartRecording();
            Logger.Info("[Voice] 语音识别已启动");
        }
        catch (Exception ex)
        {
            Logger.Error("[Voice] 启动语音识别失败", ex);
            await StopVoiceRecognition();
        }
    }

    private async Task StopVoiceRecognition()
    {
        try
        {
            _isRecording = false;
            _cts?.Cancel();

            if (_waveIn != null)
            {
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;
            }

            if (_recognizer != null)
            {
                _recognizer.Dispose();
                _recognizer = null;
            }

            if (_voskModel != null)
            {
                _voskModel.Dispose();
                _voskModel = null;
            }

            Dispatcher.Invoke(() =>
            {
                if (_btnVoice != null)
                {
                    _btnVoice.Content = "语音";
                    _btnVoice.ToolTip = "点击开始语音输入";
                }
                if (tbTalk != null)
                    tbTalk.IsEnabled = true;
            });

            Logger.Info("[Voice] 语音识别已停止");
        }
        catch (Exception ex)
        {
            Logger.Error("[Voice] 停止语音识别失败", ex);
        }
    }
}
