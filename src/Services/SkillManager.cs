using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using VPet.Plugin.PetChat.Models;

namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 工具管理器。负责加载内置工具和用户自定义工具,提供已启用工具列表用于 API 请求。
/// </summary>
public class SkillManager
{
    private readonly List<SkillInfo> _skills = new();
    private readonly Dictionary<string, string> _toolScripts = new();
    private readonly string _skillsDir;
    private readonly string _pluginDir;

    public SkillManager(string pluginDir)
    {
        _pluginDir = pluginDir;
        _skillsDir = Path.Combine(pluginDir, "skills");
    }

    /// <summary>
    /// 重新加载所有工具（清除现有列表并重新扫描 skills 目录）。
    /// </summary>
    public void ReloadSkills() => LoadSkills();

    /// <summary>
    /// 加载所有工具:内置工具 + 扫描 skills/ 子目录。
    /// </summary>
    public void LoadSkills()
    {
        _skills.Clear();
        _toolScripts.Clear();
        
        // 1. 注册内置工具
        RegisterBuiltInTools();
        
        // 2. 扫描 skills/ 目录加载用户自定义工具
        if (Directory.Exists(_skillsDir))
        {
            foreach (var dir in Directory.GetDirectories(_skillsDir))
            {
                var infoFile = Path.Combine(dir, "info.lps");
                if (File.Exists(infoFile))
                {
                    var skill = ParseSkillInfoLps(File.ReadAllText(infoFile, Encoding.UTF8));
                    if (skill != null && !_skills.Any(s => s.Name == skill.Name))
                    {
                        _skills.Add(skill);
                    }
                }
            }

            // 扫描 .moxi 文件（新格式）
            foreach (var moxiFile in Directory.GetFiles(_skillsDir, "*.moxi"))
            {
                try
                {
                    var toolDef = ToolDefinitionLoader.LoadFromMoxi(moxiFile);
                    if (toolDef != null && !string.IsNullOrEmpty(toolDef.Name) && !_skills.Any(s => s.Name == toolDef.Name))
                    {
                        var skill = new SkillInfo
                        {
                            Name = toolDef.Name,
                            Description = toolDef.Description,
                            Summary = toolDef.Summary,
                            IsBuiltIn = false,
                            IsEnabled = true,
                            Parameters = toolDef.Parameters.Select(p => new ParamInfo
                            {
                                Name = p.Name,
                                Type = p.Type,
                                Required = p.Required,
                                Description = p.Description
                            }).ToList()
                        };
                        _skills.Add(skill);

                        // 缓存脚本代码供 ToolExecutor 使用
                        _toolScripts[toolDef.Name] = toolDef.Code;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[SkillManager] 加载 moxi 工具失败: {moxiFile}", ex);
                }
            }
        }
        
        // 3. 加载启用状态
        var settingsPath = Path.Combine(_pluginDir, "plugin_settings.lps");
        LoadEnabledSkills(settingsPath);
    }

    /// <summary>
    /// 注册内置工具(代码内置,不可删除)。
    /// </summary>
    private void RegisterBuiltInTools()
    {
        _skills.Add(new SkillInfo
        {
            Name = "openweb",
            Description = "这个工具能够用来打开用户设备的默认浏览器,并且获取传递的 url参数打开这个网页",
            Summary = "打开网页",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo
                {
                    Name = "url",
                    Type = "string",
                    Required = true,
                    Description = "打开的目标网页链接"
                }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "screenshot_upload",
            Description = "截取用户电脑整个屏幕的截图,并上传到服务器获取图片URL。返回图片URL供AI查看屏幕内容。无需任何参数。",
            Summary = "截图上传",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });

        _skills.Add(new SkillInfo
        {
            Name = "list_files",
            Description = "列出指定目录路径下的所有文件和子目录。返回文件名称、大小、修改时间等信息。",
            Summary = "列出文件",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo
                {
                    Name = "path",
                    Type = "string",
                    Required = true,
                    Description = "要列出内容的目录路径"
                }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "create_file",
            Description = "在指定路径创建一个空文件。如果目录不存在会自动创建。",
            Summary = "创建文件",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo
                {
                    Name = "path",
                    Type = "string",
                    Required = true,
                    Description = "要创建的文件路径"
                }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "read_file",
            Description = "读取指定路径的文本文件内容并返回。",
            Summary = "读取文件",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo
                {
                    Name = "path",
                    Type = "string",
                    Required = true,
                    Description = "要读取的文件路径"
                }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "write_file",
            Description = "向指定路径的文件写入内容。如果文件已存在，会先自动创建.bak备份文件再覆盖写入。",
            Summary = "写入文件",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo
                {
                    Name = "path",
                    Type = "string",
                    Required = true,
                    Description = "要写入的文件路径"
                },
                new ParamInfo
                {
                    Name = "content",
                    Type = "string",
                    Required = true,
                    Description = "要写入的文件内容"
                }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "download_file",
            Description = "从指定的URL下载文件到本地指定路径。",
            Summary = "下载文件",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo
                {
                    Name = "url",
                    Type = "string",
                    Required = true,
                    Description = "要下载的文件URL链接"
                },
                new ParamInfo
                {
                    Name = "save_path",
                    Type = "string",
                    Required = true,
                    Description = "文件保存到本地的路径"
                }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "open_file",
            Description = "使用Windows默认应用程序打开指定路径的文件或目录。",
            Summary = "打开文件",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo
                {
                    Name = "path",
                    Type = "string",
                    Required = true,
                    Description = "要打开的文件或目录路径"
                }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "create_ppt",
            Description = "创建PowerPoint演示文稿（PPTX格式）。支持标题页、内容页、段落、列表和图片。",
            Summary = "创建PPT",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "title", Type = "string", Required = true, Description = "演示文稿标题" },
                new ParamInfo { Name = "slides", Type = "array", Required = true, Description = "幻灯片数组，每个元素包含type(title/content)、title、content(段落或列表)、image_url(可选)" },
                new ParamInfo { Name = "save_path", Type = "string", Required = true, Description = "文件保存路径（.pptx）" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "read_ppt",
            Description = "读取PowerPoint演示文稿内容，返回每页的文本内容。",
            Summary = "读取PPT",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "path", Type = "string", Required = true, Description = "PPT文件路径" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "create_pdf",
            Description = "创建PDF文档。支持标题、正文段落、列表和图片。",
            Summary = "创建PDF",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "title", Type = "string", Required = true, Description = "文档标题" },
                new ParamInfo { Name = "content", Type = "array", Required = true, Description = "内容数组，每个元素包含type(text/list/image)、text、items(列表项)、image_url(图片链接)" },
                new ParamInfo { Name = "save_path", Type = "string", Required = true, Description = "文件保存路径（.pdf）" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "read_pdf",
            Description = "读取PDF文档内容，返回文本内容。",
            Summary = "读取PDF",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "path", Type = "string", Required = true, Description = "PDF文件路径" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "create_excel",
            Description = "创建Excel表格（XLSX格式）。支持多工作表、表头和数据行。",
            Summary = "创建Excel",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "sheets", Type = "array", Required = true, Description = "工作表数组，每个元素包含name(表名)、headers(表头数组)、data(数据行二维数组)" },
                new ParamInfo { Name = "save_path", Type = "string", Required = true, Description = "文件保存路径（.xlsx）" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "read_excel",
            Description = "读取Excel表格内容，返回所有工作表的表头和数据。",
            Summary = "读取Excel",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "path", Type = "string", Required = true, Description = "Excel文件路径" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "create_word",
            Description = "创建Word文档（DOCX格式）。支持标题、正文段落、列表和图片。",
            Summary = "创建Word",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "title", Type = "string", Required = true, Description = "文档标题" },
                new ParamInfo { Name = "paragraphs", Type = "array", Required = true, Description = "段落数组，每个元素包含type(title/text/list/image)、text、items(列表项)、image_url(图片链接)" },
                new ParamInfo { Name = "save_path", Type = "string", Required = true, Description = "文件保存路径（.docx）" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "read_word",
            Description = "读取Word文档内容，返回所有段落文本。",
            Summary = "读取Word",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "path", Type = "string", Required = true, Description = "Word文件路径" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "open_app",
            Description = "打开指定的应用程序。支持应用名称（如计算器、记事本）或完整路径。",
            Summary = "打开应用",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "app_name", Type = "string", Required = true, Description = "应用程序名称或路径（如：计算器、notepad、calc.exe、C:\\path\\to\\app.exe）" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "close_app",
            Description = "关闭指定的应用程序进程。",
            Summary = "关闭应用",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "process_name", Type = "string", Required = true, Description = "进程名称（如：notepad、calculator、chrome）" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "list_apps",
            Description = "列出当前正在运行的所有应用程序进程。",
            Summary = "列出应用",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });

        _skills.Add(new SkillInfo
        {
            Name = "set_reminder",
            Description = "设置定时提醒。在指定时间提醒用户执行某项任务。",
            Summary = "设置提醒",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "message", Type = "string", Required = true, Description = "提醒内容" },
                new ParamInfo { Name = "delay_seconds", Type = "number", Required = true, Description = "延迟秒数（多少秒后提醒）" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "list_reminders",
            Description = "列出当前所有活动的定时提醒。",
            Summary = "列出提醒",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });

        _skills.Add(new SkillInfo
        {
            Name = "cancel_reminder",
            Description = "取消指定的定时提醒。",
            Summary = "取消提醒",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "reminder_id", Type = "string", Required = true, Description = "提醒ID" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "focus_window",
            Description = "将指定进程的窗口置于前台并激活（类似Alt+Tab切换到该窗口）。",
            Summary = "窗口置顶",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "process_name", Type = "string", Required = true, Description = "进程名称（如：notepad、chrome、WeChat）" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "minimize_window",
            Description = "将指定进程的窗口最小化到后台（任务栏）。",
            Summary = "窗口最小化",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "process_name", Type = "string", Required = true, Description = "进程名称（如：notepad、chrome、WeChat）" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "type_text",
            Description = "模拟键盘输入指定文本到当前焦点窗口。支持\\n（回车）、\\t（制表符）等特殊字符。",
            Summary = "输入文本",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "text", Type = "string", Required = true, Description = "要输入的文本内容" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "draw_on_screen",
            Description = "在屏幕上绘制图形。支持线条(line)、矩形(rect)、圆形(circle)和文本(text)。",
            Summary = "屏幕涂鸦",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "type", Type = "string", Required = true, Description = "图形类型：line（线条）、rect（矩形）、circle（圆形）、text（文本）" },
                new ParamInfo { Name = "x1", Type = "number", Required = true, Description = "起始X坐标" },
                new ParamInfo { Name = "y1", Type = "number", Required = true, Description = "起始Y坐标" },
                new ParamInfo { Name = "x2", Type = "number", Required = false, Description = "结束X坐标（线条/矩形/圆形）" },
                new ParamInfo { Name = "y2", Type = "number", Required = false, Description = "结束Y坐标（线条/矩形/圆形）" },
                new ParamInfo { Name = "text", Type = "string", Required = false, Description = "要绘制的文本内容（text类型）" },
                new ParamInfo { Name = "color", Type = "string", Required = false, Description = "颜色（如：red、blue、#FF0000，默认red）" },
                new ParamInfo { Name = "line_width", Type = "number", Required = false, Description = "线条宽度（默认3）" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "clear_drawings",
            Description = "清空屏幕上所有涂鸦内容。",
            Summary = "清空涂鸦",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });

        // ===== 系统控制类工具 =====
        _skills.Add(new SkillInfo
        {
            Name = "set_volume",
            Description = "获取/设置系统音量百分比，或切换静音。",
            Summary = "音量调节",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "level", Type = "number", Required = false, Description = "音量百分比(0-100)" },
                new ParamInfo { Name = "mute", Type = "boolean", Required = false, Description = "是否静音" },
                new ParamInfo { Name = "delta", Type = "number", Required = false, Description = "音量增减量(正数增加,负数减少)" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "set_brightness",
            Description = "调整显示器亮度（笔记本/支持DDC的台式机）。",
            Summary = "亮度调节",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "level", Type = "number", Required = true, Description = "亮度百分比(0-100)" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "get_battery",
            Description = "读取笔记本或蓝牙外设的剩余电量百分比。",
            Summary = "电量查询",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });

        _skills.Add(new SkillInfo
        {
            Name = "lock_screen",
            Description = "锁定Windows系统屏幕。",
            Summary = "锁定屏幕",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });

        _skills.Add(new SkillInfo
        {
            Name = "empty_recycle_bin",
            Description = "清空系统回收站。",
            Summary = "清空回收站",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });

        // ===== 文件与生产力工具 =====
        _skills.Add(new SkillInfo
        {
            Name = "search_files",
            Description = "按文件名快速检索指定目录（支持模糊匹配）。",
            Summary = "文件搜索",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "pattern", Type = "string", Required = true, Description = "搜索模式(支持通配符*)" },
                new ParamInfo { Name = "path", Type = "string", Required = false, Description = "搜索目录(默认当前目录)" },
                new ParamInfo { Name = "max_results", Type = "number", Required = false, Description = "最大返回数量(默认20)" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "clipboard_history",
            Description = "查看最近的剪贴板内容。",
            Summary = "剪贴板历史",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });

        _skills.Add(new SkillInfo
        {
            Name = "quick_note",
            Description = "写入一段文字到便签文件并自动添加时间戳。",
            Summary = "快捷便签",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "text", Type = "string", Required = true, Description = "便签内容" },
                new ParamInfo { Name = "file_path", Type = "string", Required = false, Description = "保存路径(默认桌面)" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "read_todo",
            Description = "读取本地待办事项文件(Todo.txt或日历文件)内容。",
            Summary = "读取待办",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "file_path", Type = "string", Required = false, Description = "待办文件路径(默认桌面Todo.txt)" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "folder_size",
            Description = "计算指定文件夹占用的磁盘空间。",
            Summary = "文件夹大小",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "path", Type = "string", Required = true, Description = "文件夹路径" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "batch_rename_preview",
            Description = "模拟批量重命名文件，预览结果。",
            Summary = "批量重命名预览",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "path", Type = "string", Required = true, Description = "文件夹路径" },
                new ParamInfo { Name = "pattern", Type = "string", Required = true, Description = "重命名模式(如: photo_{index})" },
                new ParamInfo { Name = "extension", Type = "string", Required = false, Description = "文件扩展名筛选(如: .png)" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "execute_command",
            Description = "执行命令行命令(支持ffmpeg等工具进行文件转换)。",
            Summary = "文件转换",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "command", Type = "string", Required = true, Description = "要执行的命令" },
                new ParamInfo { Name = "working_dir", Type = "string", Required = false, Description = "工作目录(默认当前目录)" }
            }
        });

