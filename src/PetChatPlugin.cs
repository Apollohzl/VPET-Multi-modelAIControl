using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using VPet.Plugin.PetChat.Services;
using VPet_Simulator.Windows.Interface;
using VPet_Simulator.Core;

namespace VPet.Plugin.PetChat;

public class PetChatPlugin : MainPlugin
{
    private string _modelsUrl = string.Empty;
    public string ChatUrl { get; private set; } = string.Empty;
    private string _balanceUrl = string.Empty;
    private bool _apiKeyInBody = false;

    // Deepseek 官方端点
    public string DeepseekChatUrl => Models.DeepseekEndpoints.ChatUrl;

    public string SavedProvider { get; private set; } = Models.ProviderNames.Pollenations;
    public string SavedApiKey { get; private set; } = string.Empty;
    public string SavedModel { get; private set; } = string.Empty;
    public string SavedDeepseekApiKey { get; private set; } = string.Empty;
    public string SavedDeepseekModel { get; private set; } = string.Empty;
    public bool SavedModelSupportsImage { get; private set; } = true;
    public bool SavedModelSupportsAudio { get; private set; } = false;
    public bool SavedModelSupportsVideo { get; private set; } = false;
    public string SavedSystemPrompt { get; private set; } = string.Empty;
    public string SavedLogDir { get; private set; } = string.Empty;

    /// <summary>配置文件路径(plugin_settings.json)</summary>
    private string _settingsPath = string.Empty;

    /// <summary>当前是否使用 Deepseek 供应商</summary>
    public bool IsDeepseekProvider => string.Equals(SavedProvider, Models.ProviderNames.Deepseek, StringComparison.OrdinalIgnoreCase);

    /// <summary>当前供应商是否限制文件上传（Deepseek 不支持）</summary>
    public bool IsFileRestrictedProvider => IsDeepseekProvider;

    // 文件拖拽服务(单例,绑定到主窗口)
    public Services.FileDropService FileDropService { get; private set; } = null!;

    // 暂存的文件拖拽附件(等待用户发送消息时一起发送)
    private List<Models.ContentItem>? _pendingAttachments;
    private readonly object _attachmentsLock = new object();
    public List<Models.ContentItem> GetPendingAttachments()
    {
        lock (_attachmentsLock)
        {
            var result = _pendingAttachments ?? new List<Models.ContentItem>();
            _pendingAttachments = null;
            return result;
        }
    }
    public void SetPendingAttachments(List<Models.ContentItem> attachments)
    {
        lock (_attachmentsLock)
        {
            _pendingAttachments = attachments;
        }
    }
    public bool HasPendingAttachments
    {
        get { lock (_attachmentsLock) { return _pendingAttachments != null && _pendingAttachments.Count > 0; } }
    }

    // 共享对话历史（整个插件生命周期内唯一）
    private ChatHistoryStore _historyStore = null!;
    public ChatHistoryStore HistoryStore => _historyStore;
    public ConversationHistory History { get; private set; } = null!;

    // 工具管理器
    public SkillManager SkillManager { get; private set; } = null!;

    public IMainWindow MainWindow => MW;

    public override string PluginName => "智能多模型对话";

    private const string DefaultSystemPrompt = "请使用简短的、口语化的、可爱的、女性化的、调皮的语言风格和我交流。你是一只电脑桌宠，你的名字叫{petname}，你喜欢玩虚拟主播模拟器。你需要尝试体会我与你对话中的感情、产生自己的情感并尽可能给出可爱的回答，每次我将会提供给你我的询问问题以及目前你的状态，结合这些数据给出可爱的回答，并且你不能承认你是一个AI机器人，称我为主人，用简短的语言（15 个字左右，必要时增加）并且使用纯文本（禁止 md 语法）。";

    public PetChatPlugin(IMainWindow mainwin) : base(mainwin)
    {
    }

