using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using VPet.Plugin.PetChat.Models;
using VPet.Plugin.PetChat.Services;

namespace VPet.Plugin.PetChat.UI;

/// <summary>
/// 自制 AI 工具编辑器主窗口。
/// 左侧为工具结构配置区（图形/代码两种模式），右侧为 C# 脚本编写与编译/执行结果区。
/// 全部 UI 由代码构建，不依赖 .xaml 文件。
/// </summary>
public class ToolBuilderWindow : Window
{
    private ToolDefinition _toolDef;
    private string? _currentFilePath;
    private bool _isDirty;
    private bool _isLoading;
    private bool _isGraphicMode = true;

    // 左侧控件
    private TextBox _nameBox = null!;
    private TextBox _descBox = null!;
    private TextBox _summaryBox = null!;
    private DataGrid _paramGrid = null!;
    private ObservableCollection<ToolParameter> _parameters = new();
    private TextBox _codeModeBox = null!;
    private FrameworkElement _graphicPanel = null!;
    private FrameworkElement _codePanel = null!;
    private Button _graphicModeBtn = null!;
    private Button _codeModeBtn = null!;

    // 右侧控件
    private TextBox _codeBox = null!;
    private TextBox _lineNumbers = null!;
    private int _gutterLineCount;
    private TextBox _outputBox = null!;

    // 工具列表
    private ListBox _toolList = null!;
    private ObservableCollection<string> _toolFileNames = new();

    private readonly ToolScriptExecutor _executor = new();

    /// <summary>skills 目录路径（插件目录下的 skills/ 子目录）。</summary>
    private static string SkillsDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skills");

    /// <summary>可选参数类型集合（ComboBox 数据源）。</summary>
    private static readonly string[] ParamTypes = { "string", "number", "integer", "boolean" };