        // ===== 网络与信息获取工具 =====
        _skills.Add(new SkillInfo
        {
            Name = "get_public_ip",
            Description = "获取当前公网IP地址及所在城市。",
            Summary = "公网IP查询",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });

        _skills.Add(new SkillInfo
        {
            Name = "speed_test",
            Description = "执行轻量级网速测速。",
            Summary = "网速测试",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });

        _skills.Add(new SkillInfo
        {
            Name = "ping_host",
            Description = "检测指定域名或IP的连通性。",
            Summary = "网络连通性检测",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "host", Type = "string", Required = true, Description = "域名或IP地址" },
                new ParamInfo { Name = "port", Type = "number", Required = false, Description = "端口号(可选)" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "get_hot_search",
            Description = "获取微博/B站/知乎的实时热搜前三名。",
            Summary = "实时热搜",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "source", Type = "string", Required = false, Description = "来源(weibo/bilibili/zhihu,默认weibo)" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "convert_currency",
            Description = "实时汇率换算。",
            Summary = "汇率换算",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "from", Type = "string", Required = true, Description = "源货币(如: USD)" },
                new ParamInfo { Name = "to", Type = "string", Required = true, Description = "目标货币(如: CNY)" },
                new ParamInfo { Name = "amount", Type = "number", Required = false, Description = "金额(默认1)" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "get_calendar",
            Description = "获取今日黄历宜忌或星座运势。",
            Summary = "黄历/运势",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "type", Type = "string", Required = false, Description = "类型(calendar/zodiac,默认calendar)" },
                new ParamInfo { Name = "zodiac", Type = "string", Required = false, Description = "星座(aries/taurus/gemini/cancer/leo/virgo/libra/scorpio/sagittarius/capricorn/aquarius/pisces)" }
            }
        });

        // ===== 开发与极客工具 =====
        _skills.Add(new SkillInfo
        {
            Name = "check_port",
            Description = "查看特定端口被哪个进程占用。",
            Summary = "端口查询",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "port", Type = "number", Required = true, Description = "端口号" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "git_command",
            Description = "执行Git快捷命令(status/pull)。",
            Summary = "Git命令",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "command", Type = "string", Required = true, Description = "命令(status/pull)" },
                new ParamInfo { Name = "path", Type = "string", Required = false, Description = "项目路径(默认当前目录)" }
            }
        });

        // ===== 多媒体与截图工具 =====
        _skills.Add(new SkillInfo
        {
            Name = "color_picker",
            Description = "获取鼠标所在位置的颜色值。",
            Summary = "取色器",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });

        _skills.Add(new SkillInfo
        {
            Name = "generate_qrcode",
            Description = "将文本/链接转为二维码图片，返回二维码图片URL。",
            Summary = "生成二维码",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "text", Type = "string", Required = true, Description = "二维码内容文本或链接" }
            }
        });

        _skills.Add(new SkillInfo
        {
            Name = "toggle_theme",
            Description = "切换Windows系统主题(浅色/深色)。",
            Summary = "系统主题切换",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>
            {
                new ParamInfo { Name = "theme", Type = "string", Required = false, Description = "主题(light/dark/system,默认切换)" }
            }
        });

        // ===== 桌面宠物专属工具 =====
        _skills.Add(new SkillInfo
        {
            Name = "system_monitor",
            Description = "读取CPU/内存占用情况。",
            Summary = "系统监控",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });

        _skills.Add(new SkillInfo
        {
            Name = "daily_quote",
            Description = "获取今日励志语录。",
            Summary = "今日鸡汤",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });

        _skills.Add(new SkillInfo
        {
            Name = "toggle_topmost",
            Description = "将当前活动窗口设为始终置顶或取消置顶。",
            Summary = "窗口置顶切换",
            IsBuiltIn = true,
            IsEnabled = true,
            Parameters = new List<ParamInfo>()
        });
    }

    /// <summary>
    /// 解析 skills/ 下 info.lps 文件。
    /// 格式: name#工具名:|description#描述:|summary#简述:|params#参数名,类型,是否必填,描述:|
    /// </summary>
    private SkillInfo? ParseSkillInfoLps(string content)
    {
        try
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = content.Split(new[] { ":|" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                int idx = p.IndexOf('#');
                if (idx > 0)
                    dict[p.Substring(0, idx).Trim()] = p.Substring(idx + 1).Trim();
            }

            var skill = new SkillInfo
            {
                Name = dict.GetValueOrDefault("name", ""),
                Description = dict.GetValueOrDefault("description", ""),
                Summary = dict.GetValueOrDefault("summary", ""),
                IsBuiltIn = false,
                IsEnabled = true,
                Parameters = new List<ParamInfo>()
            };

            // 解析 params
            if (dict.TryGetValue("params", out var paramsStr) && !string.IsNullOrWhiteSpace(paramsStr))
            {
                var paramParts = paramsStr.Split('|');
                foreach (var part in paramParts)
                {
                    var segs = part.Split(',');
                    if (segs.Length >= 4)
                    {
                        skill.Parameters.Add(new ParamInfo
                        {
                            Name = segs[0].Trim(),
                            Type = segs[1].Trim(),
                            Required = segs[2].Trim() == "必填",
                            Description = string.Join(",", segs.Skip(3)).Trim()
                        });
                    }
                }
            }

            return !string.IsNullOrEmpty(skill.Name) ? skill : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取已启用工具的 JSON 对象列表(用于 API 请求的 tools 参数)。
    /// </summary>
    public List<object> GetEnabledTools()
    {
        return GetEnabledTools(true);
    }

    public List<object> GetEnabledTools(bool supportsImageInput)
    {
        var result = new List<object>();
        foreach (var skill in _skills.Where(s => s.IsEnabled))
        {
            if (!supportsImageInput && IsImageRequiredTool(skill.Name)) continue;

            var function = new Dictionary<string, object>
            {
                ["name"] = skill.Name,
                ["description"] = skill.Description,
                ["parameters"] = BuildParametersObject(skill.Parameters)
            };
            var tool = new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = function
            };
            result.Add(tool);
        }
        return result;
    }

    public bool IsImageRequiredTool(string toolName)
    {
        var lower = toolName.ToLowerInvariant();
        return lower == "screenshot_upload";
    }

    private Dictionary<string, object> BuildParametersObject(List<ParamInfo> @params)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();
        
        foreach (var p in @params)
        {
            props[p.Name] = new Dictionary<string, object>
            {
                ["type"] = p.Type,
                ["description"] = p.Description
            };
            if (p.Required) required.Add(p.Name);
        }
        
        return new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = required
        };
    }

    /// <summary>
    /// 根据名称查找工具。
    /// </summary>
    public SkillInfo? GetToolByName(string name)
    {
        return _skills.FirstOrDefault(s => s.Name == name);
    }

    /// <summary>
    /// 获取指定工具的脚本代码(供 ToolExecutor 使用)。
    /// </summary>
    public string? GetToolScript(string toolName)
    {
        return _toolScripts.TryGetValue(toolName, out var code) ? code : null;
    }

    /// <summary>
    /// 判断是否为内置工具。
    /// </summary>
    public bool IsBuiltIn(string name)
    {
        return _skills.Any(s => s.Name == name && s.IsBuiltIn);
    }

    /// <summary>
    /// 获取所有工具列表。
    /// </summary>
    public IReadOnlyList<SkillInfo> GetAllTools() => _skills.AsReadOnly();

    /// <summary>
    /// 设置工具启用状态。
    /// </summary>
    public void SetToolEnabled(string name, bool enabled)
    {
        var tool = GetToolByName(name);
        if (tool != null) tool.IsEnabled = enabled;
    }

    /// <summary>
    /// 删除自定义工具(内置工具不可删除)。
    /// </summary>
    public bool RemoveTool(string name)
    {
        var tool = GetToolByName(name);
        if (tool == null || tool.IsBuiltIn) return false;
        return _skills.Remove(tool);
    }

    /// <summary>
    /// 保存启用状态到 plugin_settings.lps。
    /// </summary>
    public void SaveEnabledSkills(string settingsPath)
    {
        try
        {
            var enabled = string.Join(",", _skills.Where(s => s.IsEnabled).Select(s => s.Name));
            var lines = new List<string>();
            if (File.Exists(settingsPath))
            {
                var content = File.ReadAllText(settingsPath, Encoding.UTF8);
                var sb = new StringBuilder();
                var modified = false;
                var inParts = content.Split(new[] { ":|" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in inParts)
                {
                    if (p.StartsWith("enabled_skills#", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append("enabled_skills#").Append(enabled).Append(":|");
                        modified = true;
                    }
                    else
                    {
                        sb.Append(p).Append(":|");
                    }
                }
                if (!modified) sb.Append("enabled_skills#").Append(enabled).Append(":|");
                File.WriteAllText(settingsPath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("保存工具启用状态失败", ex);
        }
    }

    /// <summary>
    /// 从 plugin_settings.lps 加载启用状态。
    /// </summary>
    public void LoadEnabledSkills(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            var content = File.ReadAllText(settingsPath, Encoding.UTF8);
            var parts = content.Split(new[] { ":|" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                int idx = p.IndexOf('#');
                if (idx > 0 && p.Substring(0, idx).Trim().Equals("enabled_skills", StringComparison.OrdinalIgnoreCase))
                {
                    var enabled = p.Substring(idx + 1).Trim();
                    var names = enabled.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var skill in _skills)
                    {
                        if (names.Contains(skill.Name))
                            skill.IsEnabled = true;
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("加载工具启用状态失败", ex);
        }
    }

    /// <summary>
    /// 保存启用状态(无参数版本,使用默认路径)。
    /// </summary>
    public void SaveEnabledSkills()
    {
        var settingsPath = Path.Combine(_pluginDir, "plugin_settings.lps");
        SaveEnabledSkills(settingsPath);
    }

    /// <summary>
    /// 加载启用状态(无参数版本,使用默认路径)。
    /// </summary>
    private void LoadEnabledSkills()
    {
        var settingsPath = Path.Combine(_pluginDir, "plugin_settings.lps");
        LoadEnabledSkills(settingsPath);
    }
}