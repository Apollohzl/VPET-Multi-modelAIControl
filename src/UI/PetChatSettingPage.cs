using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.Wave;
using VPet.Plugin.PetChat.Models;
using VPet.Plugin.PetChat.Services;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.PetChat.UI;

public class PetChatSettingPage : UserControl
{
    private readonly PetChatPlugin _plugin;
    private readonly string _modelsUrl;
    private readonly string _chatUrl;
    private readonly string _balanceUrl;
    private readonly IMainWindow _mw;

    // 公共字段
    private TextBox _apiKeyBox = null!;
    private ComboBox _modelComboBox = null!;
    private TextBlock _modelCapabilitiesText = null!;
    private TextBox _systemPromptBox = null!;
    private TextBox _logDirBox = null!;
    private TextBlock _balanceText = null!;
    private TextBlock _statusText = null!;
    private TextBlock _historyStatusText = null!;
    private TextBox _testInput = null!;
    private TextBlock _currentLogDirText = null!;
    private TextBlock _historyFilePathText = null!;
    private ComboBox _historyComboBox = null!;
    private Button _switchHistoryBtn = null!;

    // 麦克风测试相关
    private ComboBox _micDeviceComboBox = null!;
    private Canvas _waveformCanvas = null!;
    private Button _saveMicBtn = null!;
    private Button _testMicBtn = null!;
    private WaveInEvent? _waveIn;
    private bool _isTestingMic;
    private readonly List<double> _audioLevels = new();

    // 工具列表相关
    private List<SkillInfo> _tools = new();
    private List<CheckBox> _toolEnableCbs = new();
    private List<Button> _toolDeleteBtns = new();
    private List<TextBox> _toolSummaryBoxes = new();
    private Panel _toolsPanel = null!;

    private readonly string _initialLogDir;
    private readonly string _currentLogDir;
    private List<ModelOption> _models = new();

    // 主题色
    private static readonly System.Windows.Media.Color PrimaryColor = System.Windows.Media.Color.FromRgb(108, 99, 255);
    private static readonly System.Windows.Media.SolidColorBrush PrimaryBrush = new(PrimaryColor);
    private static readonly System.Windows.Media.SolidColorBrush PrimaryLightBrush = new(System.Windows.Media.Color.FromRgb(245, 243, 255));
    private static readonly System.Windows.Media.SolidColorBrush PrimaryBorderBrush = new(System.Windows.Media.Color.FromRgb(200, 195, 240));
    private static readonly System.Windows.Media.SolidColorBrush WhiteBrush = new(System.Windows.Media.Colors.White);
    private static readonly System.Windows.Media.SolidColorBrush GrayBrush = new(System.Windows.Media.Color.FromRgb(128, 128, 128));
    private static readonly System.Windows.Media.SolidColorBrush DimGrayBrush = new(System.Windows.Media.Color.FromRgb(105, 105, 105));
    private static readonly System.Windows.Media.SolidColorBrush RedBrush = new(System.Windows.Media.Color.FromRgb(200, 80, 80));
    private static readonly System.Windows.Media.SolidColorBrush GreenBrush = new(System.Windows.Media.Color.FromRgb(80, 150, 80));

    // 选项卡按钮
    private Button _tab1Btn = null!;
    private Button _tab2Btn = null!;
    private Button _tab3Btn = null!;
    private Button _tab4Btn = null!;

    // 选项卡内容面板
    private Panel _tab1Content = null!;
    private Panel _tab2Content = null!;
    private Panel _tab3Content = null!;
    private Panel _tab4Content = null!;

    public PetChatSettingPage(PetChatPlugin plugin, IMainWindow mw, string modelsUrl, string chatUrl, string balanceUrl,
        string? savedApiKey, string? savedModel, string? savedSystemPrompt,
        string? savedLogDir, string? currentLogDir)
    {
        _plugin = plugin;
        _mw = mw;
        _modelsUrl = modelsUrl ?? string.Empty;
        _chatUrl = chatUrl ?? string.Empty;
        _balanceUrl = balanceUrl ?? string.Empty;
        _initialLogDir = savedLogDir ?? string.Empty;
        _currentLogDir = currentLogDir ?? string.Empty;

        BuildUi(savedApiKey, savedModel, savedSystemPrompt);
        _ = LoadModelsAndBalanceAsync();
    }

    private void BuildUi(string? savedApiKey, string? savedModel, string? savedSystemPrompt)
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var mainStack = new StackPanel { Margin = new Thickness(16) };
        scroll.Content = mainStack;