    public override void LoadPlugin()
    {
        try
        {
            var endpoints = GetEndpointsLps();

            _modelsUrl = endpoints.TryGetValue("models_url", out var mu) ? mu :
                       (endpoints.TryGetValue("models", out var m) ? m : "https://text.pollinations.ai/models");
            ChatUrl = endpoints.TryGetValue("chat_url", out var cu) ? cu :
                      (endpoints.TryGetValue("chat", out var c) ? c : "https://text.pollinations.ai/");
            // 余额查询接口：优先使用配置文件 endpoints.lps 中的 balance_url
            // （与 chat/models 一致指向网关，例如 hzliflow.ken520.top），缺失时回退到 pollinations 官方地址
            _balanceUrl = endpoints.TryGetValue("balance_url", out var bu) && !string.IsNullOrWhiteSpace(bu)
                ? bu
                : "https://gen.pollinations.ai/account/balance";
            _apiKeyInBody = endpoints.TryGetValue("api_key_in_body", out var aib) && aib.Equals("true", StringComparison.OrdinalIgnoreCase);

            // 使用 JSON 配置文件（先检测并迁移旧版 LPS 配置）
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
            Models.PluginSettings.MigrateFromLps(pluginDir);
            _settingsPath = Models.PluginSettings.GetDefaultPath(pluginDir);
            var settings = Models.PluginSettings.Load(_settingsPath);

            SavedProvider = string.IsNullOrEmpty(settings.provider) ? Models.ProviderNames.Pollenations : settings.provider;
            SavedApiKey = settings.api_key ?? string.Empty;
            SavedModel = settings.model ?? string.Empty;
            SavedDeepseekApiKey = settings.deepseek_api_key ?? string.Empty;
            SavedDeepseekModel = settings.deepseek_model ?? string.Empty;
            SavedModelSupportsImage = settings.model_supports_image;
            SavedModelSupportsAudio = settings.model_supports_audio;
            SavedModelSupportsVideo = settings.model_supports_video;
            SavedSystemPrompt = !string.IsNullOrWhiteSpace(settings.system_prompt) ? settings.system_prompt : DefaultSystemPrompt;
            SavedLogDir = !string.IsNullOrWhiteSpace(settings.log_dir) ? settings.log_dir : string.Empty;
            if (!string.IsNullOrWhiteSpace(SavedLogDir))
                Logger.LogDirectory = SavedLogDir;

            // 加载对话历史
            _historyStore = new ChatHistoryStore(ChatHistoryStore.GetDefaultPath());
            History = _historyStore.Load(SavedSystemPrompt);

            // 检测上次关闭时是否有未完成的命令执行
            CheckUnfinishedCommands();

            // 初始化工具管理器
            SkillManager = new SkillManager(pluginDir);
            SkillManager.LoadSkills();
            // 从 JSON 配置加载已启用的工具
            SkillManager.LoadEnabledSkills(settings);

            // 初始化文件拖拽服务并附加到主窗口
            FileDropService = new Services.FileDropService();
            try
            {
                if (System.Windows.Application.Current?.MainWindow != null)
                    FileDropService.Attach(System.Windows.Application.Current.MainWindow);
            }
            catch (Exception ex)
            {
                Logger.Error("FileDropService 初始化失败", ex);
            }

            // 订阅文件拖拽事件(单一订阅,避免泄漏)
            FileDropService.FileDropped += OnFileDropped;

            // 注册聊天 API
            MW.TalkAPI.Add(new PetChatTalkAPI(this));

            // 添加工具栏菜单项
            var menuItem = new MenuItem
            {
                Header = "智能多模型对话",
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            menuItem.Click += (s, e) => Setting();
            MW.Main.ToolBar.MenuMODConfig.Items.Add(menuItem);

            Logger.Info($"PetChatPlugin LoadPlugin: model={SavedModel}, chat_url={ChatUrl}, 历史轮数={(History.Messages.Count - 1) / 2}");
        }
        catch (Exception ex)
        {
            Logger.Error("PetChatPlugin 初始化失败", ex);
        }
    }

    public override void GameLoaded()
    {
        Logger.Info("PetChatPlugin GameLoaded 完成");
    }

    public override void EndGame()
    {
        try
        {
            SaveChatHistory();
            Logger.Info("PetChatPlugin EndGame: 已保存对话历史");
        }
        catch
        {
        }
    }

    public override void Save()
    {
        try
        {
            SaveChatHistory();

            // 保存工具启用状态到 JSON 配置
            var settings = Models.PluginSettings.Load(_settingsPath);
            SkillManager?.SaveEnabledSkills(settings);
            settings.Save(_settingsPath);
        }
        catch
        {
        }
    }

    /// <summary>立即保存对话历史（供 UI 或对话结束时显式调用）。</summary>
    public void SaveChatHistory()
    {
        try
        {
            // 从文件重新读取最新内容再保存，确保文件内容是最新的
            // 因为 ChatBridge 已经在对话过程中保存了文件，这里只需同步内存中的 History 对象
            if (_historyStore != null)
            {
                History = _historyStore.Load(SavedSystemPrompt);
                _historyStore.Save(History);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("保存对话历史失败", ex);
        }
    }

    /// <summary>
    /// 文件拖拽事件处理: 播放动画(失败跳过),处理文件并暂存附件(等待用户发送消息时一起发送)。
    /// 文件限制供应商不支持文件拖拽上传,会直接忽略。
    /// </summary>
    private async void OnFileDropped(string[] filePaths)
    {
        // 文件限制供应商（Deepseek）不支持文件拖拽上传
        if (IsFileRestrictedProvider)
        {
            Logger.Info($"[FileDrop] 当前供应商不支持文件拖拽上传,已忽略");
            return;
        }
        try
        {
            Logger.Info($"[FileDrop] 收到 {filePaths.Length} 个文件: {string.Join(", ", filePaths)}");

            // 1. 尝试播放动画(失败则跳过,不影响后续流程)
            TryPlayEatAnimation();

            // 2. 获取宠物核心
            object? main = null;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                Logger.Info("[FileDrop] 无法获取 Dispatcher,放弃处理");
                return;
            }
            dispatcher.Invoke(() =>
            {
                var pi = MW.GetType().GetProperty("Main",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase);
                main = pi?.GetValue(MW);
            });

            if (main == null)
            {
                Logger.Info("[FileDrop] 无法获取宠物核心,放弃处理");
                return;
            }

            // 3. 处理每个文件(显示"文件路径+上传中")
            var attachments = new System.Collections.Generic.List<Models.ContentItem>();
            var failedFiles = new System.Collections.Generic.List<string>();
            Logger.Info($"[FileDrop] 模型能力: 图片={SavedModelSupportsImage}, 音频={SavedModelSupportsAudio}, 视频={SavedModelSupportsVideo}");
            Logger.Info($"[FileDrop] API Key: {(string.IsNullOrEmpty(SavedApiKey) ? "空" : "已配置")}");

            foreach (var filePath in filePaths)
            {
                Logger.Info($"[FileDrop] 正在处理文件: {filePath}");

                // 显示"文件路径+上传中"
                dispatcher.Invoke(() =>
                {
                    main.GetType().InvokeMember("SayRnd",
                        System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                        null, main,
                        new object[] { $"{filePath} 上传中...", false, "" });
                });

                var attachment = await Services.FileProcessor.ProcessAsync(
                    filePath, SavedModelSupportsImage, SavedModelSupportsAudio, SavedModelSupportsVideo, SavedApiKey);
                if (attachment != null)
                {
                    attachments.Add(attachment);
                    Logger.Info($"[FileDrop] 文件处理成功: {filePath} -> {attachment.Type}");
                }
                else
                {
                    failedFiles.Add(filePath);
                    Logger.Info($"[FileDrop] 文件处理失败: {filePath}");
                }
            }

            if (attachments.Count == 0)
            {
                string reason = "未知原因";
                if (failedFiles.Count > 0)
                {
                    reason = $"文件: {string.Join(", ", failedFiles)}";
                }
                Logger.Info($"[FileDrop] 没有文件被成功处理. 失败文件: {string.Join(", ", failedFiles)}");
                dispatcher.Invoke(() =>
                {
                    main.GetType().InvokeMember("SayRnd",
                        System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                        null, main,
                        new object[] { $"这些文件我都没法处理呢~ ({reason})", false, "" });
                });
                return;
            }

            // 4. 暂存附件,等待用户发送消息时一起发送(不自动发送)
            SetPendingAttachments(attachments);
            Logger.Info($"[FileDrop] 成功处理 {attachments.Count} 个文件,已暂存等待发送");

            // 提示用户
            dispatcher.Invoke(() =>
            {
                main.GetType().InvokeMember("SayRnd",
                    System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                    null, main,
                    new object[] { $"已收到 {attachments.Count} 个文件，请输入消息后发送~", false, "" });
            });

            // 持久化保存
            SaveChatHistory();
        }
        catch (Exception ex)
        {
            Logger.Error("[FileDrop] 处理文件拖拽异常", ex);
        }
    }

    /// <summary>
    /// 尝试播放 eat_back_lay_2 动画(Single 模式),播放完成后切换 DisplayNomaldo。
    /// 所有异常都捕获,失败时仅记录日志,不影响主流程。
    /// </summary>
    private void TryPlayEatAnimation()
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            object? main = null;
            dispatcher.Invoke(() =>
            {
                var pi = MW.GetType().GetProperty("Main",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase);
                main = pi?.GetValue(MW);
            });
            if (main == null) return;

            var mainType = main.GetType();

            // 尝试调用 DisplayGraph(string) 播放 eat_back_lay_2
            try
            {
                mainType.InvokeMember("DisplayGraph",
                    System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                    null, main,
                    new object[] { "eat_back_lay_2" });
                Logger.Info("[FileDrop] 动画 eat_back_lay_2 播放成功");
            }
            catch (Exception ex1)
            {
                Logger.Info($"[FileDrop] 调用 DisplayGraph 失败: {ex1.Message}");
                // 二次尝试: 调用 DisplayNomal
                try
                {
                    mainType.InvokeMember("DisplayNomal",
                        System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                        null, main,
                        new object[] { "eat_back_lay_2" });
                    Logger.Info("[FileDrop] 动画 DisplayNomal(eat_back_lay_2) 播放成功");
                }
                catch (Exception ex2)
                {
                    Logger.Info($"[FileDrop] 调用 DisplayNomal 失败: {ex2.Message}");
                }
            }

            // 切换回 DisplayNomal(空字符串)以恢复正常状态
            try
            {
                mainType.InvokeMember("DisplayNomal",
                    System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                    null, main,
                    new object[] { "" });
            }
            catch
            {
                // 切换失败忽略
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"[FileDrop] 动画播放整体失败(忽略,继续处理文件): {ex.Message}");
        }
    }

    /// <summary>清空对话历史（重置为仅含系统提示）。</summary>
    public void ClearChatHistory()
    {
        try
        {
            if (_historyStore != null)
                History = _historyStore.Clear(SavedSystemPrompt);
            else
                History = new ConversationHistory(SavedSystemPrompt);
            Logger.Info("对话历史已清空");
        }
        catch (Exception ex)
        {
            Logger.Error("清空对话历史失败", ex);
        }
    }

    /// <summary>返回历史文件路径，便于设置页面展示。</summary>
    public string HistoryFilePath => _historyStore?.FilePath ?? ChatHistoryStore.GetDefaultPath();

    /// <summary>返回当前历史中的对话轮数（不含 system 提示）。</summary>
    public int CurrentHistoryTurns
    {
        get
        {
            if (History == null) return 0;
            return (History.Messages.Count - 1) / 2;
        }
    }

    /// <summary>检测上次关闭时是否有未完成的命令执行。</summary>
    private void CheckUnfinishedCommands()
    {
        try
        {
            string trackingFile = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                "running_commands.tmp"
            );

            if (!File.Exists(trackingFile)) return;

            var lines = File.ReadAllLines(trackingFile).ToList();
            bool hasUnfinished = false;

            foreach (var line in lines)
            {
                var parts = line.Split('|');
                if (parts.Length >= 1 && int.TryParse(parts[0], out int pid))
                {
                    try
                    {
                        var process = Process.GetProcessById(pid);
                        if (!process.HasExited)
                            hasUnfinished = true;
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }

            if (hasUnfinished)
            {
                History.AppendAssistant("用户强行关闭游戏，执行结果未知，请自行询问用户");
                _historyStore.Save(History);
                Logger.Info("[Command] 检测到未完成的命令执行，已记录到历史");
            }

            File.Delete(trackingFile);
        }
        catch (Exception ex)
        {
            Logger.Error("检测未完成命令失败", ex);
        }
    }

    /// <summary>返回 Chat_Historys 文件夹路径。</summary>
    public string GetChatHistorysDir()
    {
        string pluginDir = Path.GetDirectoryName(HistoryFilePath) ?? Directory.GetCurrentDirectory();
        return Path.Combine(pluginDir, "Chat_Historys");
    }

    /// <summary>
    /// 将当前 chat_history.json 归档到 Chat_Historys 文件夹。
    /// 文件命名为时间戳格式 yyyy-MM-dd HH_mm_ss.json。
    /// 返回归档文件名（不含扩展名）；如果当前历史为空则返回 null。
    /// </summary>
    public string? ArchiveChatHistory()
    {
        try
        {
            string currentFile = HistoryFilePath;
            if (!File.Exists(currentFile)) return null;

            string content = File.ReadAllText(currentFile, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(content) || content.Trim() == "[]") return null;

            string archiveDir = GetChatHistorysDir();
            Directory.CreateDirectory(archiveDir);

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH_mm_ss");
            string archivePath = Path.Combine(archiveDir, timestamp + ".json");

            File.Copy(currentFile, archivePath, true);
            Logger.Info($"[History] 对话历史已归档到: {archivePath}");

            // 清空当前历史
            ClearChatHistory();
            return timestamp;
        }
        catch (Exception ex)
        {
            Logger.Error("归档对话历史失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 切换对话历史：先归档当前历史，然后将选中的归档文件移出并重命名为 chat_history.json。
    /// </summary>
    public bool SwitchChatHistory(string archiveFileName)
    {
        try
        {
            string archiveDir = GetChatHistorysDir();
            string archivePath = Path.Combine(archiveDir, archiveFileName + ".json");

            if (!File.Exists(archivePath))
            {
                Logger.Error($"[History] 归档文件不存在: {archivePath}");
                return false;
            }

            // 先归档当前历史
            ArchiveChatHistory();

            // 将选中的归档文件移出并重命名为 chat_history.json
            string currentFile = HistoryFilePath;
            File.Move(archivePath, currentFile, true);
            Logger.Info($"[History] 已切换到历史: {archiveFileName}");

            // 重新加载到内存
            History = _historyStore.Load(SavedSystemPrompt);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("切换对话历史失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 读取 Chat_Historys 文件夹中所有 json 文件名（不含扩展名），按名称降序排列。
    /// </summary>
    public List<string> GetArchivedHistoryList()
    {
        var result = new List<string>();
        try
        {
            string archiveDir = GetChatHistorysDir();
            if (!Directory.Exists(archiveDir)) return result;

            var files = Directory.GetFiles(archiveDir, "*.json");
            foreach (var file in files)
            {
                result.Add(Path.GetFileNameWithoutExtension(file));
            }
            result.Sort((a, b) => string.Compare(b, a, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Logger.Error("读取归档历史列表失败", ex);
        }
        return result;
    }

    public override void Setting()
    {
        OpenSettingWindow();
    }

    public void OpenSettingWindow()
    {
        try
        {
            var window = new Window
            {
                Title = "智能多模型对话 - 设置",
                Width = 600,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new UI.PetChatSettingPage(
                    this, MW, _modelsUrl, ChatUrl, _balanceUrl, _apiKeyInBody,
                    SavedProvider, SavedApiKey,
                    SavedModel, SavedDeepseekModel,
                    SavedSystemPrompt,
                    SavedLogDir, Logger.LogDirectory,
                    SavedDeepseekApiKey)
            };
            window.ShowDialog();

            // 刷新设置
            ReloadSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开设置失败: " + ex.Message);
            Logger.Error("打开设置失败", ex);
        }
    }

    /// <summary>配置文件路径,供设置页面读写 JSON 配置使用。</summary>
    public string SettingsPath => _settingsPath;

    private void ReloadSettings()
    {
        try
        {
            var settings = Models.PluginSettings.Load(_settingsPath);
            SavedProvider = string.IsNullOrEmpty(settings.provider) ? Models.ProviderNames.Pollenations : settings.provider;
            SavedApiKey = settings.api_key ?? string.Empty;
            SavedModel = settings.model ?? string.Empty;
            SavedDeepseekApiKey = settings.deepseek_api_key ?? string.Empty;
            SavedDeepseekModel = settings.deepseek_model ?? string.Empty;
            SavedModelSupportsImage = settings.model_supports_image;
            SavedModelSupportsAudio = settings.model_supports_audio;
            SavedModelSupportsVideo = settings.model_supports_video;
            SavedSystemPrompt = !string.IsNullOrWhiteSpace(settings.system_prompt) ? settings.system_prompt : DefaultSystemPrompt;
            SavedLogDir = !string.IsNullOrWhiteSpace(settings.log_dir) ? settings.log_dir : string.Empty;
            if (!string.IsNullOrWhiteSpace(SavedLogDir))
                Logger.LogDirectory = SavedLogDir;

            // 重新加载历史（可能用户更改了角色设定导致 system prompt 变化）
            if (_historyStore != null)
                History = _historyStore.Load(SavedSystemPrompt);

            // 重新加载工具启用状态
            SkillManager?.LoadEnabledSkills(settings);

            Logger.Info($"ReloadSettings: provider={SavedProvider}, model={SavedModel}");
        }
        catch
        {
        }
    }

    public string GetPetName()
    {
        try
        {
            var mw = MW;
            if (mw == null) return string.Empty;
            var mwType = mw.GetType();
            var saveProp = mwType.GetProperty("GameSave",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase) ??
                mwType.GetProperty("Save",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase) ??
                mwType.GetProperty("MainSave",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            var save = saveProp?.GetValue(mw);
            if (save != null)
            {
                var nameProp = save.GetType().GetProperty("Name",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (nameProp != null)
                {
                    var val = nameProp.GetValue(save);
                    if (val != null) return val.ToString() ?? string.Empty;
                }
                var nameField = save.GetType().GetField("Name",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (nameField != null)
                {
                    var val = nameField.GetValue(save);
                    if (val != null) return val.ToString() ?? string.Empty;
                }
            }
        }
        catch
        {
        }
        return string.Empty;
    }

    private Dictionary<string, string> GetEndpointsLps()
    {
        return LoadLps("endpoints.lps");
    }

    private Dictionary<string, string> LoadLps(string fileName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
            string path = Path.Combine(pluginDir, fileName);
            if (!File.Exists(path))
            {
                Logger.Info($"未找到配置文件: " + fileName);
                return result;
            }
            string content = File.ReadAllText(path, Encoding.UTF8);
            string[] parts = content.Split(new[] { ":|" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                int idx = p.IndexOf('#');
                if (idx > 0)
                {
                    string key = p.Substring(0, idx).Trim();
                    string val = p.Substring(idx + 1).Trim();
                    if (!string.IsNullOrEmpty(key))
                        result[key] = val;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"解析 {fileName} 失败", ex);
        }
        return result;
    }
}