    /// <summary>是否有未保存的更改，变更时同步窗口标题。</summary>
    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            _isDirty = value;
            UpdateTitle();
        }
    }

    public ToolBuilderWindow()
    {
        _toolDef = ToolDefinitionLoader.CreateNew();
        SetupUI();
        SyncFromModel();
        RefreshToolList();
        UpdateTitle();
    }

    // ============== UI 构建 ==============

    private void SetupUI()
    {
        this.Title = "自制AI工具编辑器";
        this.Width = 1280;
        this.Height = 800;
        this.MinWidth = 960;
        this.MinHeight = 600;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.F5)
            {
                RefreshToolList();
                e.Handled = true;
            }
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 菜单
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 主区

        var menu = CreateMenuBar();
        Grid.SetRow(menu, 0);
        rootGrid.Children.Add(menu);

        // 左 40% | splitter | 右 60%
        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40, GridUnitType.Star) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Pixel) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60, GridUnitType.Star) });

        var leftPanel = CreateLeftPanel();
        Grid.SetColumn(leftPanel, 0);
        mainGrid.Children.Add(leftPanel);

        var splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.LightGray,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        Grid.SetColumn(splitter, 1);
        mainGrid.Children.Add(splitter);

        var rightPanel = CreateRightPanel();
        Grid.SetColumn(rightPanel, 2);
        mainGrid.Children.Add(rightPanel);

        Grid.SetRow(mainGrid, 1);
        rootGrid.Children.Add(mainGrid);

        this.Content = rootGrid;
    }

    private Menu CreateMenuBar()
    {
        var menu = new Menu();

        var fileMenu = new MenuItem { Header = "文件(_F)" };

        var newItem = new MenuItem { Header = "新建(_N)", InputGestureText = "Ctrl+N" };
        newItem.Click += (s, e) => NewTool();
        fileMenu.Items.Add(newItem);

        var openItem = new MenuItem { Header = "打开(_O)", InputGestureText = "Ctrl+O" };
        openItem.Click += (s, e) => OpenTool();
        fileMenu.Items.Add(openItem);

        fileMenu.Items.Add(new Separator());

        var saveItem = new MenuItem { Header = "保存(_S)", InputGestureText = "Ctrl+S" };
        saveItem.Click += (s, e) => SaveTool();
        fileMenu.Items.Add(saveItem);

        var saveAsItem = new MenuItem { Header = "另存为(_A)", InputGestureText = "Ctrl+Shift+S" };
        saveAsItem.Click += (s, e) => SaveToolAs();
        fileMenu.Items.Add(saveAsItem);

        fileMenu.Items.Add(new Separator());

        var openFolderItem = new MenuItem { Header = "打开工具文件夹(_D)" };
        openFolderItem.Click += (s, e) => OpenSkillsFolder();
        fileMenu.Items.Add(openFolderItem);

        menu.Items.Add(fileMenu);

        // 工具操作菜单
        var toolMenu = new MenuItem { Header = "工具(_T)" };

        var refreshItem = new MenuItem { Header = "刷新工具列表(_R)", InputGestureText = "F5" };
        refreshItem.Click += (s, e) => RefreshToolList();
        toolMenu.Items.Add(refreshItem);

        var openFolderItem2 = new MenuItem { Header = "打开工具文件夹(_D)" };
        openFolderItem2.Click += (s, e) => OpenSkillsFolder();
        toolMenu.Items.Add(openFolderItem2);

        menu.Items.Add(toolMenu);
        return menu;
    }

    private Border CreateLeftPanel()
    {
        var border = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 0, 1, 0)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 工具列表区
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 模式切换
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 内容

        // 工具列表区
        var toolListPanel = new StackPanel { Margin = new Thickness(8, 8, 8, 4) };
        toolListPanel.Children.Add(new TextBlock
        {
            Text = "已有工具",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 3)
        });

        _toolList = new ListBox
        {
            Height = 120,
            ItemsSource = _toolFileNames,
            Margin = new Thickness(0, 0, 0, 4)
        };
        _toolList.MouseDoubleClick += (s, e) =>
        {
            if (_toolList.SelectedItem is string fileName)
                LoadToolFromSkills(fileName);
        };
        toolListPanel.Children.Add(_toolList);

        // 工具操作按钮行
        var toolBtnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4)
        };
        var refreshBtn = new Button
        {
            Content = "刷新",
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 4, 0)
        };
        refreshBtn.Click += (s, e) => RefreshToolList();
        toolBtnPanel.Children.Add(refreshBtn);

        var openFolderBtn = new Button
        {
            Content = "打开文件夹",
            Padding = new Thickness(8, 3, 8, 3)
        };
        openFolderBtn.Click += (s, e) => OpenSkillsFolder();
        toolBtnPanel.Children.Add(openFolderBtn);

        toolListPanel.Children.Add(toolBtnPanel);
        Grid.SetRow(toolListPanel, 0);
        root.Children.Add(toolListPanel);

        // 模式切换按钮
        var togglePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 8, 8)
        };
        _graphicModeBtn = new Button
        {
            Content = "图形模式",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 4, 0)
        };
        _graphicModeBtn.Click += (s, e) => SwitchMode(true);
        togglePanel.Children.Add(_graphicModeBtn);

        _codeModeBtn = new Button
        {
            Content = "代码模式",
            Padding = new Thickness(12, 4, 12, 4)
        };
        _codeModeBtn.Click += (s, e) => SwitchMode(false);
        togglePanel.Children.Add(_codeModeBtn);

        Grid.SetRow(togglePanel, 1);
        root.Children.Add(togglePanel);

        // 内容容器：两种模式叠加，通过 Visibility 切换
        var contentGrid = new Grid();
        _graphicPanel = CreateGraphicPanel();
        _codePanel = CreateCodeModePanel();
        _codePanel.Visibility = Visibility.Collapsed;
        contentGrid.Children.Add(_graphicPanel);
        contentGrid.Children.Add(_codePanel);

        Grid.SetRow(contentGrid, 2);
        root.Children.Add(contentGrid);

        border.Child = root;
        UpdateModeButtonStyles();
        return border;
    }

    private FrameworkElement CreateGraphicPanel()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(8)
        };

        var panel = new StackPanel();

        // 工具名称
        panel.Children.Add(new TextBlock
        {
            Text = "工具名称",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 3)
        });
        _nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 10) };
        _nameBox.TextChanged += (s, e) =>
        {
            if (_isLoading) return;
            _toolDef.Name = _nameBox.Text;
            IsDirty = true;
        };
        panel.Children.Add(_nameBox);

        // 功能描述
        panel.Children.Add(new TextBlock
        {
            Text = "功能描述",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 3)
        });
        _descBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 60,
            Margin = new Thickness(0, 0, 0, 10),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _descBox.TextChanged += (s, e) =>
        {
            if (_isLoading) return;
            _toolDef.Description = _descBox.Text;
            IsDirty = true;
        };
        panel.Children.Add(_descBox);

        // 简述
        panel.Children.Add(new TextBlock
        {
            Text = "简述（≤6字）",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 3)
        });
        _summaryBox = new TextBox { MaxLength = 6, Margin = new Thickness(0, 0, 0, 10) };
        _summaryBox.TextChanged += (s, e) =>
        {
            if (_isLoading) return;
            _toolDef.Summary = _summaryBox.Text;
            IsDirty = true;
        };
        panel.Children.Add(_summaryBox);

        // 参数表格
        panel.Children.Add(new TextBlock
        {
            Text = "参数表格",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 3)
        });

        _paramGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            ItemsSource = _parameters,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 220,
            Margin = new Thickness(0, 0, 0, 5)
        };

        // 参数名列
        _paramGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "参数名",
            Binding = new Binding("Name"),
            Width = 100
        });

        // 类型列（ComboBox）
        _paramGrid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "类型",
            ItemsSource = ParamTypes,
            SelectedItemBinding = new Binding("Type") { Mode = BindingMode.TwoWay },
            Width = 90
        });

        // 必填列
        _paramGrid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "必填",
            Binding = new Binding("Required") { Mode = BindingMode.TwoWay },
            Width = 50
        });

        // 默认值列
        _paramGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "默认值",
            Binding = new Binding("DefaultValue"),
            Width = 100
        });

        // 描述列
        _paramGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "描述",
            Binding = new Binding("Description"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        _parameters.CollectionChanged += (s, e) =>
        {
            if (_isLoading) return;
            IsDirty = true;
        };

        panel.Children.Add(_paramGrid);

        // 添加/删除参数按钮
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 5, 0, 10)
        };
        var addBtn = new Button
        {
            Content = "添加参数",
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 5, 0)
        };
        addBtn.Click += (s, e) =>
        {
            _parameters.Add(new ToolParameter());
            IsDirty = true;
        };
        btnPanel.Children.Add(addBtn);

        var delBtn = new Button
        {
            Content = "删除参数",
            Padding = new Thickness(8, 3, 8, 3)
        };
        delBtn.Click += (s, e) =>
        {
            if (_paramGrid.SelectedItem is ToolParameter tp)
            {
                _parameters.Remove(tp);
                IsDirty = true;
            }
        };
        btnPanel.Children.Add(delBtn);
        panel.Children.Add(btnPanel);

        scroll.Content = panel;
        return scroll;
    }

    private FrameworkElement CreateCodeModePanel()
    {
        var border = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(8),
            Background = Brushes.WhiteSmoke
        };

        var panel = new StackPanel { Margin = new Thickness(5) };
        panel.Children.Add(new TextBlock
        {
            Text = "根据图形模式配置实时生成的 JSON（符合 API 规范）：",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 5)
        });

        _codeModeBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 400
        };
        panel.Children.Add(_codeModeBox);

        border.Child = panel;
        return border;
    }

    private Border CreateRightPanel()
    {
        var border = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1, 0, 0, 0)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 工具栏
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 编写说明
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) }); // 代码编辑器
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 结果标题
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 结果输出

        // 顶部工具栏
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8)
        };
        var compileBtn = new Button
        {
            Content = "编译",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 5, 0)
        };
        compileBtn.Click += (s, e) => Compile();
        toolbar.Children.Add(compileBtn);

        var testBtn = new Button
        {
            Content = "测试",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 5, 0)
        };
        testBtn.Click += (s, e) => _ = TestExecuteAsync();
        toolbar.Children.Add(testBtn);

        var saveBtn = new Button
        {
            Content = "保存",
            Padding = new Thickness(12, 4, 12, 4)
        };
        saveBtn.Click += (s, e) => SaveTool();
        toolbar.Children.Add(saveBtn);

        Grid.SetRow(toolbar, 0);
        grid.Children.Add(toolbar);

        // 编写说明
        var hint = new TextBlock
        {
            Text = "在下方编写 C# 脚本代码（函数签名：public async Task<string> Execute(Dictionary<string,object> args)）",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(8, 0, 8, 4),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(hint, 1);
        grid.Children.Add(hint);

        // 代码编辑器（内置 TextBox + 行号栏）
        // 原先用 AvalonEdit，但它的 DLL 内嵌了 .xshd/.cur 等资源，
        // VPet 的代码插件检测无法解析这些资源会阻止整个 MOD 加载，故改为零依赖实现。
        var editorGrid = new Grid { Margin = new Thickness(8, 0, 8, 4) };
        editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _lineNumbers = new TextBox
        {
            IsReadOnly = true,
            Focusable = false,
            IsHitTestVisible = false,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Brushes.Gray,
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)),
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 2, 8, 2),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalContentAlignment = VerticalAlignment.Top,
            Text = "1"
        };
        Grid.SetColumn(_lineNumbers, 0);
        editorGrid.Children.Add(_lineNumbers);

        _codeBox = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 1, 1, 1),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        _codeBox.TextChanged += (s, e) =>
        {
            UpdateLineNumbers();
            if (_isLoading) return;
            _toolDef.Code = _codeBox.Text;
            IsDirty = true;
        };
        _codeBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnCodeScroll));
        Grid.SetColumn(_codeBox, 1);
        editorGrid.Children.Add(_codeBox);

        Grid.SetRow(editorGrid, 2);
        grid.Children.Add(editorGrid);

        // 结果输出标题
        var outputTitle = new TextBlock
        {
            Text = "结果输出",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(8, 0, 8, 2)
        };
        Grid.SetRow(outputTitle, 3);
        grid.Children.Add(outputTitle);

        // 结果输出区
        _outputBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(8, 0, 8, 8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Black,
            Foreground = Brushes.White
        };
        Grid.SetRow(_outputBox, 4);
        grid.Children.Add(_outputBox);

        border.Child = grid;
        return border;
    }

    // ============== 行号栏 ==============

    /// <summary>刷新左侧行号栏（行数变化时才重建文本）。</summary>
    private void UpdateLineNumbers()
    {
        if (_codeBox == null || _lineNumbers == null) return;

        int count = _codeBox.Text.Count(c => c == '\n') + 1;
        if (count == _gutterLineCount) return;
        _gutterLineCount = count;

        var sb = new System.Text.StringBuilder(count * 4);
        for (int i = 1; i <= count; i++)
        {
            sb.Append(i);
            if (i < count) sb.Append('\n');
        }
        _lineNumbers.Text = sb.ToString();
    }

    /// <summary>代码区滚动时同步行号栏的滚动位置。</summary>
    private void OnCodeScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_lineNumbers == null) return;
        _lineNumbers.ScrollToVerticalOffset(e.VerticalOffset);
    }

    // ============== 模式切换 ==============

    private void SwitchMode(bool graphic)
    {
        if (_isGraphicMode == graphic) return;
        _isGraphicMode = graphic;

        if (graphic)
        {
            _graphicPanel.Visibility = Visibility.Visible;
            _codePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            // 切换到代码模式时自动生成 JSON
            _codeModeBox.Text = GenerateApiJson();
            _graphicPanel.Visibility = Visibility.Collapsed;
            _codePanel.Visibility = Visibility.Visible;
        }

        UpdateModeButtonStyles();
    }

    private void UpdateModeButtonStyles()
    {
        _graphicModeBtn.FontWeight = _isGraphicMode ? FontWeights.Bold : FontWeights.Normal;
        _codeModeBtn.FontWeight = _isGraphicMode ? FontWeights.Normal : FontWeights.Bold;
    }

    // ============== 数据同步 ==============

    /// <summary>从 UI 控件同步到 _toolDef（编译/测试/保存/生成 JSON 前调用）。</summary>
    private void SyncFromUI()
    {
        _toolDef.Name = _nameBox.Text;
        _toolDef.Description = _descBox.Text;
        _toolDef.Summary = _summaryBox.Text;
        _toolDef.Parameters = _parameters.ToList();
        _toolDef.Code = _codeBox.Text;
    }

    /// <summary>从 _toolDef 同步到 UI 控件（新建/打开后调用）。</summary>
    private void SyncFromModel()
    {
        _isLoading = true;
        try
        {
            _nameBox.Text = _toolDef.Name;
            _descBox.Text = _toolDef.Description;
            _summaryBox.Text = _toolDef.Summary;

            _parameters.Clear();
            foreach (var p in _toolDef.Parameters)
                _parameters.Add(p);

            _codeBox.Text = _toolDef.Code;
            UpdateLineNumbers();
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>根据图形模式配置生成符合 API 规范的 JSON。</summary>
    private string GenerateApiJson()
    {
        SyncFromUI();
        return _toolDef.ToApiToolJson();
    }

    private void UpdateTitle()
    {
        var dirty = _isDirty ? "*" : "";
        this.Title = $"自制AI工具编辑器 - {_toolDef.Name}{dirty}";
    }

    // ============== 文件操作 ==============

    /// <summary>刷新 skills 目录下的工具列表。</summary>
    private void RefreshToolList()
    {
        _toolFileNames.Clear();
        try
        {
            if (!Directory.Exists(SkillsDir))
                Directory.CreateDirectory(SkillsDir);

            foreach (var file in Directory.GetFiles(SkillsDir, "*.moxi"))
                _toolFileNames.Add(Path.GetFileName(file));
        }
        catch (Exception ex)
        {
            AppendOutput($"刷新工具列表失败: {ex.Message}", false);
        }
    }

    /// <summary>在资源管理器中打开 skills 目录。</summary>
    private void OpenSkillsFolder()
    {
        try
        {
            if (!Directory.Exists(SkillsDir))
                Directory.CreateDirectory(SkillsDir);

            Process.Start(new ProcessStartInfo
            {
                FileName = SkillsDir,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            AppendOutput($"打开文件夹失败: {ex.Message}", false);
        }
    }

    /// <summary>从 skills 目录加载选中的工具。</summary>
    private void LoadToolFromSkills(string fileName)
    {
        if (!ConfirmDiscardChanges()) return;

        var filePath = Path.Combine(SkillsDir, fileName);
        var def = ToolDefinitionLoader.LoadFromMoxi(filePath);
        if (def != null)
        {
            _toolDef = def;
            _currentFilePath = filePath;
            IsDirty = false;
            SyncFromModel();
            UpdateTitle();
            AppendOutput($"已加载工具: {_toolDef.Name}", true);
        }
        else
        {
            AppendOutput("加载失败，请查看日志", false);
        }
    }

    private void NewTool()
    {
        if (!ConfirmDiscardChanges()) return;

        _toolDef = ToolDefinitionLoader.CreateNew();
        _currentFilePath = null;
        IsDirty = false;
        SyncFromModel();
        UpdateTitle();
        AppendOutput("已新建工具", true);
    }

    private void OpenTool()
    {
        if (!ConfirmDiscardChanges()) return;

        var dialog = new OpenFileDialog
        {
            Filter = "Moxi工具 (*.moxi)|*.moxi|所有文件 (*.*)|*.*",
            Title = "打开工具"
        };

        if (dialog.ShowDialog() == true)
        {
            var def = ToolDefinitionLoader.LoadFromMoxi(dialog.FileName);
            if (def != null)
            {
                _toolDef = def;
                _currentFilePath = dialog.FileName;
                IsDirty = false;
                SyncFromModel();
                UpdateTitle();
                AppendOutput($"已加载工具: {_toolDef.Name}", true);
            }
            else
            {
                AppendOutput("加载失败，请查看日志", false);
            }
        }
    }

    private void SaveTool()
    {
        if (string.IsNullOrEmpty(_currentFilePath))
        {
            SaveToolAs();
            return;
        }

        SyncFromUI();
        if (!ValidateBeforeSave()) return;
        if (ToolDefinitionLoader.SaveToMoxi(_toolDef, _currentFilePath))
        {
            IsDirty = false;
            RefreshToolList();
            AppendOutput($"已保存到: {_currentFilePath}", true);
        }
        else
        {
            AppendOutput("保存失败，请查看日志", false);
        }
    }

    private void SaveToolAs()
    {
        SyncFromUI();
        if (!ValidateBeforeSave()) return;

        // 默认保存到插件目录下的 skills/ 子目录
        var skillsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skills");
        if (!Directory.Exists(skillsDir))
            Directory.CreateDirectory(skillsDir);

        var dialog = new SaveFileDialog
        {
            Filter = "Moxi工具 (*.moxi)|*.moxi",
            Title = "保存工具",
            FileName = string.IsNullOrEmpty(_toolDef.Name) ? "tool.moxi" : _toolDef.Name + ".moxi",
            InitialDirectory = skillsDir
        };

        if (dialog.ShowDialog() == true)
        {
            _currentFilePath = dialog.FileName;
            if (ToolDefinitionLoader.SaveToMoxi(_toolDef, _currentFilePath))
            {
                IsDirty = false;
                RefreshToolList();
                AppendOutput($"已保存到: {_currentFilePath}", true);
            }
            else
            {
                AppendOutput("保存失败，请查看日志", false);
            }
        }
    }

    /// <summary>若有未保存更改则提示用户确认是否丢弃。</summary>
    /// <returns>用户选择继续（true）或取消（false）。</returns>
    private bool ConfirmDiscardChanges()
    {
        if (!_isDirty) return true;
        var result = MessageBox.Show(
            "当前工具未保存，是否保存？",
            "提示",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            SaveTool();
            return !_isDirty;
        }
        if (result == MessageBoxResult.No) return true;
        return false;
    }

    /// <summary>保存前校验工具定义是否符合 API 规范。</summary>
    /// <returns>校验通过返回 true，否则提示用户并返回 false。</returns>
    private bool ValidateBeforeSave()
    {
        // 名称不能为空
        if (string.IsNullOrWhiteSpace(_toolDef.Name))
        {
            MessageBox.Show("工具名称不能为空。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // 名称必须匹配 ^[a-zA-Z0-9_\.-]+$ （API 规范）
        if (!System.Text.RegularExpressions.Regex.IsMatch(_toolDef.Name, @"^[a-zA-Z0-9_\.-]+$"))
        {
            MessageBox.Show(
                "工具名称只能包含英文字母、数字、下划线、点和连字符，不能包含中文或空格。",
                "保存失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        // 功能描述不能为空
        if (string.IsNullOrWhiteSpace(_toolDef.Description))
        {
            MessageBox.Show("功能描述不能为空。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // 参数名校验
        foreach (var p in _toolDef.Parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                MessageBox.Show("存在未填写名称的参数。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(p.Name, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            {
                MessageBox.Show(
                    $"参数名 \"{p.Name}\" 不合法，只能以字母或下划线开头，只能包含字母、数字和下划线。",
                    "保存失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        // 代码不能为空
        if (string.IsNullOrWhiteSpace(_toolDef.Code))
        {
            MessageBox.Show("执行代码不能为空。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    // ============== 编译 / 测试 ==============

    private void Compile()
    {
        SyncFromUI();
        AppendOutput("正在编译...", null);
        var result = _executor.Compile(_toolDef.Code);
        if (result.Success)
        {
            AppendOutput("✓ 编译成功", true);
        }
        else
        {
            AppendOutput("✗ 编译失败：", false);
            AppendOutput(result.ErrorMessage, false);
        }
    }

    private async Task TestExecuteAsync()
    {
        var argsJson = ShowInputDialog(
            "测试参数",
            "请输入 JSON 格式的测试参数（例如：{\"url\": \"https://example.com\"}）：");

        if (argsJson == null) return;

        Dictionary<string, object> args;
        try
        {
            args = ParseArgsJson(argsJson);
        }
        catch (Exception ex)
        {
            AppendOutput($"参数 JSON 解析失败: {ex.Message}", false);
            return;
        }

        AppendOutput("正在执行...", null);
        try
        {
            SyncFromUI();
            var output = await _executor.Execute(_toolDef.Code, args);
            AppendOutput("✓ 执行成功：", true);
            AppendOutput(output, true);
        }
        catch (Exception ex)
        {
            AppendOutput("✗ 执行失败：", false);
            AppendOutput(ex.Message, false);
        }
    }

    /// <summary>解析 JSON 字符串为参数字典，将 JsonElement 转换为原生 CLR 类型。</summary>
    private static Dictionary<string, object> ParseArgsJson(string json)
    {
        var result = new Dictionary<string, object>();
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = ConvertJsonElement(prop.Value) ?? string.Empty;
        }
        return result;
    }

    private static object? ConvertJsonElement(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => el.ToString()
        };
    }

    /// <summary>弹出输入对话框，返回用户输入文本；取消则返回 null。</summary>
    private string? ShowInputDialog(string title, string prompt)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 520,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.CanResize
        };

        var panel = new StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 5)
        });

        var inputBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 220,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        panel.Children.Add(inputBox);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var okBtn = new Button
        {
            Content = "确定",
            Width = 80,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(0, 4, 0, 4)
        };
        var cancelBtn = new Button
        {
            Content = "取消",
            Width = 80,
            Padding = new Thickness(0, 4, 0, 4)
        };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);

        dlg.Content = panel;

        string? result = null;
        okBtn.Click += (s, e) =>
        {
            result = inputBox.Text;
            dlg.DialogResult = true;
            dlg.Close();
        };
        cancelBtn.Click += (s, e) =>
        {
            dlg.DialogResult = false;
            dlg.Close();
        };

        dlg.ShowDialog();
        return dlg.DialogResult == true ? result : null;
    }

    // ============== 输出 ==============

    /// <summary>向结果输出区追加一行文本。success=true 绿色，false 红色，null 白色。</summary>
    private void AppendOutput(string text, bool? success)
    {
        var color = success switch
        {
            true => Brushes.LightGreen,
            false => Brushes.IndianRed,
            null => Brushes.White
        };
        _outputBox.Foreground = color;
        _outputBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        _outputBox.ScrollToEnd();
    }
}