        // 页面标题
        var header = new Border
        {
            Background = PrimaryBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 14),
        };
        header.Child = new TextBlock
        {
            Text = "智能多模型对话 - 设置",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = WhiteBrush,
        };
        mainStack.Children.Add(header);

        // 选项卡按钮行
        var tabBtnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12),
        };
        _tab1Btn = CreateTabButton("基础配置", true);
        _tab2Btn = CreateTabButton("Key配置", false);
        _tab3Btn = CreateTabButton("自定义工具", false);
        _tab4Btn = CreateTabButton("开发者工具", false);
        _tab1Btn.Click += (s, e) => SwitchTab(1);
        _tab2Btn.Click += (s, e) => SwitchTab(2);
        _tab3Btn.Click += (s, e) => SwitchTab(3);
        _tab4Btn.Click += (s, e) => SwitchTab(4);
        tabBtnPanel.Children.Add(_tab1Btn);
        tabBtnPanel.Children.Add(_tab2Btn);
        tabBtnPanel.Children.Add(_tab3Btn);
        tabBtnPanel.Children.Add(_tab4Btn);
        mainStack.Children.Add(tabBtnPanel);

        // 选项卡容器
        var tabContainer = new Grid { MinHeight = 400 };

        // Tab 1: 基础配置
        _tab1Content = CreateTab1Content(savedModel, savedSystemPrompt);
        _tab1Content.Visibility = Visibility.Visible;

        // Tab 2: Key配置
        _tab2Content = CreateTab2Content(savedApiKey);
        _tab2Content.Visibility = Visibility.Collapsed;

        // Tab 3: 自定义工具
        _tab3Content = CreateTab3Content();
        _tab3Content.Visibility = Visibility.Collapsed;

        // Tab 4: 开发者工具
        _tab4Content = CreateTab4Content();
        _tab4Content.Visibility = Visibility.Collapsed;

        tabContainer.Children.Add(_tab1Content);
        tabContainer.Children.Add(_tab2Content);
        tabContainer.Children.Add(_tab3Content);
        tabContainer.Children.Add(_tab4Content);
        mainStack.Children.Add(tabContainer);

        Content = scroll;
    }

    private Button CreateTabButton(string text, bool isActive)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Background = isActive ? PrimaryBrush : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 230, 240)),
            Foreground = isActive ? WhiteBrush : DimGrayBrush,
        };
        return btn;
    }

    private void SwitchTab(int tabIndex)
    {
        _tab1Content.Visibility = tabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _tab2Content.Visibility = tabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        _tab3Content.Visibility = tabIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        _tab4Content.Visibility = tabIndex == 4 ? Visibility.Visible : Visibility.Collapsed;

        _tab1Btn.Background = tabIndex == 1 ? PrimaryBrush : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 230, 240));
        _tab1Btn.Foreground = tabIndex == 1 ? WhiteBrush : DimGrayBrush;
        _tab2Btn.Background = tabIndex == 2 ? PrimaryBrush : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 230, 240));
        _tab2Btn.Foreground = tabIndex == 2 ? WhiteBrush : DimGrayBrush;
        _tab3Btn.Background = tabIndex == 3 ? PrimaryBrush : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 230, 240));
        _tab3Btn.Foreground = tabIndex == 3 ? WhiteBrush : DimGrayBrush;
        _tab4Btn.Background = tabIndex == 4 ? PrimaryBrush : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 230, 240));
        _tab4Btn.Foreground = tabIndex == 4 ? WhiteBrush : DimGrayBrush;

        // 切换到开发者标签时刷新归档历史下拉框
        if (tabIndex == 4)
        {
            RefreshHistoryComboBox();
        }
    }

    /// <summary>
    /// 刷新归档历史下拉框：读取 Chat_Historys 文件夹中的 json 文件名并填充。
    /// </summary>
    private void RefreshHistoryComboBox()
    {
        if (_historyComboBox == null) return;

        _historyComboBox.Items.Clear();
        var archives = _plugin.GetArchivedHistoryList();

        foreach (var name in archives)
        {
            _historyComboBox.Items.Add(name);
        }

        if (_historyComboBox.Items.Count > 0)
        {
            _historyComboBox.SelectedIndex = 0;
        }
        else
        {
            _historyComboBox.SelectedIndex = -1;
            if (_switchHistoryBtn != null)
                _switchHistoryBtn.Visibility = Visibility.Collapsed;
        }
    }

    // ==================== Tab 1: 基础配置 ====================
    private Panel CreateTab1Content(string? savedModel, string? savedSystemPrompt)
    {
        var stack = new StackPanel();

        // 欢迎卡片
        var welcomeCard = new Border
        {
            Background = PrimaryLightBrush,
            BorderBrush = PrimaryBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 16),
        };
        var welcomeStack = new StackPanel();
        welcomeCard.Child = welcomeStack;

        welcomeStack.Children.Add(new TextBlock
        {
            Text = "欢迎使用【智能多模型对话-墨希MoxiAI】",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = PrimaryBrush,
            Margin = new Thickness(0, 0, 0, 8),
        });
        welcomeStack.Children.Add(new TextBlock
        {
            Text = "本mod由这位初中生主播独自开发,请转载时备注出处。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        welcomeStack.Children.Add(new TextBlock
        {
            Text = "喜欢的话能不能点个关注~---bilibili@apollo小黄",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var bilibiliBtn = new Button
        {
            Content = "点击跳转bilibili",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(20, 10, 20, 10),
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        bilibiliBtn.Click += (s, e) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://space.bilibili.com/1519941537",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开浏览器失败: " + ex.Message);
            }
        };
        welcomeStack.Children.Add(bilibiliBtn);

        var docBtn = new Button
        {
            Content = "答疑解惑文档",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(20, 10, 20, 10),
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 8, 0, 0),
        };
        docBtn.Click += (s, e) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://hzl.ken520.top/vpet/index.html",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开浏览器失败: " + ex.Message);
            }
        };
        welcomeStack.Children.Add(docBtn);

        stack.Children.Add(welcomeCard);

        // 模型选择
        stack.Children.Add(new TextBlock
        {
            Text = "选择模型",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 4, 0, 4)
        });
        _modelComboBox = new ComboBox
        {
            Height = 32,
            Padding = new Thickness(6, 2, 6, 2),
            DisplayMemberPath = "Name",
            SelectedValuePath = "Model",
        };
        _modelComboBox.Items.Add(new ModelOption("loading", "加载中..."));
        _modelComboBox.SelectedIndex = 0;
        if (!string.IsNullOrEmpty(savedModel))
            _modelComboBox.SelectedValue = savedModel;
        _modelComboBox.SelectionChanged += (s, e) =>
        {
            RefreshToolList();
            UpdateModelCapabilities();
        };
        stack.Children.Add(_modelComboBox);

        // 模型支持输入类型显示
        _modelCapabilitiesText = new TextBlock
        {
            Text = "支持的输入类型：",
            FontSize = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100)),
            Margin = new Thickness(0, 4, 0, 0)
        };
        stack.Children.Add(_modelCapabilitiesText);
        UpdateModelCapabilities();

        // 角色设定
        stack.Children.Add(new TextBlock
        {
            Text = "角色设定",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 12, 0, 4)
        });
        _systemPromptBox = new TextBox
        {
            Text = savedSystemPrompt ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 200,
            Padding = new Thickness(6),
            BorderBrush = PrimaryBorderBrush,
            BorderThickness = new Thickness(1),
        };
        if (string.IsNullOrEmpty(_systemPromptBox.Text))
            _systemPromptBox.Text = "请输入角色设定...";
        stack.Children.Add(_systemPromptBox);

        // 保存按钮
        var saveBtn = new Button
        {
            Content = "保存设置",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 16, 0, 0),
            MinWidth = 120,
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        saveBtn.Click += OnSaveTab1Settings;
        stack.Children.Add(saveBtn);

        return stack;
    }

    private void OnSaveTab1Settings(object sender, RoutedEventArgs e)
    {
        try
        {
            string model = string.Empty;
            bool supportsImage = true;
            bool supportsAudio = false;
            bool supportsVideo = false;
            if (_modelComboBox.SelectedItem is ModelOption mo)
            {
                model = mo.Model;
                supportsImage = mo.SupportsImageInput;
                supportsAudio = mo.SupportsAudioInput;
                supportsVideo = mo.SupportsVideoInput;
            }
            string systemPrompt = _systemPromptBox.Text ?? string.Empty;

            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
            string path = Path.Combine(pluginDir, "plugin_settings.lps");

            // 读取现有设置保留 api_key 和 log_dir
            var existingSettings = ReadPluginSettings();

            var sb = new StringBuilder();
            sb.Append("api_key#").Append(existingSettings.TryGetValue("api_key", out var k) ? k : string.Empty).Append(":|");
            sb.Append("model#").Append(model).Append(":|");
            sb.Append("model_supports_image#").Append(supportsImage ? "true" : "false").Append(":|");
            sb.Append("model_supports_audio#").Append(supportsAudio ? "true" : "false").Append(":|");
            sb.Append("model_supports_video#").Append(supportsVideo ? "true" : "false").Append(":|");
            sb.Append("system_prompt#").Append(systemPrompt).Append(":|");
            sb.Append("log_dir#").Append(existingSettings.TryGetValue("log_dir", out var ld) ? ld : string.Empty).Append(":|");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

            MessageBox.Show("基础配置已保存!", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            Logger.Info($"Tab1设置已保存: 模型={model}, 图片支持={supportsImage}, 音频支持={supportsAudio}, 视频支持={supportsVideo}");
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Logger.Error("Tab1保存设置失败", ex);
        }
    }

    // ==================== Tab 2: Key配置 ====================
    private Panel CreateTab2Content(string? savedApiKey)
    {
        var stack = new StackPanel();

        // 说明文本
        var introCard = new Border
        {
            Background = PrimaryLightBrush,
            BorderBrush = PrimaryBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 16),
        };
        var introStack = new StackPanel();
        introCard.Child = introStack;

        introStack.Children.Add(new TextBlock
        {
            Text = "bilibili@apollo小黄,要想使用这个对话模组,请点击下面按钮跳转到",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        introStack.Children.Add(new TextBlock
        {
            Text = "https://enter.pollinations.ai/sign-in",
            TextWrapping = TextWrapping.Wrap,
            Foreground = PrimaryBrush,
            Margin = new Thickness(0, 0, 0, 4),
        });
        introStack.Children.Add(new TextBlock
        {
            Text = "页面内并注册",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        introStack.Children.Add(new TextBlock
        {
            Text = ",然后点击Keys->Add Key->设置一个喜欢的Name->预算和过期时间不填->Profile,Usage,Key Management,Models按钮全部选择->Create->复制API Key",
            TextWrapping = TextWrapping.Wrap,
        });

        var jumpBtn = new Button
        {
            Content = "点击跳转注册",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 10, 0, 0),
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        jumpBtn.Click += (s, e) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://enter.pollinations.ai/sign-in",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开浏览器失败: " + ex.Message);
            }
        };
        introStack.Children.Add(jumpBtn);

        stack.Children.Add(introCard);

        // API Key
        stack.Children.Add(new TextBlock
        {
            Text = "API Key",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 4, 0, 4)
        });
        _apiKeyBox = new TextBox
        {
            Text = savedApiKey ?? string.Empty,
            Height = 32,
            Padding = new Thickness(6, 2, 6, 2),
            BorderBrush = PrimaryBorderBrush,
            BorderThickness = new Thickness(1),
        };
        stack.Children.Add(_apiKeyBox);

        // 余额
        stack.Children.Add(new TextBlock
        {
            Text = "当前余额",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 12, 0, 4)
        });
        _balanceText = new TextBlock { Text = "余额查询中...", FontSize = 14 };
        stack.Children.Add(_balanceText);

        // 保存按钮
        var saveBtn = new Button
        {
            Content = "保存设置",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 16, 0, 0),
            MinWidth = 120,
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        saveBtn.Click += OnSaveTab2Settings;
        stack.Children.Add(saveBtn);

        return stack;
    }

    private void OnSaveTab2Settings(object sender, RoutedEventArgs e)
    {
        try
        {
            string apiKey = _apiKeyBox.Text ?? string.Empty;

            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
            string path = Path.Combine(pluginDir, "plugin_settings.lps");

            // 读取现有设置
            var existingSettings = ReadPluginSettings();

            // 新用户逻辑：如果是第一次配置API Key且模型未配置，自动选择第一个模型并使用默认提示词
            bool isNewUser = string.IsNullOrEmpty(existingSettings.TryGetValue("api_key", out var oldKey) ? oldKey : string.Empty) &&
                             string.IsNullOrEmpty(existingSettings.TryGetValue("model", out var oldModel) ? oldModel : string.Empty);

            string model = string.Empty;
            bool supportsImage = true;
            bool supportsAudio = false;
            bool supportsVideo = false;
            string systemPrompt = string.Empty;

            if (isNewUser && _modelComboBox.Items.Count > 0 && _modelComboBox.Items[0] is ModelOption firstModel)
            {
                // 新用户：自动选择第一个可用模型
                model = firstModel.Model;
                supportsImage = firstModel.SupportsImageInput;
                supportsAudio = firstModel.SupportsAudioInput;
                supportsVideo = firstModel.SupportsVideoInput;
                systemPrompt = "请使用简短的、口语化的、可爱的、女性化的、调皮的语言风格和我交流。你是一只电脑桌宠，你的名字叫{petname}，你喜欢玩虚拟主播模拟器。你需要尝试体会我与你对话中的感情、产生自己的情感并尽可能给出可爱的回答，每次我将会提供给你我的询问问题以及目前你的状态，结合这些数据给出可爱的回答，并且你不能承认你是一个AI机器人，称我为主人，用简短的语言（15 个字左右，必要时增加）并且使用纯文本（禁止 md 语法）。";
                Logger.Info($"[新用户配置] 自动选择模型: {model}, 图片支持={supportsImage}, 音频支持={supportsAudio}, 视频支持={supportsVideo}");
            }
            else
            {
                // 已有配置：保留现有设置
                model = existingSettings.TryGetValue("model", out var mo) ? mo : string.Empty;
                systemPrompt = existingSettings.TryGetValue("system_prompt", out var sp) ? sp : string.Empty;
            }

            var sb = new StringBuilder();
            sb.Append("api_key#").Append(apiKey).Append(":|");
            sb.Append("model#").Append(model).Append(":|");
            sb.Append("model_supports_image#").Append(supportsImage ? "true" : "false").Append(":|");
            sb.Append("model_supports_audio#").Append(supportsAudio ? "true" : "false").Append(":|");
            sb.Append("model_supports_video#").Append(supportsVideo ? "true" : "false").Append(":|");
            sb.Append("system_prompt#").Append(systemPrompt).Append(":|");
            sb.Append("log_dir#").Append(existingSettings.TryGetValue("log_dir", out var ld) ? ld : string.Empty).Append(":|");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

            MessageBox.Show(isNewUser ? "配置已保存！已为您自动选择默认模型和提示词。" : "Key配置已保存!", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            Logger.Info(isNewUser ? $"Tab2设置已保存(新用户): API Key已更新, 模型={model}" : "Tab2设置已保存: API Key已更新");

            // 刷新余额查询
            _ = LoadBalanceAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Logger.Error("Tab2保存设置失败", ex);
        }
    }

    private Dictionary<string, string> ReadPluginSettings()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
            string path = Path.Combine(pluginDir, "plugin_settings.lps");
            if (!File.Exists(path)) return result;

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
        catch { }
        return result;
    }

    private TextBox _toolSearchBox = null!;
    private StackPanel _batchActionPanel = null!;
    private bool _batchPanelVisible = false;

    // ==================== Tab 3: 自定义工具 ====================
    private Panel CreateTab3Content()
    {
        var stack = new StackPanel();

        // 搜索框 + 搜索按钮 + 全选按钮
        var searchPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
        };

        _toolSearchBox = new TextBox
        {
            Width = 200,
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12,
            BorderBrush = PrimaryBorderBrush,
            BorderThickness = new Thickness(1),
        };
        searchPanel.Children.Add(_toolSearchBox);

        var searchBtn = new Button
        {
            Content = "搜索",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(8, 0, 0, 0),
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        searchBtn.Click += (s, e) => RefreshToolList();
        searchPanel.Children.Add(searchBtn);

        // 全选按钮
        var selectAllBtn = new Button
        {
            Content = "全选",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(8, 0, 0, 0),
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        selectAllBtn.Click += (s, e) => ToggleBatchActionPanel();
        searchPanel.Children.Add(selectAllBtn);

        stack.Children.Add(searchPanel);

        // 批量操作面板（默认隐藏，点击全选后显示）
        _batchActionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
        };

        var enableAllBtn = new Button
        {
            Content = "启用",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 8, 0),
            Background = GreenBrush,
            Foreground = WhiteBrush,
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        enableAllBtn.Click += (s, e) => BatchSetEnabled(true);

        var disableAllBtn = new Button
        {
            Content = "禁用",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 0, 0),
            Background = RedBrush,
            Foreground = WhiteBrush,
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        disableAllBtn.Click += (s, e) => BatchSetEnabled(false);

        _batchActionPanel.Children.Add(enableAllBtn);
        _batchActionPanel.Children.Add(disableAllBtn);
        stack.Children.Add(_batchActionPanel);

        // 表格标题
        var headerBorder = new Border
        {
            Background = PrimaryBrush,
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 0),
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        var h1 = new TextBlock { Text = "名称", Foreground = WhiteBrush, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        var h2 = new TextBlock { Text = "简述(≤6字)", Foreground = WhiteBrush, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        var h3 = new TextBlock { Text = "描述", Foreground = WhiteBrush, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        var h4 = new TextBlock { Text = "操作", Foreground = WhiteBrush, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };

        Grid.SetColumn(h1, 0);
        Grid.SetColumn(h2, 1);
        Grid.SetColumn(h3, 2);
        Grid.SetColumn(h4, 3);
        headerGrid.Children.Add(h1);
        headerGrid.Children.Add(h2);
        headerGrid.Children.Add(h3);
        headerGrid.Children.Add(h4);
        headerBorder.Child = headerGrid;
        stack.Children.Add(headerBorder);

        // 工具列表容器（带滚动条，最大显示8行）
        _toolsPanel = new StackPanel { Background = System.Windows.Media.Brushes.White };
        var scrollViewer = new ScrollViewer
        {
            Content = _toolsPanel,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            BorderBrush = PrimaryBorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        stack.Children.Add(scrollViewer);

        // 加载工具列表
        RefreshToolList();

        // 底部按钮
        var btnStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 16, 0, 0),
        };

        // 刷新 + 导入 垂直按钮组
        var leftBtnPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 12, 0),
        };

        var refreshBtn = new Button
        {
            Content = "刷新",
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 0, 6),
            MinWidth = 100,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(70, 130, 180)),
            Foreground = WhiteBrush,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        refreshBtn.Click += (s, e) =>
        {
            _plugin.SkillManager.ReloadSkills();
            RefreshToolList();
        };
        leftBtnPanel.Children.Add(refreshBtn);

        var importBtn = new Button
        {
            Content = "导入",
            Padding = new Thickness(16, 8, 16, 8),
            MinWidth = 100,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 140, 60)),
            Foreground = WhiteBrush,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        importBtn.Click += (s, e) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Moxi工具 (*.moxi)|*.moxi|所有文件 (*.*)|*.*",
                Title = "选择要导入的 .moxi 文件",
                Multiselect = true
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
                string skillsDir = Path.Combine(pluginDir, "skills");
                if (!Directory.Exists(skillsDir))
                    Directory.CreateDirectory(skillsDir);

                int imported = 0;
                foreach (var srcFile in dialog.FileNames)
                {
                    var fileName = Path.GetFileName(srcFile);
                    var destFile = Path.Combine(skillsDir, fileName);
                    File.Copy(srcFile, destFile, true);
                    imported++;
                }

                _plugin.SkillManager.ReloadSkills();
                RefreshToolList();
                MessageBox.Show($"已导入 {imported} 个工具", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        leftBtnPanel.Children.Add(importBtn);
        btnStack.Children.Add(leftBtnPanel);

        var saveBtn = new Button
        {
            Content = "保存设置",
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 12, 0),
            MinWidth = 100,
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        saveBtn.Click += OnSaveTab3Settings;
        btnStack.Children.Add(saveBtn);

        var customBtn = new Button
        {
            Content = "自制AI工具",
            Padding = new Thickness(16, 8, 16, 8),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 150, 80)),
            Foreground = WhiteBrush,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        customBtn.Click += (s, e) =>
        {
            var window = new ToolBuilderWindow();
            window.Owner = Window.GetWindow(this);
            window.ShowDialog();
            // 从工具编辑器返回后自动刷新列表
            _plugin.SkillManager.ReloadSkills();
            RefreshToolList();
        };
        btnStack.Children.Add(customBtn);

        stack.Children.Add(btnStack);

        return stack;
    }

    /// <summary>
    /// 刷新工具列表：清除现有行，重新从 SkillManager 加载并渲染（支持搜索过滤）。
    /// </summary>
    private void RefreshToolList()
    {
        _tools.Clear();
        _toolEnableCbs.Clear();
        _toolDeleteBtns.Clear();
        _toolSummaryBoxes.Clear();
        _toolsPanel.Children.Clear();

        var searchText = (_toolSearchBox?.Text ?? string.Empty).Trim().ToLowerInvariant();
        var allTools = _plugin.SkillManager.GetAllTools();

        // 搜索过滤 + 按优先级排序
        var filteredTools = string.IsNullOrEmpty(searchText)
            ? allTools
            : allTools
                .Select(t => new
                {
                    Tool = t,
                    Priority = CalculateSearchPriority(t, searchText)
                })
                .Where(x => x.Priority > 0)
                .OrderByDescending(x => x.Priority)
                .Select(x => x.Tool)
                .ToList();

        foreach (var tool in filteredTools)
        {
            _tools.Add(tool);
            RenderToolRow(tool);
        }
    }

    /// <summary>
    /// 计算搜索匹配优先级：名称>描述>简述。返回0表示不匹配。
    /// </summary>
    private int CalculateSearchPriority(SkillInfo tool, string searchText)
    {
        if (string.IsNullOrEmpty(searchText)) return 0;

        int priority = 0;

        // 名称匹配：最高优先级
        if (!string.IsNullOrEmpty(tool.Name) && tool.Name.ToLowerInvariant().Contains(searchText))
        {
            priority += 4;
        }

        // 描述匹配：中等优先级
        if (!string.IsNullOrEmpty(tool.Description) && tool.Description.ToLowerInvariant().Contains(searchText))
        {
            priority += 2;
        }

        // 简述匹配：最低优先级
        if (!string.IsNullOrEmpty(tool.Summary) && tool.Summary.ToLowerInvariant().Contains(searchText))
        {
            priority += 1;
        }

        return priority;
    }

    /// <summary>
    /// 切换批量操作面板的显示/隐藏状态。
    /// </summary>
    private void ToggleBatchActionPanel()
    {
        _batchPanelVisible = !_batchPanelVisible;
        _batchActionPanel.Visibility = _batchPanelVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 批量设置当前列表中所有工具的启用状态。
    /// </summary>
    private void BatchSetEnabled(bool enabled)
    {
        foreach (var tool in _tools)
        {
            tool.IsEnabled = enabled;
        }

        // 同步更新 CheckBox 状态
        foreach (var cb in _toolEnableCbs)
        {
            if (cb.IsEnabled)
            {
                cb.IsChecked = enabled;
            }
        }
    }

    private void UpdateModelCapabilities()
    {
        if (_modelComboBox.SelectedItem is ModelOption mo)
        {
            var capabilities = new System.Collections.Generic.List<string>();
            capabilities.Add("文本");
            if (mo.SupportsImageInput) capabilities.Add("图片");
            if (mo.SupportsAudioInput) capabilities.Add("音频");
            if (mo.SupportsVideoInput) capabilities.Add("视频");
            _modelCapabilitiesText.Text = "支持的输入类型：" + string.Join("、", capabilities);
        }
        else
        {
            _modelCapabilitiesText.Text = "支持的输入类型：未知";
        }
    }

    private void RenderToolRow(SkillInfo tool)
    {
        var rowBorder = new Border
        {
            BorderBrush = PrimaryBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 0),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        var nameText = new TextBlock { Text = tool.Name, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold };
        var summaryBox = new TextBox
        {
            Text = tool.Summary,
            MaxLength = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 2, 4, 2),
            BorderBrush = PrimaryBorderBrush,
        };
        _toolSummaryBoxes.Add(summaryBox);

        var descText = new TextBlock { Text = tool.Description, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(summaryBox, 1);
        Grid.SetColumn(descText, 2);
        Grid.SetColumn(actionPanel, 3);

        grid.Children.Add(nameText);
        grid.Children.Add(summaryBox);
        grid.Children.Add(descText);
        grid.Children.Add(actionPanel);
        rowBorder.Child = grid;

        // 获取当前模型是否支持图片输入
        bool supportsImage = true;
        if (_modelComboBox.SelectedItem is ModelOption mo)
            supportsImage = mo.SupportsImageInput;

        // 判断工具是否需要图片输入支持
        bool isImageRequired = _plugin.SkillManager.IsImageRequiredTool(tool.Name);

        // 启用开关
        var enableCb = new CheckBox
        {
            Content = "启用",
            IsChecked = tool.IsEnabled,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !(isImageRequired && !supportsImage),
        };
        if (isImageRequired && !supportsImage)
        {
            enableCb.ToolTip = "当前模型不支持图片输入，此工具不可用";
            enableCb.Foreground = GrayBrush;
        }
        enableCb.Checked += (s, e) => tool.IsEnabled = true;
        enableCb.Unchecked += (s, e) => tool.IsEnabled = false;
        _toolEnableCbs.Add(enableCb);
        actionPanel.Children.Add(enableCb);

        // 内置工具无删除按钮
        if (!tool.IsBuiltIn)
        {
            var deleteBtn = new Button
            {
                Content = "删除",
                Padding = new Thickness(8, 4, 8, 4),
                Background = RedBrush,
                Foreground = WhiteBrush,
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
            };
            var toolRef = tool;
            deleteBtn.Click += (s, e) =>
            {
                if (MessageBox.Show($"确定要删除工具 [{toolRef.Name}] 吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _plugin.SkillManager.RemoveTool(toolRef.Name);
                    _tools.Remove(toolRef);
                    _toolsPanel.Children.Remove(rowBorder);
                }
            };
            _toolDeleteBtns.Add(deleteBtn);
            actionPanel.Children.Add(deleteBtn);
        }

        _toolsPanel.Children.Add(rowBorder);
    }

    private void OnSaveTab3Settings(object sender, RoutedEventArgs e)
    {
        try
        {
            // 同步简述修改到 SkillManager
            for (int i = 0; i < _tools.Count && i < _toolSummaryBoxes.Count; i++)
            {
                _tools[i].Summary = _toolSummaryBoxes[i].Text;
            }

            // 保存工具启用状态和简述
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
            string settingsPath = Path.Combine(pluginDir, "plugin_settings.lps");
            _plugin.SkillManager.SaveEnabledSkills(settingsPath);

            MessageBox.Show(
                "工具配置已保存!\n\n注意：自定义工具需要保存到 mod 插件的 plugin\\skills 文件夹中才能调用到，否则需要手动导入。",
                "保存成功",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Logger.Info("Tab3设置已保存: 工具数量=" + _tools.Count);
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Logger.Error("Tab3保存设置失败", ex);
        }
    }

    // ==================== Tab 4: 开发者工具 ====================
    private Panel CreateTab4Content()
    {
        var stack = new StackPanel();

        // 对话测试区
        stack.Children.Add(new TextBlock
        {
            Text = "对话测试",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 4, 0, 4)
        });
        _testInput = new TextBox
        {
            Height = 80,
            TextWrapping = TextWrapping.Wrap,
            Text = "你好呀!",
            Padding = new Thickness(6),
            BorderBrush = PrimaryBorderBrush,
            BorderThickness = new Thickness(1),
        };
        stack.Children.Add(_testInput);

        _statusText = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 4),
            Foreground = DimGrayBrush,
            TextWrapping = TextWrapping.Wrap,
        };
        stack.Children.Add(_statusText);

        var testBtn = new Button
        {
            Content = "发送测试",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 4, 0, 16),
            MinWidth = 100,
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        testBtn.Click += OnTestChat;
        stack.Children.Add(testBtn);

        // 日志文件区
        stack.Children.Add(new TextBlock
        {
            Text = "日志文件保存路径",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 8, 0, 4)
        });
        _logDirBox = new TextBox
        {
            Text = _initialLogDir,
            Height = 32,
            Padding = new Thickness(6, 2, 6, 2),
            BorderBrush = PrimaryBorderBrush,
            BorderThickness = new Thickness(1),
            ToolTip = "留空则使用默认路径 (mod 目录下的 logs 文件夹)",
        };
        stack.Children.Add(_logDirBox);
        _currentLogDirText = new TextBlock
        {
            Text = "当前实际路径: " + _currentLogDir,
            TextWrapping = TextWrapping.Wrap,
            Foreground = GrayBrush,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
        };
        stack.Children.Add(_currentLogDirText);

        var logBtnStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        var openFolderBtn = new Button
        {
            Content = "打开日志文件夹",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        openFolderBtn.Click += (s, e) =>
        {
            try
            {
                string dir = Logger.LogDirectory;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex) { _statusText.Text = "打开日志文件夹失败: " + ex.Message; }
        };
        logBtnStack.Children.Add(openFolderBtn);

        var openLogBtn = new Button
        {
            Content = "打开今日日志",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        openLogBtn.Click += (s, e) =>
        {
            try
            {
                string file = Logger.GetTodayLogFilePath();
                if (!File.Exists(file)) File.WriteAllText(file, string.Empty, Encoding.UTF8);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = file, UseShellExecute = true });
            }
            catch (Exception ex) { _statusText.Text = "打开日志文件失败: " + ex.Message; }
        };
        logBtnStack.Children.Add(openLogBtn);

        var openErrBtn = new Button
        {
            Content = "打开错误日志",
            Padding = new Thickness(12, 6, 12, 6),
            Background = RedBrush,
            Foreground = WhiteBrush,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        openErrBtn.Click += (s, e) =>
        {
            try
            {
                string file = Logger.GetTodayErrorLogFilePath();
                if (!File.Exists(file)) File.WriteAllText(file, string.Empty, Encoding.UTF8);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = file, UseShellExecute = true });
            }
            catch (Exception ex) { _statusText.Text = "打开错误日志失败: " + ex.Message; }
        };
        logBtnStack.Children.Add(openErrBtn);
        stack.Children.Add(logBtnStack);

        // 对话历史区
        stack.Children.Add(new TextBlock
        {
            Text = "对话历史",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 20, 0, 4)
        });
        _historyStatusText = new TextBlock
        {
            Text = $"当前已保存 {_plugin.CurrentHistoryTurns} 轮对话",
            TextWrapping = TextWrapping.Wrap,
            Foreground = DimGrayBrush,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        };
        stack.Children.Add(_historyStatusText);
        _historyFilePathText = new TextBlock
        {
            Text = "历史文件: " + _plugin.HistoryFilePath,
            TextWrapping = TextWrapping.Wrap,
            Foreground = GrayBrush,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
        };
        stack.Children.Add(_historyFilePathText);

        var histBtnStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

        var openHistBtn = new Button
        {
            Content = "打开历史文件",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        openHistBtn.Click += (s, e) =>
        {
            try
            {
                string file = _plugin.HistoryFilePath;
                if (!File.Exists(file)) File.WriteAllText(file, "[]", Encoding.UTF8);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = file, UseShellExecute = true });
            }
            catch (Exception ex) { _statusText.Text = "打开历史文件失败: " + ex.Message; }
        };
        histBtnStack.Children.Add(openHistBtn);

        var clearHistBtn = new Button
        {
            Content = "清空对话历史",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Background = RedBrush,
            Foreground = WhiteBrush,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        clearHistBtn.Click += (s, e) =>
        {
            try
            {
                if (MessageBox.Show("确定要清空所有对话历史吗？此操作不可恢复。", "确认清空", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _plugin.ClearChatHistory();
                    _historyStatusText.Text = $"当前已保存 {_plugin.CurrentHistoryTurns} 轮对话";
                    _statusText.Text = "对话历史已清空";
                }
            }
            catch (Exception ex) { _statusText.Text = "清空对话历史失败: " + ex.Message; }
        };
        histBtnStack.Children.Add(clearHistBtn);

        var saveHistBtn = new Button
        {
            Content = "立即保存",
            Padding = new Thickness(12, 6, 12, 6),
            Background = GreenBrush,
            Foreground = WhiteBrush,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        saveHistBtn.Click += (s, e) =>
        {
            try
            {
                _plugin.SaveChatHistory();
                _historyStatusText.Text = $"当前已保存 {_plugin.CurrentHistoryTurns} 轮对话";
                _statusText.Text = "对话历史已保存";
            }
            catch (Exception ex) { _statusText.Text = "保存对话历史失败: " + ex.Message; }
        };
        histBtnStack.Children.Add(saveHistBtn);

        // 新建对话历史按钮（橙色背景）
        var newHistBtn = new Button
        {
            Content = "新建对话历史",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 140, 40)),
            Foreground = WhiteBrush,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        newHistBtn.Click += (s, e) =>
        {
            try
            {
                var archiveName = _plugin.ArchiveChatHistory();
                if (archiveName == null)
                {
                    _statusText.Text = "当前没有对话历史可归档";
                }
                else
                {
                    _historyStatusText.Text = $"当前已保存 {_plugin.CurrentHistoryTurns} 轮对话";
                    _statusText.Text = "对话历史已归档并新建";
                    RefreshHistoryComboBox();
                }
            }
            catch (Exception ex) { _statusText.Text = "新建对话历史失败: " + ex.Message; }
        };
        histBtnStack.Children.Add(newHistBtn);
        stack.Children.Add(histBtnStack);

        // 归档历史下拉框 + 切换按钮
        var historySwitchPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
        };

        _historyComboBox = new ComboBox
        {
            Width = 260,
            FontSize = 12,
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _historyComboBox.SelectionChanged += (s, e) =>
        {
            _switchHistoryBtn.Visibility = _historyComboBox.SelectedIndex >= 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        };
        historySwitchPanel.Children.Add(_historyComboBox);

        _switchHistoryBtn = new Button
        {
            Content = "切换对话历史",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
        };
        _switchHistoryBtn.Click += (s, e) =>
        {
            try
            {
                if (_historyComboBox.SelectedItem is string selectedName)
                {
                    if (MessageBox.Show("确定要切换到选中的对话历史吗？当前历史将自动归档。", "确认切换", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        if (_plugin.SwitchChatHistory(selectedName))
                        {
                            _historyStatusText.Text = $"当前已保存 {_plugin.CurrentHistoryTurns} 轮对话";
                            _statusText.Text = "对话历史已切换";
                            RefreshHistoryComboBox();
                        }
                        else
                        {
                            _statusText.Text = "切换对话历史失败";
                        }
                    }
                }
            }
            catch (Exception ex) { _statusText.Text = "切换对话历史失败: " + ex.Message; }
        };
        historySwitchPanel.Children.Add(_switchHistoryBtn);
        stack.Children.Add(historySwitchPanel);

        // 麦克风测试区
        stack.Children.Add(new TextBlock
        {
            Text = "麦克风测试",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 20, 0, 4)
        });

        var micPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        _micDeviceComboBox = new ComboBox
        {
            Width = 400,
            FontSize = 12,
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6),
        };
        PopulateMicDeviceComboBox();
        micPanel.Children.Add(_micDeviceComboBox);

        _waveformCanvas = new Canvas
        {
            Width = 400,
            Height = 100,
            Background = new SolidColorBrush(Color.FromRgb(245, 245, 250)),
        };
        var waveformBorder = new Border
        {
            Child = _waveformCanvas,
            BorderBrush = PrimaryBorderBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 6),
        };
        micPanel.Children.Add(waveformBorder);

        var micBtnStack = new StackPanel { Orientation = Orientation.Horizontal };
        _testMicBtn = new Button
        {
            Content = "开始测试",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Background = PrimaryBrush,
            Foreground = WhiteBrush,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        _testMicBtn.Click += OnTestMicrophone;
        micBtnStack.Children.Add(_testMicBtn);

        _saveMicBtn = new Button
        {
            Content = "保存设置",
            Padding = new Thickness(12, 6, 12, 6),
            Background = GreenBrush,
            Foreground = WhiteBrush,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        _saveMicBtn.Click += OnSaveMicrophone;
        micBtnStack.Children.Add(_saveMicBtn);
        micPanel.Children.Add(micBtnStack);

        stack.Children.Add(micPanel);

        return stack;
    }

    // ==================== 异步加载 ====================
    private async System.Threading.Tasks.Task LoadModelsAndBalanceAsync()
    {
        // 加载模型
        try
        {
            if (!string.IsNullOrWhiteSpace(_modelsUrl))
            {
                _models = await ApiClient.GetModelsAsync(_modelsUrl);
                Dispatcher.Invoke(() =>
                {
                    _modelComboBox.Items.Clear();
                    foreach (var m in _models)
                        _modelComboBox.Items.Add(m);
                    if (_models.Count > 0)
                        _modelComboBox.SelectedIndex = 0;
                    else
                    {
                        _modelComboBox.Items.Add(new ModelOption("empty", "无可用模型"));
                        _modelComboBox.SelectedIndex = 0;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => _balanceText.Text = "模型加载失败: " + ex.Message);
        }

        // 加载余额
        await LoadBalanceAsync();
    }

    private async System.Threading.Tasks.Task LoadBalanceAsync()
    {
        try
        {
            // 优先使用用户保存到本地文件中的 API Key
            string apiKey = ReadSavedApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                // 本地文件没有保存时，回退到输入框中的内容
                Dispatcher.Invoke(() => apiKey = _apiKeyBox.Text);
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Dispatcher.Invoke(() => _balanceText.Text = "余额: 未提供 API Key");
                return;
            }
            var (ok, pollen, err) = await ApiClient.GetBalanceAsync(apiKey);
            Dispatcher.Invoke(() =>
            {
                if (ok)
                    _balanceText.Text = "剩余 " + pollen.ToString("0.##") + " 花粉";
                else
                    _balanceText.Text = err ?? "查询失败";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => _balanceText.Text = "余额查询失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 从本地配置文件 plugin_settings.lps 中读取已保存的 API Key。
    /// </summary>
    private string ReadSavedApiKey()
    {
        try
        {
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
            string path = Path.Combine(pluginDir, "plugin_settings.lps");
            if (!File.Exists(path)) return string.Empty;

            string content = File.ReadAllText(path, Encoding.UTF8);
            string[] parts = content.Split(new[] { ":|" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                int idx = p.IndexOf('#');
                if (idx > 0)
                {
                    string key = p.Substring(0, idx).Trim();
                    string val = p.Substring(idx + 1).Trim();
                    if (string.Equals(key, "api_key", StringComparison.OrdinalIgnoreCase))
                        return val;
                }
            }
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ==================== 测试对话 ====================
    private async void OnTestChat(object sender, RoutedEventArgs e)
    {
        try
        {
            string userInput = _testInput.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(userInput)) return;

            string apiKey = string.Empty;
            string model = string.Empty;
            string systemPrompt = string.Empty;
            Dispatcher.Invoke(() =>
            {
                apiKey = _apiKeyBox.Text ?? string.Empty;
                model = _modelComboBox.SelectedItem is ModelOption mo ? mo.Model : string.Empty;
                systemPrompt = _systemPromptBox.Text ?? string.Empty;
            });

            var gameDataInjector = new GameDataInjector(_mw);
            string gameData = gameDataInjector.BuildGameDataBlock();

            Logger.Info($"[Chat] 用户输入: {userInput}");
            Logger.Info($"[Chat] 游戏数据整合后: {gameData}");

            var history = _plugin.History;
            history.AppendUser(userInput + gameData);

            _statusText.Text = "正在生成回复...";
            Logger.Info($"[StreamChat] 请求接口: {_chatUrl},模型: {model}");

            var fullText = new StringBuilder();
            var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
            await foreach (var chunk in ApiClient.StreamChatAsync(_chatUrl, apiKey, model, history.Messages, cts.Token))
            {
                if (chunk.Type == VPet.Plugin.PetChat.Services.StreamChunkType.Text && !string.IsNullOrEmpty(chunk.Text))
                {
                    fullText.Append(chunk.Text);
                    string current = fullText.ToString();
                    Dispatcher.Invoke(() => _statusText.Text = current);
                }
            }

            string reply = fullText.ToString();
            history.AppendAssistant(reply);
            _plugin.SaveChatHistory();

            Dispatcher.Invoke(() =>
            {
                _statusText.Text = "测试完成，当前对话 " + _plugin.CurrentHistoryTurns + " 轮。";
                _historyStatusText.Text = $"当前已保存 {_plugin.CurrentHistoryTurns} 轮对话";
            });

            Logger.Info($"[StreamChat] 接口正常输出: {reply}");
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => _statusText.Text = "测试失败: " + ex.Message);
            Logger.Error("测试对话失败", ex);
        }
    }

    // ==================== 麦克风测试 ====================
    private void PopulateMicDeviceComboBox()
    {
        try
        {
            _micDeviceComboBox.Items.Clear();

            int deviceCount = WaveIn.DeviceCount;
            if (deviceCount == 0)
            {
                _micDeviceComboBox.Items.Add("未检测到麦克风设备");
                _micDeviceComboBox.SelectedIndex = 0;
                return;
            }

            for (int i = 0; i < deviceCount; i++)
            {
                var cap = WaveIn.GetCapabilities(i);
                string deviceName = cap.ProductName;
                _micDeviceComboBox.Items.Add(new MicDeviceItem { DeviceId = i.ToString(), DeviceName = deviceName });
            }

            string savedDevice = _plugin.SavedMicrophoneDevice;
            if (!string.IsNullOrWhiteSpace(savedDevice) && int.TryParse(savedDevice, out int savedId))
            {
                for (int i = 0; i < _micDeviceComboBox.Items.Count; i++)
                {
                    if (_micDeviceComboBox.Items[i] is MicDeviceItem item && item.DeviceId == savedId.ToString())
                    {
                        _micDeviceComboBox.SelectedIndex = i;
                        break;
                    }
                }
            }

            if (_micDeviceComboBox.SelectedIndex < 0)
                _micDeviceComboBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _statusText.Text = "获取麦克风设备列表失败: " + ex.Message;
            Logger.Error("获取麦克风设备列表失败", ex);
        }
    }

    private void OnTestMicrophone(object sender, RoutedEventArgs e)
    {
        if (!_isTestingMic)
        {
            StartMicTest();
        }
        else
        {
            StopMicTest();
        }
    }

    private void StartMicTest()
    {
        try
        {
            if (_micDeviceComboBox.SelectedItem is not MicDeviceItem selectedDevice)
            {
                _statusText.Text = "请先选择麦克风设备";
                return;
            }

            int deviceId = int.Parse(selectedDevice.DeviceId);

            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceId,
                WaveFormat = new WaveFormat(16000, 16, 1)
            };

            _waveIn.DataAvailable += OnMicDataAvailable;
            _waveIn.RecordingStopped += OnMicRecordingStopped;

            _audioLevels.Clear();
            _isTestingMic = true;
            _testMicBtn.Content = "停止测试";
            _testMicBtn.Background = RedBrush;
            _micDeviceComboBox.IsEnabled = false;

            _waveIn.StartRecording();
            _statusText.Text = "麦克风测试中...";
        }
        catch (Exception ex)
        {
            _statusText.Text = "启动麦克风测试失败: " + ex.Message;
            Logger.Error("启动麦克风测试失败", ex);
        }
    }

    private void StopMicTest()
    {
        try
        {
            _isTestingMic = false;
            _testMicBtn.Content = "开始测试";
            _testMicBtn.Background = PrimaryBrush;
            _micDeviceComboBox.IsEnabled = true;

            if (_waveIn != null)
            {
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;
            }

            _audioLevels.Clear();
            ClearWaveform();
            _statusText.Text = "麦克风测试已停止";
        }
        catch (Exception ex)
        {
            _statusText.Text = "停止麦克风测试失败: " + ex.Message;
            Logger.Error("停止麦克风测试失败", ex);
        }
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            double maxLevel = 0;
            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                short sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                double level = Math.Abs(sample) / 32768.0;
                if (level > maxLevel) maxLevel = level;
            }

            _audioLevels.Add(maxLevel);
            if (_audioLevels.Count > 100)
                _audioLevels.RemoveAt(0);

            Dispatcher.Invoke(UpdateWaveform);
        }
        catch
        {
        }
    }

    private void OnMicRecordingStopped(object? sender, StoppedEventArgs e)
    {
        try
        {
            if (_waveIn != null)
            {
                _waveIn.Dispose();
                _waveIn = null;
            }
        }
        catch
        {
        }
    }

    private void UpdateWaveform()
    {
        try
        {
            _waveformCanvas.Children.Clear();

            if (_audioLevels.Count == 0) return;

            double width = _waveformCanvas.Width;
            double height = _waveformCanvas.Height;
            double centerY = height / 2;
            double barWidth = width / 100;

            for (int i = 0; i < _audioLevels.Count; i++)
            {
                double level = _audioLevels[i];
                double barHeight = level * height * 0.8;

                var bar = new System.Windows.Shapes.Rectangle
                {
                    Width = barWidth - 1,
                    Height = barHeight,
                    Fill = PrimaryBrush,
                    RadiusX = 2,
                    RadiusY = 2,
                };

                Canvas.SetLeft(bar, i * barWidth);
                Canvas.SetTop(bar, centerY - barHeight / 2);

                _waveformCanvas.Children.Add(bar);
            }
        }
        catch
        {
        }
    }

    private void ClearWaveform()
    {
        _waveformCanvas.Children.Clear();
    }

    private void OnSaveMicrophone(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_micDeviceComboBox.SelectedItem is MicDeviceItem selectedDevice)
            {
                _plugin.SaveMicrophoneDevice(selectedDevice.DeviceId);
                _statusText.Text = "麦克风设备设置已保存";
                Logger.Info($"[Microphone] 用户保存麦克风设备: {selectedDevice.DeviceName} ({selectedDevice.DeviceId})");
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = "保存麦克风设置失败: " + ex.Message;
            Logger.Error("保存麦克风设置失败", ex);
        }
    }

    private class MicDeviceItem
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;

        public override string ToString()
        {
            return DeviceName;
        }
    }
}
