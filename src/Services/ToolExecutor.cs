using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using NAudio.CoreAudioApi;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VPet.Plugin.PetChat.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using WP = DocumentFormat.OpenXml.Wordprocessing;
using SS = DocumentFormat.OpenXml.Spreadsheet;
using ZXing;
using ZXing.QrCode;

namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 工具执行器。负责执行 AI 请求调用的工具函数。
/// 支持内置工具(如 openweb)与用户自定义 C# 脚本工具。
/// </summary>
public class ToolExecutor
{
    private readonly SkillManager? _skillManager;
    private readonly string? _apiKey;
    private readonly bool _supportsImage;
    private readonly bool _supportsAudio;
    private readonly bool _supportsVideo;

    // 定时提醒管理: 提醒ID -> (提醒内容, 触发时间, Timer)
    private static readonly Dictionary<string, (string Message, DateTime TriggerTime, System.Threading.Timer Timer)> _reminders
        = new Dictionary<string, (string, DateTime, System.Threading.Timer)>();

    private static readonly object _remindersLock = new object();

    /// <summary>
    /// 默认构造函数(无 SkillManager 引用,仅支持内置工具)。
    /// </summary>
    public ToolExecutor()
    {
        _skillManager = null;
        _apiKey = null;
        _supportsImage = true;
        _supportsAudio = false;
        _supportsVideo = false;
    }

    /// <summary>
    /// 构造函数,注入 SkillManager 以支持自定义脚本工具。
    /// </summary>
    /// <param name="skillManager">技能管理器,用于获取自定义工具脚本代码。</param>
    public ToolExecutor(SkillManager skillManager)
    {
        _skillManager = skillManager;
        _apiKey = null;
        _supportsImage = true;
        _supportsAudio = false;
        _supportsVideo = false;
    }

    /// <summary>
    /// 构造函数,注入 SkillManager 和 API Key 以支持需要 API 的内置工具(如截图上传)。
    /// </summary>
    /// <param name="skillManager">技能管理器,用于获取自定义工具脚本代码。</param>
    /// <param name="apiKey">用户的 API Key,用于图片上传等操作。</param>
    public ToolExecutor(SkillManager skillManager, string apiKey)
    {
        _skillManager = skillManager;
        _apiKey = apiKey;
        _supportsImage = true;
        _supportsAudio = false;
        _supportsVideo = false;
    }

    /// <summary>
    /// 构造函数,注入 SkillManager、API Key 和模型能力支持。
    /// </summary>
    /// <param name="skillManager">技能管理器,用于获取自定义工具脚本代码。</param>
    /// <param name="apiKey">用户的 API Key,用于图片上传等操作。</param>
    /// <param name="supportsImage">模型是否支持图片输入。</param>
    /// <param name="supportsAudio">模型是否支持音频输入。</param>
    /// <param name="supportsVideo">模型是否支持视频输入。</param>
    public ToolExecutor(SkillManager skillManager, string apiKey, bool supportsImage, bool supportsAudio, bool supportsVideo)
    {
        _skillManager = skillManager;
        _apiKey = apiKey;
        _supportsImage = supportsImage;
        _supportsAudio = supportsAudio;
        _supportsVideo = supportsVideo;
    }

    /// <summary>
    /// 执行指定的工具。
    /// 内置工具走原逻辑,自定义工具通过 ToolScriptExecutor 执行 C# 脚本。
    /// 返回 List<ContentItem>，可直接用于 tool 消息的 content 数组。
    /// </summary>
    /// <param name="toolName">工具函数名</param>
    /// <param name="argumentsJson">参数字典的 JSON 字符串</param>
    /// <returns>工具执行结果,封装为 ContentItem 列表</returns>
    public async Task<List<ContentItem>> ExecuteAsync(string toolName, string argumentsJson)
    {
        Logger.ToolCall(toolName, argumentsJson);

        try
        {
            List<ContentItem> result;
            if (IsBuiltInTool(toolName))
            {
                result = toolName.ToLowerInvariant() switch
                {
                    "openweb" => await ExecuteOpenWebAsync(argumentsJson),
                    "screenshot_upload" => await ExecuteScreenshotUploadAsync(),
                    "list_files" => await ExecuteListFilesAsync(argumentsJson),
                    "create_file" => await ExecuteCreateFileAsync(argumentsJson),
                    "read_file" => await ExecuteReadFileAsync(argumentsJson),
                    "write_file" => await ExecuteWriteFileAsync(argumentsJson),
                    "download_file" => await ExecuteDownloadFileAsync(argumentsJson),
                    "open_file" => await ExecuteOpenFileAsync(argumentsJson),
                    "create_ppt" => await ExecuteCreatePptAsync(argumentsJson),
                    "read_ppt" => await ExecuteReadPptAsync(argumentsJson),
                    "create_pdf" => await ExecuteCreatePdfAsync(argumentsJson),
                    "read_pdf" => await ExecuteReadPdfAsync(argumentsJson),
                    "create_excel" => await ExecuteCreateExcelAsync(argumentsJson),
                    "read_excel" => await ExecuteReadExcelAsync(argumentsJson),
                    "create_word" => await ExecuteCreateWordAsync(argumentsJson),
                    "read_word" => await ExecuteReadWordAsync(argumentsJson),
                    "open_app" => await ExecuteOpenAppAsync(argumentsJson),
                    "close_app" => await ExecuteCloseAppAsync(argumentsJson),
                    "list_apps" => await ExecuteListAppsAsync(),
                    "set_reminder" => await ExecuteSetReminderAsync(argumentsJson),
                    "list_reminders" => await ExecuteListRemindersAsync(),
                    "cancel_reminder" => await ExecuteCancelReminderAsync(argumentsJson),
                    "focus_window" => await ExecuteFocusWindowAsync(argumentsJson),
                    "minimize_window" => await ExecuteMinimizeWindowAsync(argumentsJson),
                    "type_text" => await ExecuteTypeTextAsync(argumentsJson),
                    "draw_on_screen" => await ExecuteDrawOnScreenAsync(argumentsJson),
                    "clear_drawings" => await ExecuteClearDrawingsAsync(),
                    // 系统控制类
                    "set_volume" => await ExecuteSetVolumeAsync(argumentsJson),
                    "set_brightness" => await ExecuteSetBrightnessAsync(argumentsJson),
                    "get_battery" => await ExecuteGetBatteryAsync(),
                    "lock_screen" => await ExecuteLockScreenAsync(),
                    "empty_recycle_bin" => await ExecuteEmptyRecycleBinAsync(),
                    // 文件与生产力
                    "search_files" => await ExecuteSearchFilesAsync(argumentsJson),
                    "clipboard_history" => await ExecuteClipboardHistoryAsync(),
                    "quick_note" => await ExecuteQuickNoteAsync(argumentsJson),
                    "read_todo" => await ExecuteReadTodoAsync(argumentsJson),
                    "folder_size" => await ExecuteFolderSizeAsync(argumentsJson),
                    "batch_rename_preview" => await ExecuteBatchRenamePreviewAsync(argumentsJson),
                    "execute_command" => await ExecuteExecuteCommandAsync(argumentsJson),
                    // 网络与信息获取
                    "get_public_ip" => await ExecuteGetPublicIpAsync(),
                    "speed_test" => await ExecuteSpeedTestAsync(),
                    "ping_host" => await ExecutePingHostAsync(argumentsJson),
                    "get_hot_search" => await ExecuteGetHotSearchAsync(argumentsJson),
                    "convert_currency" => await ExecuteConvertCurrencyAsync(argumentsJson),
                    "get_calendar" => await ExecuteGetCalendarAsync(argumentsJson),
                    // 开发与极客
                    "check_port" => await ExecuteCheckPortAsync(argumentsJson),
                    "git_command" => await ExecuteGitCommandAsync(argumentsJson),
                    // 多媒体与截图
                    "color_picker" => await ExecuteColorPickerAsync(),
                    "generate_qrcode" => await ExecuteGenerateQrcodeAsync(argumentsJson),
                    "toggle_theme" => await ExecuteToggleThemeAsync(argumentsJson),
                    // 桌面宠物专属
                    "system_monitor" => await ExecuteSystemMonitorAsync(),
                    "daily_quote" => await ExecuteDailyQuoteAsync(),
                    "toggle_topmost" => await ExecuteToggleTopmostAsync(),
                    _ => new List<ContentItem> { new ContentItem { Type = "text", Text = $"未知工具: {toolName}" } }
                };
            }
            else
            {
                var scriptCode = _skillManager?.GetToolScript(toolName);
                if (string.IsNullOrEmpty(scriptCode))
                {
                    Logger.ToolError(toolName, $"未找到工具脚本: {toolName}");
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"未找到工具: {toolName}" } };
                }

                var args = ParseArguments(argumentsJson);
                var scriptExecutor = new ToolScriptExecutor();
                var resultText = await scriptExecutor.Execute(scriptCode, args);

                result = new List<ContentItem> { new ContentItem { Type = "text", Text = resultText } };
            }

            string resultTextLog = result.Count > 0 && result[0].Type == "text" ? result[0].Text ?? "" : $"[{result.Count} 个 content 项]";
            Logger.ToolResult(toolName, result.Count > 0 ? result[0].Type : "empty", resultTextLog);
            return result;
        }
        catch (Exception ex)
        {
            Logger.ToolError(toolName, ex.Message);
            Logger.Error($"[ToolExecutor] 工具执行异常: {toolName}", ex);
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"工具执行失败: {ex.Message}" } };
        }
    }

    /// <summary>
    /// 判断是否为内置工具。
    /// </summary>
    private bool IsBuiltInTool(string toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return false;

        var lower = toolName.ToLowerInvariant();
        if (lower == "openweb" || lower == "screenshot_upload" ||
            lower == "list_files" || lower == "create_file" ||
            lower == "read_file" || lower == "write_file" ||
            lower == "download_file" || lower == "open_file" ||
            lower == "create_ppt" || lower == "read_ppt" ||
            lower == "create_pdf" || lower == "read_pdf" ||
            lower == "create_excel" || lower == "read_excel" ||
            lower == "create_word" || lower == "read_word" ||
            lower == "open_app" || lower == "close_app" || lower == "list_apps" ||
            lower == "set_reminder" || lower == "list_reminders" || lower == "cancel_reminder" ||
            lower == "focus_window" || lower == "minimize_window" ||
            lower == "type_text" || lower == "draw_on_screen" || lower == "clear_drawings" ||
            // 系统控制类
            lower == "set_volume" || lower == "set_brightness" || lower == "get_battery" ||
            lower == "lock_screen" || lower == "empty_recycle_bin" ||
            // 文件与生产力
            lower == "search_files" || lower == "clipboard_history" || lower == "quick_note" ||
            lower == "read_todo" || lower == "folder_size" || lower == "batch_rename_preview" || lower == "execute_command" ||
            // 网络与信息获取
            lower == "get_public_ip" || lower == "speed_test" || lower == "ping_host" ||
            lower == "get_hot_search" || lower == "convert_currency" || lower == "get_calendar" ||
            // 开发与极客
            lower == "check_port" || lower == "git_command" ||
            // 多媒体与截图
            lower == "color_picker" || lower == "generate_qrcode" ||
            lower == "toggle_theme" ||
            // 桌面宠物专属
            lower == "system_monitor" || lower == "daily_quote" ||
            lower == "toggle_topmost") return true;

        if (_skillManager != null && _skillManager.IsBuiltIn(toolName))
            return true;

        return _skillManager == null;
    }

    /// <summary>
    /// 将 JSON 参数字符串解析为 Dictionary&lt;string,object&gt;。
    /// </summary>
    private Dictionary<string, object> ParseArguments(string argumentsJson)
    {
        var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(argumentsJson))
            return args;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return args;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                args[prop.Name] = ConvertJsonElement(prop.Value);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[ToolExecutor] 解析参数失败", ex);
        }

        return args;
    }

    /// <summary>
    /// 将 JsonElement 转换为对应的 CLR 类型。
    /// </summary>
    private static object ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Object => element.GetRawText(),
            JsonValueKind.Array => element.GetRawText(),
            _ => element.GetRawText()
        };
    }

    /// <summary>
    /// 执行 openweb 工具:在默认浏览器中打开 URL。
    /// </summary>
    private async Task<List<ContentItem>> ExecuteOpenWebAsync(string argumentsJson)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;

                string? url = null;

                if (root.TryGetProperty("url", out var urlProp))
                {
                    url = urlProp.GetString();
                }
                else if (root.TryGetProperty("URL", out var urlProp2))
                {
                    url = urlProp2.GetString();
                }
                else if (root.TryGetProperty("Url", out var urlProp3))
                {
                    url = urlProp3.GetString();
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "openweb 工具: 缺少 url 参数" } };
                }

                var psi = new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
                Process.Start(psi);

                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"已成功在浏览器中打开: {url}" } };
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"打开网页失败: {ex.Message}" } };
            }
        });
    }

    /// <summary>
    /// 执行 screenshot_upload 工具:截取整个屏幕,通过 FileProcessor 统一处理(与拖拽上传逻辑一致),返回 image_url 类型的结果。
    /// 流程: 截屏 -> 保存到 temp 目录 -> FileProcessor.ProcessAsync 处理(上传+生成 ContentItem) -> 返回。
    /// </summary>
    private async Task<List<ContentItem>> ExecuteScreenshotUploadAsync()
    {
        Logger.Info("[ScreenshotUpload] ========== 截图上传开始 ==========");

        if (string.IsNullOrEmpty(_apiKey))
        {
            Logger.Error("[ScreenshotUpload] 未配置 API Key，无法上传图片");
            return new List<ContentItem> { new ContentItem { Type = "text", Text = "上传失败: 未配置 API Key" } };
        }

        try
        {
            Logger.Info("[ScreenshotUpload] 开始截取屏幕...");
            string? tempFilePath = null;

            var captureResult = System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                return CaptureScreenAndSave();
            });

            if (captureResult.HasValue)
            {
                tempFilePath = captureResult.Value.filePath;
            }

            if (string.IsNullOrEmpty(tempFilePath) || !File.Exists(tempFilePath))
            {
                Logger.Error("[ScreenshotUpload] 截图失败: 未能捕获屏幕图像");
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "截图失败: 未能捕获屏幕" } };
            }

            Logger.Info($"[ScreenshotUpload] 截图完成,分辨率: {System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width}x{System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height}");
            Logger.Info($"[ScreenshotUpload] 截图已保存到临时文件: {tempFilePath}");
            Logger.Info("[ScreenshotUpload] 调用 FileProcessor.ProcessAsync 统一处理(与拖拽上传逻辑一致)");

            var contentItem = await FileProcessor.ProcessAsync(
                tempFilePath!,
                _supportsImage,
                _supportsAudio,
                _supportsVideo,
                _apiKey!);

            if (contentItem == null)
            {
                Logger.Error("[ScreenshotUpload] FileProcessor 处理失败，返回 null");
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "截图上传失败: 文件处理失败" } };
            }

            Logger.Info($"[ScreenshotUpload] 处理成功,类型: {contentItem.Type}");
            Logger.Info("[ScreenshotUpload] ========== 截图上传完成 ==========");

            return new List<ContentItem> { contentItem };
        }
        catch (Exception ex)
        {
            Logger.Error("[ScreenshotUpload] 截图上传异常", ex);
            Logger.Info("[ScreenshotUpload] ========== 截图上传失败(异常) ==========");
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"截图上传失败: {ex.Message}" } };
        }
    }

    /// <summary>
    /// 截取整个主屏幕,保存到 temp 目录,并返回 (文件路径, PNG字节数组)。
    /// </summary>
    private static (string filePath, byte[] bytes) CaptureScreenAndSave()
    {
        var screenBounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                           ?? new System.Drawing.Rectangle(0, 0,
                               (int)SystemParameters.PrimaryScreenWidth,
                               (int)SystemParameters.PrimaryScreenHeight);

        Logger.Info($"[ScreenshotUpload] 屏幕分辨率: {screenBounds.Width} x {screenBounds.Height},位置: ({screenBounds.X},{screenBounds.Y})");

        using var bmp = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppArgb);
        Logger.Info("[ScreenshotUpload] 位图已创建,开始复制屏幕...");

        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(screenBounds.X, screenBounds.Y, 0, 0, screenBounds.Size, CopyPixelOperation.SourceCopy);
        Logger.Info("[ScreenshotUpload] 屏幕复制完成");

        string tempDir = Path.Combine(Path.GetTempPath(), "PetChat");
        if (!Directory.Exists(tempDir))
        {
            Directory.CreateDirectory(tempDir);
            Logger.Info($"[ScreenshotUpload] 创建临时目录: {tempDir}");
        }

        string fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png";
        string filePath = Path.Combine(tempDir, fileName);
        Logger.Info($"[ScreenshotUpload] 保存截图到: {filePath}");

        bmp.Save(filePath, ImageFormat.Png);

        var fileInfo = new FileInfo(filePath);
        Logger.Info($"[ScreenshotUpload] 截图文件保存成功,文件大小: {fileInfo.Length} bytes");

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        var bytes = ms.ToArray();
        Logger.Info($"[ScreenshotUpload] 内存流转换完成,字节数: {bytes.Length}");

        return (filePath, bytes);
    }

    /// <summary>
    /// 上传图片到 hzliflow 服务器,返回图片 URL。
    /// </summary>
    public static async Task<string> UploadImageAsync(byte[] imageBytes, string apiKey, string tempFileName = "screenshot.png")
    {
        const string uploadUrl = "https://hzliflow.ken520.top/api/upload";

        Logger.Info($"[ScreenshotUpload] 准备上传 - 图片大小: {imageBytes.Length} bytes, 文件名: {tempFileName}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(imageBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(fileContent, "file", tempFileName);
        form.Add(new StringContent(apiKey), "API_Key");

        Logger.Info("[ScreenshotUpload] 发送 HTTP POST 请求...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var resp = await http.PostAsync(uploadUrl, form);
        sw.Stop();

        var respJson = await resp.Content.ReadAsStringAsync();

        Logger.Info($"[ScreenshotUpload] 上传耗时: {sw.ElapsedMilliseconds} ms");
        Logger.Info($"[ScreenshotUpload] HTTP 状态码: {(int)resp.StatusCode} {resp.StatusCode}");
        Logger.Info($"[ScreenshotUpload] 响应内容长度: {respJson.Length} 字符");
        Logger.Info($"[ScreenshotUpload] 响应内容: {respJson}");

        if (!resp.IsSuccessStatusCode)
        {
            Logger.Error($"[ScreenshotUpload] HTTP 请求失败: {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return $"上传失败: {(int)resp.StatusCode} {resp.ReasonPhrase}";
        }

        try
        {
            using var doc = JsonDocument.Parse(respJson);
            bool success = false;
            if (doc.RootElement.TryGetProperty("success", out var succ))
            {
                success = succ.GetBoolean();
                Logger.Info($"[ScreenshotUpload] 返回 success 字段: {success}");
            }

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("url", out var urlEl))
            {
                var url = urlEl.GetString() ?? "";
                Logger.Info($"[ScreenshotUpload] 解析到图片URL: {url}");
                return url;
            }

            if (doc.RootElement.TryGetProperty("message", out var msgEl))
            {
                var msg = msgEl.GetString() ?? "";
                Logger.Info($"[ScreenshotUpload] 服务器返回消息: {msg}");
            }

            Logger.Info("[ScreenshotUpload] 响应中未找到 data.url 字段");
            return "";
        }
        catch (JsonException ex)
        {
            Logger.Error("[ScreenshotUpload] 解析响应JSON失败", ex);
            return $"上传失败: 响应格式异常 - {ex.Message}";
        }
    }

    // ============================================================
    //  内置文件系统工具
    // ============================================================

    /// <summary>
    /// list_files: 列出指定目录下的所有文件和子目录。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteListFilesAsync(string argumentsJson)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                string? path = null;
                if (root.TryGetProperty("path", out var p)) path = p.GetString();

                if (string.IsNullOrWhiteSpace(path))
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "list_files: 缺少 path 参数" } };

                if (!Directory.Exists(path))
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"list_files: 目录不存在: {path}" } };

                var sb = new StringBuilder();
                sb.AppendLine($"目录: {path}");
                sb.AppendLine("---");

                try
                {
                    foreach (var dir in Directory.GetDirectories(path))
                    {
                        var di = new DirectoryInfo(dir);
                        sb.AppendLine($"[DIR]  {di.Name}  (修改: {di.LastWriteTime:yyyy-MM-dd HH:mm:ss})");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"[无法读取子目录: {ex.Message}]");
                }

                try
                {
                    foreach (var file in Directory.GetFiles(path))
                    {
                        var fi = new FileInfo(file);
                        sb.AppendLine($"[FILE] {fi.Name}  大小: {fi.Length} bytes  修改: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"[无法读取文件: {ex.Message}]");
                }

                return new List<ContentItem> { new ContentItem { Type = "text", Text = sb.ToString() } };
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"list_files 执行失败: {ex.Message}" } };
            }
        });
    }

    /// <summary>
    /// create_file: 在指定路径创建一个空文件。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteCreateFileAsync(string argumentsJson)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                string? path = null;
                if (root.TryGetProperty("path", out var p)) path = p.GetString();

                if (string.IsNullOrWhiteSpace(path))
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "create_file: 缺少 path 参数" } };

                if (File.Exists(path))
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"create_file: 文件已存在: {path}" } };

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path, string.Empty);
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"create_file: 文件创建成功: {path}" } };
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"create_file 执行失败: {ex.Message}" } };
            }
        });
    }

    /// <summary>
    /// read_file: 读取指定文件的文本内容。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteReadFileAsync(string argumentsJson)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                string? path = null;
                if (root.TryGetProperty("path", out var p)) path = p.GetString();

                if (string.IsNullOrWhiteSpace(path))
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "read_file: 缺少 path 参数" } };

                if (!File.Exists(path))
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"read_file: 文件不存在: {path}" } };

                string content = File.ReadAllText(path, Encoding.UTF8);
                return new List<ContentItem> { new ContentItem { Type = "text", Text = content } };
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"read_file 执行失败: {ex.Message}" } };
            }
        });
    }

    /// <summary>
    /// write_file: 向指定文件写入内容。若文件已存在则先创建 .bak 备份。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteWriteFileAsync(string argumentsJson)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                string? path = null;
                string? content = null;
                if (root.TryGetProperty("path", out var p)) path = p.GetString();
                if (root.TryGetProperty("content", out var c)) content = c.GetString();

                if (string.IsNullOrWhiteSpace(path))
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "write_file: 缺少 path 参数" } };

                if (content == null)
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "write_file: 缺少 content 参数" } };

                bool hadBackup = false;
                if (File.Exists(path))
                {
                    string backupPath = path + ".bak";
                    File.Copy(path, backupPath, true);
                    hadBackup = true;
                }

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path, content, Encoding.UTF8);

                string msg = hadBackup
                    ? $"write_file: 文件写入成功(已备份原文件到 {path}.bak): {path}"
                    : $"write_file: 文件写入成功: {path}";
                return new List<ContentItem> { new ContentItem { Type = "text", Text = msg } };
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"write_file 执行失败: {ex.Message}" } };
            }
        });
    }

    /// <summary>
    /// download_file: 从 URL 下载文件到本地指定路径。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteDownloadFileAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string? url = null;
            string? savePath = null;
            if (root.TryGetProperty("url", out var u)) url = u.GetString();
            if (root.TryGetProperty("save_path", out var s)) savePath = s.GetString();

            if (string.IsNullOrWhiteSpace(url))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "download_file: 缺少 url 参数" } };

            if (string.IsNullOrWhiteSpace(savePath))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "download_file: 缺少 save_path 参数" } };

            string originalPath = savePath;
            savePath = SanitizeFilePath(savePath);
            bool sanitized = !string.Equals(originalPath, savePath, StringComparison.Ordinal);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            byte[] data = await http.GetByteArrayAsync(url);

            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(savePath, data);

            string msg = $"download_file: 下载成功,文件大小 {data.Length} bytes,保存到: {savePath}";
            if (sanitized)
                msg += $" (原路径包含非法字符已清理: {originalPath})";

            return new List<ContentItem>
            {
                new ContentItem { Type = "text", Text = msg }
            };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"download_file 执行失败: {ex.Message}" } };
        }
    }

    /// <summary>
    /// 清理文件路径中的非法字符（只清理文件名部分，保留目录结构）。
    /// Windows 文件名不允许: \ / : * ? " < > |
    /// </summary>
    private static string SanitizeFilePath(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        string fileName = Path.GetFileName(path);

        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (char c in invalidChars)
        {
            fileName = fileName.Replace(c.ToString(), "_");
        }

        if (string.IsNullOrEmpty(dir))
            return fileName;

        return Path.Combine(dir, fileName);
    }

    /// <summary>
    /// open_file: 使用 Windows 默认应用程序打开指定路径的文件或目录。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteOpenFileAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string? path = null;
            if (root.TryGetProperty("path", out var p)) path = p.GetString();

            if (string.IsNullOrWhiteSpace(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "open_file: 缺少 path 参数" } };

            if (!File.Exists(path) && !Directory.Exists(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"open_file: 文件或目录不存在: {path}" } };

            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = path;
            process.StartInfo.UseShellExecute = true;
            process.Start();

            await Task.CompletedTask;

            return new List<ContentItem>
            {
                new ContentItem { Type = "text", Text = $"open_file: 已使用默认应用打开: {path}" }
            };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"open_file 执行失败: {ex.Message}" } };
        }
    }

    // ============================================================
    //  文档生成工具 (PPT/PDF/Excel/Word)
    // ============================================================

    private static async Task<List<ContentItem>> ExecuteCreatePptAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string? title = null;
            string? savePath = null;
            JsonElement slidesElem = default;

            if (root.TryGetProperty("title", out var t)) title = t.GetString();
            if (root.TryGetProperty("save_path", out var s)) savePath = s.GetString();
            root.TryGetProperty("slides", out slidesElem);

            if (string.IsNullOrWhiteSpace(title))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "create_ppt: 缺少 title 参数" } };
            if (string.IsNullOrWhiteSpace(savePath))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "create_ppt: 缺少 save_path 参数" } };

            string originalPath = savePath;
            savePath = SanitizeFilePath(savePath);

            using var presentation = PresentationDocument.Create(savePath, PresentationDocumentType.Presentation);
            var presentationPart = presentation.AddPresentationPart();
            presentationPart.Presentation = new Presentation();
            
            var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>();
            slideMasterPart.SlideMaster = new SlideMaster();
            var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>();
            slideLayoutPart.SlideLayout = new SlideLayout();

            var slideIdList = new SlideIdList();
            presentationPart.Presentation.Append(slideIdList);

            if (slidesElem.ValueKind == JsonValueKind.Array)
            {
                int slideIdx = 0;
                foreach (var slideElem in slidesElem.EnumerateArray())
                {
                    var slidePart = presentationPart.AddNewPart<SlidePart>();
                    slidePart.Slide = new Slide(
                        new CommonSlideData(
                            new ShapeTree()));
                    
                    var shapeTree = slidePart.Slide.CommonSlideData.ShapeTree;

                    string? slideTitle = null;
                    string? slideType = slideElem.TryGetProperty("type", out var st) ? st.GetString() : "content";
                    if (slideElem.TryGetProperty("title", out var tt)) slideTitle = tt.GetString();

                    if (slideType == "title")
                    {
                        AddPptTitleShape(shapeTree, title, slideTitle ?? "", 50, 50, 600, 100, true);
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(slideTitle))
                            AddPptTitleShape(shapeTree, slideTitle, "", 50, 50, 600, 50, false);

                        int y = 120;
                        if (slideElem.TryGetProperty("content", out var contentElem))
                        {
                            if (contentElem.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in contentElem.EnumerateArray())
                                {
                                    string? text = item.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                                    string? itemType = item.TryGetProperty("type", out var it) ? it.GetString() : "text";

                                    if (itemType == "list")
                                    {
                                        if (item.TryGetProperty("items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
                                        {
                                            foreach (var listItem in itemsElem.EnumerateArray())
                                            {
                                                AddPptTextShape(shapeTree, "• " + (listItem.GetString() ?? ""), 70, y, 560, 30);
                                                y += 35;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        AddPptTextShape(shapeTree, text ?? "", 70, y, 560, 100);
                                        y += 110;
                                    }
                                }
                            }
                            else
                            {
                                AddPptTextShape(shapeTree, contentElem.GetString() ?? "", 70, y, 560, 300);
                                y += 310;
                            }
                        }

                        if (slideElem.TryGetProperty("image_url", out var imgUrl) && !string.IsNullOrEmpty(imgUrl.GetString()))
                        {
                            await AddPptImage(slidePart, shapeTree, imgUrl.GetString()!, 70, y, 500, 300);
                        }
                    }

                    var slideId = new SlideId { Id = (uint)(slideIdx + 256), RelationshipId = presentationPart.GetIdOfPart(slidePart) };
                    slideIdList.Append(slideId);
                    slideIdx++;
                }
            }

            presentationPart.Presentation.Save();

            string msg = $"create_ppt: PPT 创建成功，保存到: {savePath}";
            if (!string.Equals(originalPath, savePath, StringComparison.Ordinal))
                msg += $" (原路径已清理: {originalPath})";

            return new List<ContentItem> { new ContentItem { Type = "text", Text = msg } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"create_ppt 执行失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteReadPptAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string? path = null;
            if (root.TryGetProperty("path", out var p)) path = p.GetString();

            if (string.IsNullOrWhiteSpace(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "read_ppt: 缺少 path 参数" } };
            if (!File.Exists(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"read_ppt: 文件不存在: {path}" } };

            using var presentation = PresentationDocument.Open(path, false);
            var presentationPart = presentation.PresentationPart;
            
            var result = new System.Text.StringBuilder();
            int slideIdx = 1;

            foreach (var slidePart in presentationPart.SlideParts)
            {
                result.AppendLine($"=== 幻灯片 {slideIdx} ===");
                
                var shapes = slidePart.Slide.Descendants<Shape>();
                foreach (var shape in shapes)
                {
                    var textBody = shape.Descendants<TextBody>().FirstOrDefault();
                    if (textBody != null)
                    {
                        string text = string.Join("", textBody.Descendants<A.Text>().Select(t => t.Text));
                        if (!string.IsNullOrWhiteSpace(text))
                            result.AppendLine(text.Trim());
                    }
                }
                result.AppendLine();
                slideIdx++;
            }

            return new List<ContentItem> { new ContentItem { Type = "text", Text = result.ToString().Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"read_ppt 执行失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteCreatePdfAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string? title = null;
            string? savePath = null;
            JsonElement contentElem = default;

            if (root.TryGetProperty("title", out var t)) title = t.GetString();
            if (root.TryGetProperty("save_path", out var s)) savePath = s.GetString();
            root.TryGetProperty("content", out contentElem);

            if (string.IsNullOrWhiteSpace(title))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "create_pdf: 缺少 title 参数" } };
            if (string.IsNullOrWhiteSpace(savePath))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "create_pdf: 缺少 save_path 参数" } };

            string originalPath = savePath;
            savePath = SanitizeFilePath(savePath);

            using var fs = new FileStream(savePath, FileMode.Create);
            var pdfDoc = new iTextSharp.text.Document(iTextSharp.text.PageSize.A4, 50, 50, 50, 50);
            iTextSharp.text.pdf.PdfWriter.GetInstance(pdfDoc, fs);
            pdfDoc.Open();

            var titleFont = iTextSharp.text.FontFactory.GetFont("SimSun", 24, iTextSharp.text.Font.BOLD);
            var headingFont = iTextSharp.text.FontFactory.GetFont("SimSun", 16, iTextSharp.text.Font.BOLD);
            var normalFont = iTextSharp.text.FontFactory.GetFont("SimSun", 12, iTextSharp.text.Font.NORMAL);

            pdfDoc.Add(new iTextSharp.text.Paragraph(title, titleFont));
            pdfDoc.Add(new iTextSharp.text.Paragraph("\n"));

            if (contentElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in contentElem.EnumerateArray())
                {
                    string? text = item.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                    string? itemType = item.TryGetProperty("type", out var it) ? it.GetString() : "text";

                    if (itemType == "title")
                    {
                        pdfDoc.Add(new iTextSharp.text.Paragraph(text ?? "", headingFont));
                    }
                    else if (itemType == "list")
                    {
                        if (item.TryGetProperty("items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var listItem in itemsElem.EnumerateArray())
                            {
                                pdfDoc.Add(new iTextSharp.text.Paragraph("• " + (listItem.GetString() ?? ""), normalFont));
                            }
                        }
                    }
                    else if (itemType == "image")
                    {
                        if (item.TryGetProperty("image_url", out var imgUrl) && !string.IsNullOrEmpty(imgUrl.GetString()))
                        {
                            try
                            {
                                using var http = new HttpClient();
                                byte[] imgBytes = await http.GetByteArrayAsync(imgUrl.GetString()!);
                                var image = iTextSharp.text.Image.GetInstance(imgBytes);
                                image.ScaleToFit(500, 400);
                                pdfDoc.Add(image);
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        pdfDoc.Add(new iTextSharp.text.Paragraph(text ?? "", normalFont));
                    }
                    pdfDoc.Add(new iTextSharp.text.Paragraph("\n"));
                }
            }

            pdfDoc.Close();

            string msg = $"create_pdf: PDF 创建成功，保存到: {savePath}";
            if (!string.Equals(originalPath, savePath, StringComparison.Ordinal))
                msg += $" (原路径已清理: {originalPath})";

            return new List<ContentItem> { new ContentItem { Type = "text", Text = msg } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"create_pdf 执行失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteReadPdfAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string? path = null;
            if (root.TryGetProperty("path", out var p)) path = p.GetString();

            if (string.IsNullOrWhiteSpace(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "read_pdf: 缺少 path 参数" } };
            if (!File.Exists(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"read_pdf: 文件不存在: {path}" } };

            var result = new System.Text.StringBuilder();
            using (var reader = new iTextSharp.text.pdf.PdfReader(path))
            {
                for (int i = 1; i <= reader.NumberOfPages; i++)
                {
                    result.AppendLine($"=== 第 {i} 页 ===");
                    result.AppendLine(iTextSharp.text.pdf.parser.PdfTextExtractor.GetTextFromPage(reader, i));
                    result.AppendLine();
                }
            }

            return new List<ContentItem> { new ContentItem { Type = "text", Text = result.ToString().Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"read_pdf 执行失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteCreateExcelAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string? savePath = null;
            JsonElement sheetsElem = default;

            if (root.TryGetProperty("save_path", out var s)) savePath = s.GetString();
            root.TryGetProperty("sheets", out sheetsElem);

            if (string.IsNullOrWhiteSpace(savePath))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "create_excel: 缺少 save_path 参数" } };

            string originalPath = savePath;
            savePath = SanitizeFilePath(savePath);

            using var workbook = SpreadsheetDocument.Create(savePath, SpreadsheetDocumentType.Workbook);
            var workbookPart = workbook.AddWorkbookPart();
            workbookPart.Workbook = new SS.Workbook();

            if (sheetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var sheetElem in sheetsElem.EnumerateArray())
                {
                    string? sheetName = sheetElem.TryGetProperty("name", out var sn) ? sn.GetString() : "Sheet";

                    var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                    worksheetPart.Worksheet = new SS.Worksheet(
                        new SS.SheetData());

                    var sheetData = worksheetPart.Worksheet.GetFirstChild<SS.SheetData>();

                    int rowIdx = 1;

                    if (sheetElem.TryGetProperty("headers", out var headersElem) && headersElem.ValueKind == JsonValueKind.Array)
                    {
                        var row = new SS.Row { RowIndex = (uint)rowIdx };
                        int colIdx = 1;
                        foreach (var header in headersElem.EnumerateArray())
                        {
                            var cell = CreateTextCell(colIdx, rowIdx, header.GetString() ?? "");
                            row.Append(cell);
                            colIdx++;
                        }
                        sheetData.Append(row);
                        rowIdx++;
                    }

                    if (sheetElem.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var rowElem in dataElem.EnumerateArray())
                        {
                            var row = new SS.Row { RowIndex = (uint)rowIdx };
                            int colIdx = 1;
                            if (rowElem.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var cellElem in rowElem.EnumerateArray())
                                {
                                    var cell = CreateTextCell(colIdx, rowIdx, cellElem.GetString() ?? "");
                                    row.Append(cell);
                                    colIdx++;
                                }
                            }
                            sheetData.Append(row);
                            rowIdx++;
                        }
                    }

                    var sheets = workbookPart.Workbook.AppendChild(new SS.Sheets());
                    var sheet = new SS.Sheet
                    {
                        Id = workbookPart.GetIdOfPart(worksheetPart),
                        SheetId = (uint)(sheets.Count() + 1),
                        Name = sheetName ?? "Sheet"
                    };
                    sheets.Append(sheet);
                }
            }

            workbookPart.Workbook.Save();
            workbook.Dispose();

            string msg = $"create_excel: Excel 创建成功，保存到: {savePath}";
            if (!string.Equals(originalPath, savePath, StringComparison.Ordinal))
                msg += $" (原路径已清理: {originalPath})";

            return new List<ContentItem> { new ContentItem { Type = "text", Text = msg } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"create_excel 执行失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteReadExcelAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string? path = null;
            if (root.TryGetProperty("path", out var p)) path = p.GetString();

            if (string.IsNullOrWhiteSpace(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "read_excel: 缺少 path 参数" } };
            if (!File.Exists(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"read_excel: 文件不存在: {path}" } };

            using var workbook = SpreadsheetDocument.Open(path, false);
            var workbookPart = workbook.WorkbookPart;

            var result = new System.Text.StringBuilder();

            foreach (var sheet in workbookPart.Workbook.Sheets!.Cast<SS.Sheet>())
            {
                result.AppendLine($"=== 工作表: {sheet.Name} ===");
                var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
                
                foreach (var row in worksheetPart.Worksheet.Descendants<SS.Row>())
                {
                    var cells = row.Descendants<SS.Cell>();
                    var values = new List<string>();
                    foreach (var cell in cells)
                    {
                        values.Add(GetCellValue(cell, workbookPart));
                    }
                    result.AppendLine(string.Join("\t", values));
                }
                result.AppendLine();
            }

            return new List<ContentItem> { new ContentItem { Type = "text", Text = result.ToString().Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"read_excel 执行失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteCreateWordAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string? title = null;
            string? savePath = null;
            JsonElement paragraphsElem = default;

            if (root.TryGetProperty("title", out var t)) title = t.GetString();
            if (root.TryGetProperty("save_path", out var s)) savePath = s.GetString();
            root.TryGetProperty("paragraphs", out paragraphsElem);

            if (string.IsNullOrWhiteSpace(title))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "create_word: 缺少 title 参数" } };
            if (string.IsNullOrWhiteSpace(savePath))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "create_word: 缺少 save_path 参数" } };

            string originalPath = savePath;
            savePath = SanitizeFilePath(savePath);

            using var wordDoc = WordprocessingDocument.Create(savePath, WordprocessingDocumentType.Document);
            var mainPart = wordDoc.AddMainDocumentPart();
            mainPart.Document = new WP.Document();
            var body = mainPart.Document.AppendChild(new WP.Body());

            var titleParagraph = body.AppendChild(new WP.Paragraph());
            var titleRun = titleParagraph.AppendChild(new WP.Run());
            titleRun.AppendChild(new WP.Text(title));
            titleParagraph.ParagraphProperties = new WP.ParagraphProperties(
                new WP.ParagraphStyleId { Val = "Title" });

            if (paragraphsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in paragraphsElem.EnumerateArray())
                {
                    string? text = item.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                    string? itemType = item.TryGetProperty("type", out var it) ? it.GetString() : "text";

                    if (itemType == "title")
                    {
                        var heading = body.AppendChild(new WP.Paragraph());
                        var headingRun = heading.AppendChild(new WP.Run());
                        headingRun.AppendChild(new WP.Text(text ?? ""));
                        heading.ParagraphProperties = new WP.ParagraphProperties(
                            new WP.ParagraphStyleId { Val = "Heading1" });
                    }
                    else if (itemType == "list")
                    {
                        if (item.TryGetProperty("items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var listItem in itemsElem.EnumerateArray())
                            {
                                var para = body.AppendChild(new WP.Paragraph());
                                var run = para.AppendChild(new WP.Run());
                                run.AppendChild(new WP.Text("• " + (listItem.GetString() ?? "")));
                            }
                        }
                    }
                    else if (itemType == "image")
                    {
                        if (item.TryGetProperty("image_url", out var imgUrl) && !string.IsNullOrEmpty(imgUrl.GetString()))
                        {
                            try
                            {
                                using var http = new HttpClient();
                                byte[] imgBytes = await http.GetByteArrayAsync(imgUrl.GetString()!);
                                var imagePart = mainPart.AddImagePart(ImagePartType.Png);
                                imagePart.FeedData(new MemoryStream(imgBytes));

                                var para = body.AppendChild(new WP.Paragraph());
                                var run = para.AppendChild(new WP.Run());
                                var drawing = run.AppendChild(new WP.Drawing());
                                var inline = drawing.AppendChild(new A.Wordprocessing.Inline());
                                inline.AppendChild(new A.Wordprocessing.Extent { Cx = 5000000, Cy = 3000000 });
                                inline.AppendChild(new A.Wordprocessing.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 });
                                inline.AppendChild(new A.Wordprocessing.DocProperties { Id = 1, Name = "Image" });
                                inline.AppendChild(new A.Wordprocessing.NonVisualGraphicFrameDrawingProperties(
                                    new A.GraphicFrameLocks { NoChangeAspect = true }));
                                var graphic = inline.AppendChild(new A.Graphic());
                                var graphicData = graphic.AppendChild(new A.GraphicData { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" });
                                graphicData.AppendChild(new A.Pictures.Picture(
                                    new A.Pictures.NonVisualPictureProperties(
                                        new A.Pictures.NonVisualDrawingProperties { Id = 1, Name = "Image" },
                                        new A.Pictures.NonVisualPictureDrawingProperties()),
                                    new A.Pictures.BlipFill(
                                        new A.Blip { Embed = mainPart.GetIdOfPart(imagePart), CompressionState = A.BlipCompressionValues.Print },
                                        new A.Stretch(
                                            new A.FillRectangle())),
                                    new A.Pictures.ShapeProperties(
                                        new A.Transform2D(
                                            new A.Offset { X = 0, Y = 0 },
                                            new A.Extents { Cx = 5000000, Cy = 3000000 }),
                                        new A.PresetGeometry { Preset = A.ShapeTypeValues.Rectangle })));
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        var para = body.AppendChild(new WP.Paragraph());
                        var run = para.AppendChild(new WP.Run());
                        run.AppendChild(new WP.Text(text ?? ""));
                    }
                }
            }

            mainPart.Document.Save();
            wordDoc.Dispose();

            string msg = $"create_word: Word 创建成功，保存到: {savePath}";
            if (!string.Equals(originalPath, savePath, StringComparison.Ordinal))
                msg += $" (原路径已清理: {originalPath})";

            return new List<ContentItem> { new ContentItem { Type = "text", Text = msg } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"create_word 执行失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteReadWordAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string? path = null;
            if (root.TryGetProperty("path", out var p)) path = p.GetString();

            if (string.IsNullOrWhiteSpace(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "read_word: 缺少 path 参数" } };
            if (!File.Exists(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"read_word: 文件不存在: {path}" } };

            using var wordDoc = WordprocessingDocument.Open(path, false);
            var mainPart = wordDoc.MainDocumentPart;

            var result = new System.Text.StringBuilder();
            foreach (var paragraph in mainPart.Document.Body!.Descendants<WP.Paragraph>())
            {
                string text = string.Join("", paragraph.Descendants<WP.Text>().Select(t => t.Text));
                if (!string.IsNullOrWhiteSpace(text))
                    result.AppendLine(text.Trim());
            }

            return new List<ContentItem> { new ContentItem { Type = "text", Text = result.ToString().Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"read_word 执行失败: {ex.Message}" } };
        }
    }

    #region 应用程序管理工具

    /// <summary>
    /// 应用名称映射到可执行文件名或查找键。
    /// </summary>
    private static readonly Dictionary<string, string> AppNameMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "计算器", "calc" },
        { "calculator", "calc" },
        { "记事本", "notepad" },
        { "notepad", "notepad" },
        { "画图", "mspaint" },
        { "paint", "mspaint" },
        { "浏览器", "msedge" },
        { "edge", "msedge" },
        { "chrome", "chrome" },
        { "firefox", "firefox" },
        { "命令行", "cmd" },
        { "cmd", "cmd" },
        { "powershell", "powershell" },
        { "控制面板", "control" },
        { "control", "control" },
        { "文件资源管理器", "explorer" },
        { "explorer", "explorer" },
        { "任务管理器", "taskmgr" },
        { "taskmgr", "taskmgr" },
        { "设置", "ms-settings:" },
        { "settings", "ms-settings:" },
        { "word", "winword" },
        { "excel", "excel" },
        { "powerpoint", "powerpnt" },
        { "onenote", "onenote" },
        { "outlook", "outlook" },
        { "音乐播放器", "wmplayer" },
        { "媒体播放器", "wmplayer" },
        { "播放器", "wmplayer" },
        { "微信", "_wechat_" },
        { "qq", "_qq_" },
        { "腾讯qq", "_qq_" },
        { "tim", "_tim_" },
        { "钉钉", "_dingtalk_" },
        { "飞书", "_feishu_" },
        { "网易云音乐", "_netease_" },
        { "酷狗音乐", "_kugou_" },
        { "酷我音乐", "_kuwo_" },
        { "迅雷", "_thunder_" },
        { "百度网盘", "_baidunetdisk_" },
        { "360安全卫士", "_360safe_" },
        { "杀毒软件", "_360safe_" },
        { "steam", "steam" },
        { "origin", "origin" },
        { "epic", "epic" },
        { "b站", "_bilibili_" },
        { "哔哩哔哩", "_bilibili_" },
        { "斗鱼", "_douyu_" },
        { "虎牙", "_huya_" },
        { "快手", "_kuaishou_" },
        { "抖音", "_douyin_" },
        { "支付宝", "_alipay_" },
        { "淘宝", "_taobao_" },
        { "京东", "_jd_" }
    };

    /// <summary>
    /// 特殊应用的安装路径搜索模式。
    /// </summary>
    private static readonly Dictionary<string, List<string>> AppSearchPaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
    {
        { "_wechat_", new List<string> {
            @"C:\Program Files (x86)\Tencent\WeChat\WeChat.exe",
            @"C:\Program Files\Tencent\WeChat\WeChat.exe",
            @"%AppData%\Tencent\WeChat\WeChat.exe",
            @"%LocalAppData%\Tencent\WeChat\WeChat.exe",
            @"C:\Users\{username}\AppData\Roaming\Tencent\WeChat\WeChat.exe"
        }},
        { "_qq_", new List<string> {
            @"C:\Program Files (x86)\Tencent\QQ\Bin\QQ.exe",
            @"C:\Program Files\Tencent\QQ\Bin\QQ.exe",
            @"%AppData%\Tencent\QQ\Bin\QQ.exe",
            @"%LocalAppData%\Tencent\QQ\Bin\QQ.exe"
        }},
        { "_tim_", new List<string> {
            @"C:\Program Files (x86)\Tencent\TIM\Bin\TIM.exe",
            @"C:\Program Files\Tencent\TIM\Bin\TIM.exe"
        }},
        { "_dingtalk_", new List<string> {
            @"C:\Program Files (x86)\DingTalk\DingTalk.exe",
            @"C:\Program Files\DingTalk\DingTalk.exe",
            @"%AppData%\DingTalk\DingTalk.exe"
        }},
        { "_feishu_", new List<string> {
            @"C:\Program Files (x86)\Feishu\Feishu.exe",
            @"C:\Program Files\Feishu\Feishu.exe",
            @"%AppData%\Feishu\Feishu.exe"
        }},
        { "_netease_", new List<string> {
            @"C:\Program Files (x86)\NetEase\CloudMusic\cloudmusic.exe",
            @"C:\Program Files\NetEase\CloudMusic\cloudmusic.exe",
            @"%LocalAppData%\NetEase\CloudMusic\cloudmusic.exe"
        }},
        { "_kugou_", new List<string> {
            @"C:\Program Files (x86)\KuGou\KuGou.exe",
            @"C:\Program Files\KuGou\KuGou.exe"
        }},
        { "_kuwo_", new List<string> {
            @"C:\Program Files (x86)\KuWo\KuWo.exe",
            @"C:\Program Files\KuWo\KuWo.exe"
        }},
        { "_thunder_", new List<string> {
            @"C:\Program Files (x86)\Thunder Network\Thunder\Program\Thunder.exe",
            @"C:\Program Files\Thunder Network\Thunder\Program\Thunder.exe"
        }},
        { "_baidunetdisk_", new List<string> {
            @"C:\Program Files (x86)\BaiduNetdisk\BaiduNetdisk.exe",
            @"C:\Program Files\BaiduNetdisk\BaiduNetdisk.exe"
        }},
        { "_360safe_", new List<string> {
            @"C:\Program Files (x86)\360\360safe\360Safe.exe",
            @"C:\Program Files\360\360safe\360Safe.exe"
        }},
        { "_bilibili_", new List<string> {
            @"C:\Program Files (x86)\Bilibili\Bilibili.exe",
            @"C:\Program Files\Bilibili\Bilibili.exe",
            @"%LocalAppData%\Programs\Bilibili\Bilibili.exe"
        }},
        { "_douyu_", new List<string> {
            @"C:\Program Files (x86)\DouYu\DouYu.exe",
            @"C:\Program Files\DouYu\DouYu.exe"
        }},
        { "_huya_", new List<string> {
            @"C:\Program Files (x86)\Huya\Huya.exe",
            @"C:\Program Files\Huya\Huya.exe"
        }},
        { "_kuaishou_", new List<string> {
            @"C:\Program Files (x86)\Kuaishou\Kuaishou.exe",
            @"C:\Program Files\Kuaishou\Kuaishou.exe"
        }},
        { "_douyin_", new List<string> {
            @"C:\Program Files (x86)\Douyin\Douyin.exe",
            @"C:\Program Files\Douyin\Douyin.exe",
            @"%LocalAppData%\Programs\Douyin\Douyin.exe"
        }},
        { "_alipay_", new List<string> {
            @"C:\Program Files (x86)\Alipay\Alipay.exe",
            @"C:\Program Files\Alipay\Alipay.exe"
        }},
        { "_taobao_", new List<string> {
            @"C:\Program Files (x86)\Taobao\Taobao.exe",
            @"C:\Program Files\Taobao\Taobao.exe"
        }},
        { "_jd_", new List<string> {
            @"C:\Program Files (x86)\JD\JD.exe",
            @"C:\Program Files\JD\JD.exe"
        }}
    };

    /// <summary>
    /// 从注册表查找应用程序路径。
    /// </summary>
    private static string? FindAppFromRegistry(string appKey)
    {
        try
        {
            var regPaths = new List<string>
            {
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{appKey}",
                $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\{appKey}",
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{appKey}",
                $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{appKey}"
            };

            foreach (var regPath in regPaths)
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath);
                if (key != null)
                {
                    var value = key.GetValue("") as string;
                    if (!string.IsNullOrEmpty(value) && File.Exists(value))
                        return value;
                    value = key.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(value))
                    {
                        var exePath = Path.Combine(value, appKey);
                        if (File.Exists(exePath))
                            return exePath;
                    }
                }
            }

            using var userKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (userKey != null)
            {
                foreach (var subKeyName in userKey.GetSubKeyNames())
                {
                    using var subKey = userKey.OpenSubKey(subKeyName);
                    if (subKey != null)
                    {
                        var value = subKey.GetValue("") as string;
                        if (!string.IsNullOrEmpty(value) && File.Exists(value) &&
                            (subKeyName.IndexOf(appKey, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             value.IndexOf(appKey, StringComparison.OrdinalIgnoreCase) >= 0))
                            return value;
                    }
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 解析路径中的环境变量。
    /// </summary>
    private static string ExpandEnvironmentPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        path = path.Replace("{username}", Environment.UserName);
        return path;
    }

    /// <summary>
    /// 根据应用键查找实际可执行文件路径。
    /// </summary>
    private static string? FindAppExecutable(string appKey)
    {
        // 先尝试从预设路径搜索
        if (AppSearchPaths.TryGetValue(appKey, out var paths))
        {
            foreach (var path in paths)
            {
                var expandedPath = ExpandEnvironmentPath(path);
                if (File.Exists(expandedPath))
                    return expandedPath;
            }
        }

        // 尝试从注册表查找
        string? exeName = null;
        if (appKey.StartsWith("_"))
        {
            exeName = appKey switch
            {
                "_wechat_" => "WeChat.exe",
                "_qq_" => "QQ.exe",
                "_tim_" => "TIM.exe",
                "_dingtalk_" => "DingTalk.exe",
                "_feishu_" => "Feishu.exe",
                "_netease_" => "cloudmusic.exe",
                "_kugou_" => "KuGou.exe",
                "_kuwo_" => "KuWo.exe",
                "_thunder_" => "Thunder.exe",
                "_baidunetdisk_" => "BaiduNetdisk.exe",
                "_360safe_" => "360Safe.exe",
                "_bilibili_" => "Bilibili.exe",
                "_douyu_" => "DouYu.exe",
                "_huya_" => "Huya.exe",
                "_kuaishou_" => "Kuaishou.exe",
                "_douyin_" => "Douyin.exe",
                "_alipay_" => "Alipay.exe",
                "_taobao_" => "Taobao.exe",
                "_jd_" => "JD.exe",
                _ => null
            };
        }

        if (!string.IsNullOrEmpty(exeName))
        {
            var regPath = FindAppFromRegistry(exeName);
            if (!string.IsNullOrEmpty(regPath))
                return regPath;
        }

        // 尝试通用搜索路径
        var commonPaths = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        if (!string.IsNullOrEmpty(exeName))
        {
            foreach (var basePath in commonPaths)
            {
                var found = Directory.GetFiles(basePath, exeName, SearchOption.AllDirectories)
                    .FirstOrDefault(f => !f.Contains(@"\Cache\") && !f.Contains(@"\Temp\"));
                if (!string.IsNullOrEmpty(found))
                    return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 执行 open_app 工具:打开指定的应用程序。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteOpenAppAsync(string argumentsJson)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                string? appName = null;
                if (root.TryGetProperty("app_name", out var app)) appName = app.GetString();
                else if (root.TryGetProperty("app", out var app2)) appName = app2.GetString();
                else if (root.TryGetProperty("name", out var app3)) appName = app3.GetString();

                if (string.IsNullOrWhiteSpace(appName))
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "open_app: 缺少 app_name 参数" } };

                appName = appName.Trim();

                // 检查是否有预定义的映射
                string executable;
                string displayName = appName;

                if (AppNameMappings.TryGetValue(appName, out var mapped))
                {
                    executable = mapped;
                    displayName = appName;
                }
                else if (appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    executable = appName;
                }
                else if (File.Exists(appName) || appName.Contains(":") || appName.StartsWith("\\"))
                {
                    executable = appName;
                }
                else
                {
                    executable = appName;
                }

                // 如果是特殊键（以_开头），尝试查找实际路径
                if (executable.StartsWith("_"))
                {
                    var foundPath = FindAppExecutable(executable);
                    if (!string.IsNullOrEmpty(foundPath))
                    {
                        executable = foundPath;
                    }
                    else
                    {
                        return new List<ContentItem> { new ContentItem { Type = "text", Text = $"找不到应用 '{displayName}' 的安装路径喵～请确认已安装该应用" } };
                    }
                }

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = executable,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"已成功打开应用: {displayName}" } };
                }
                catch (Exception innerEx)
                {
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"打开应用 '{displayName}' 失败: {innerEx.Message}" } };
                }
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"open_app 执行失败: {ex.Message}" } };
            }
        });
    }

    /// <summary>
    /// 执行 close_app 工具:关闭指定的应用程序进程。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteCloseAppAsync(string argumentsJson)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                string? processName = null;
                if (root.TryGetProperty("process_name", out var proc)) processName = proc.GetString();
                else if (root.TryGetProperty("process", out var proc2)) processName = proc2.GetString();
                else if (root.TryGetProperty("name", out var proc3)) processName = proc3.GetString();

                if (string.IsNullOrWhiteSpace(processName))
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "close_app: 缺少 process_name 参数" } };

                processName = processName.Trim();

                // 移除 .exe 后缀（如果存在）
                if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    processName = processName.Substring(0, processName.Length - 4);

                var processes = Process.GetProcessesByName(processName);
                if (processes.Length == 0)
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"未找到进程 '{processName}'，可能已经关闭了喵～" } };

                int closedCount = 0;
                foreach (var process in processes)
                {
                    try
                    {
                        process.CloseMainWindow();
                        process.WaitForExit(2000); // 等待2秒
                        if (!process.HasExited)
                            process.Kill(); // 强制关闭
                        closedCount++;
                    }
                    catch (Exception)
                    {
                        try { process.Kill(); closedCount++; } catch { }
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"已关闭 {closedCount} 个 '{processName}' 进程喵～" } };
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"close_app 执行失败: {ex.Message}" } };
            }
        });
    }

    /// <summary>
    /// 执行 list_apps 工具:列出当前正在运行的所有应用程序进程。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteListAppsAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                var processes = Process.GetProcesses()
                    .Where(p => !string.IsNullOrWhiteSpace(p.MainWindowTitle) || p.ProcessName.Length > 0)
                    .OrderBy(p => p.ProcessName)
                    .ToList();

                var result = new System.Text.StringBuilder();
                result.AppendLine("当前正在运行的进程列表（共 " + processes.Count + " 个）:");
                result.AppendLine();

                // 只显示有窗口标题的进程或重要进程
                var windowedProcesses = processes
                    .Where(p => !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    .ToList();

                if (windowedProcesses.Count > 0)
                {
                    result.AppendLine("=== 有窗口的进程 ===");
                    foreach (var p in windowedProcesses)
                    {
                        try
                        {
                            result.AppendLine($"  {p.ProcessName} - {p.MainWindowTitle}");
                        }
                        catch
                        {
                            result.AppendLine($"  {p.ProcessName}");
                        }
                    }
                    result.AppendLine();
                }

                // 显示一些常见的后台进程
                var commonBackground = processes
                    .Where(p => string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    .Take(20)
                    .ToList();

                if (commonBackground.Count > 0)
                {
                    result.AppendLine("=== 后台进程（前20个） ===");
                    foreach (var p in commonBackground)
                    {
                        result.AppendLine($"  {p.ProcessName} (PID: {p.Id})");
                    }
                }

                return new List<ContentItem> { new ContentItem { Type = "text", Text = result.ToString() } };
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"list_apps 执行失败: {ex.Message}" } };
            }
        });
    }

    #endregion

    #region 定时提醒工具

    /// <summary>
    /// 执行 set_reminder 工具:设置定时提醒。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteSetReminderAsync(string argumentsJson)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;

                string? message = null;
                int delaySeconds = 0;

                if (root.TryGetProperty("message", out var msgProp)) message = msgProp.GetString();
                else if (root.TryGetProperty("content", out var contentProp)) message = contentProp.GetString();
                else if (root.TryGetProperty("text", out var textProp)) message = textProp.GetString();

                if (root.TryGetProperty("delay_seconds", out var delayProp))
                {
                    if (delayProp.ValueKind == JsonValueKind.Number)
                        delaySeconds = delayProp.GetInt32();
                    else if (delayProp.ValueKind == JsonValueKind.String)
                        delaySeconds = int.TryParse(delayProp.GetString(), out var parsed) ? parsed : 0;
                }
                else if (root.TryGetProperty("delay", out var delayProp2))
                {
                    if (delayProp2.ValueKind == JsonValueKind.Number)
                        delaySeconds = delayProp2.GetInt32();
                    else if (delayProp2.ValueKind == JsonValueKind.String)
                        delaySeconds = int.TryParse(delayProp2.GetString(), out var parsed2) ? parsed2 : 0;
                }

                if (string.IsNullOrWhiteSpace(message))
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "set_reminder: 缺少 message 参数喵～" } };

                if (delaySeconds <= 0)
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "set_reminder: delay_seconds 必须大于0喵～" } };

                // 生成唯一ID
                string reminderId = $"reminder_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}";

                DateTime triggerTime = DateTime.Now.AddSeconds(delaySeconds);

                // 创建定时器
                var timer = new System.Threading.Timer(_ =>
                {
                    // 触发提醒 - 使用MessageBox显示（需要在UI线程）
                    try
                    {
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            System.Windows.MessageBox.Show(message, "定时提醒", MessageBoxButton.OK, MessageBoxImage.Information);
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[Reminder] 显示提醒失败: {ex.Message}", ex);
                    }

                    // 从列表中移除已触发的提醒
                    lock (_remindersLock)
                    {
                        _reminders.Remove(reminderId);
                    }
                }, null, delaySeconds * 1000, Timeout.Infinite);

                // 添加到提醒列表
                lock (_remindersLock)
                {
                    _reminders[reminderId] = (message, triggerTime, timer);
                }

                return new List<ContentItem>
                {
                    new ContentItem
                    {
                        Type = "text",
                        Text = $"已设置定时提醒喵～\nID: {reminderId}\n内容: {message}\n触发时间: {triggerTime:yyyy-MM-dd HH:mm:ss}\n延迟: {delaySeconds} 秒"
                    }
                };
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"set_reminder 执行失败: {ex.Message}" } };
            }
        });
    }

    /// <summary>
    /// 执行 list_reminders 工具:列出当前所有活动的定时提醒。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteListRemindersAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                lock (_remindersLock)
                {
                    if (_reminders.Count == 0)
                        return new List<ContentItem> { new ContentItem { Type = "text", Text = "当前没有活动的定时提醒喵～" } };

                    var result = new System.Text.StringBuilder();
                    result.AppendLine($"当前活动的定时提醒（共 {_reminders.Count} 个）:");
                    result.AppendLine();

                    foreach (var kvp in _reminders)
                    {
                        var remainingSeconds = (int)(kvp.Value.TriggerTime - DateTime.Now).TotalSeconds;
                        if (remainingSeconds < 0) remainingSeconds = 0;
                        result.AppendLine($"ID: {kvp.Key}");
                        result.AppendLine($"  内容: {kvp.Value.Message}");
                        result.AppendLine($"  触发时间: {kvp.Value.TriggerTime:yyyy-MM-dd HH:mm:ss}");
                        result.AppendLine($"  剩余时间: {remainingSeconds} 秒");
                        result.AppendLine();
                    }

                    return new List<ContentItem> { new ContentItem { Type = "text", Text = result.ToString() } };
                }
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"list_reminders 执行失败: {ex.Message}" } };
            }
        });
    }

    /// <summary>
    /// 执行 cancel_reminder 工具:取消指定的定时提醒。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteCancelReminderAsync(string argumentsJson)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                string? reminderId = null;

                if (root.TryGetProperty("reminder_id", out var idProp)) reminderId = idProp.GetString();
                else if (root.TryGetProperty("id", out var idProp2)) reminderId = idProp2.GetString();

                if (string.IsNullOrWhiteSpace(reminderId))
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "cancel_reminder: 缺少 reminder_id 参数喵～" } };

                lock (_remindersLock)
                {
                    if (!_reminders.TryGetValue(reminderId, out var reminder))
                        return new List<ContentItem> { new ContentItem { Type = "text", Text = $"未找到提醒 ID '{reminderId}'，可能已经触发或被取消了喵～" } };

                    // 停止定时器
                    reminder.Timer.Dispose();

                    // 移除提醒
                    _reminders.Remove(reminderId);

                    return new List<ContentItem>
                    {
                        new ContentItem
                        {
                            Type = "text",
                            Text = $"已取消定时提醒 '{reminderId}'喵～\n内容: {reminder.Message}"
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"cancel_reminder 执行失败: {ex.Message}" } };
            }
        });
    }

    #endregion

    #region Helper Methods

    private static void AddPptTitleShape(ShapeTree shapeTree, string title, string subtitle, int x, int y, int w, int h, bool isTitlePage)
    {
        var shapeId = (uint)(shapeTree.Descendants<Shape>().Count() + 1);
        
        // 创建标题文本框
        var titleShape = new Shape();
        titleShape.NonVisualShapeProperties = new NonVisualShapeProperties(
            new NonVisualDrawingProperties { Id = shapeId, Name = "Title" },
            new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
            new ApplicationNonVisualDrawingProperties());
        
        titleShape.ShapeProperties = new ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = x * 9525, Y = y * 9525 },
                new A.Extents { Cx = w * 9525, Cy = h * 9525 }),
            new A.PresetGeometry { Preset = A.ShapeTypeValues.Rectangle },
            new A.NoFill());
        
        titleShape.TextBody = new TextBody(
            new A.BodyProperties { Anchor = A.TextAnchoringTypeValues.Center },
            new A.ListStyle(),
            new A.Paragraph(
                new A.Run(
                    new A.RunProperties { Bold = true, FontSize = 3200 },
                    new A.Text(title))));
        
        shapeTree.Append(titleShape);

        if (!string.IsNullOrEmpty(subtitle))
        {
            var subShapeId = shapeId + 1;
            var subShape = new Shape();
            subShape.NonVisualShapeProperties = new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = subShapeId, Name = "Subtitle" },
                new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties());
            
            subShape.ShapeProperties = new ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x * 9525, Y = (y + 60) * 9525 },
                    new A.Extents { Cx = w * 9525, Cy = (h - 60) * 9525 }),
                new A.PresetGeometry { Preset = A.ShapeTypeValues.Rectangle },
                new A.NoFill());
            
            subShape.TextBody = new TextBody(
                new A.BodyProperties { Anchor = A.TextAnchoringTypeValues.Center },
                new A.ListStyle(),
                new A.Paragraph(
                    new A.Run(
                        new A.RunProperties { FontSize = 2000 },
                        new A.Text(subtitle))));
            
            shapeTree.Append(subShape);
        }
    }

    private static void AddPptTextShape(ShapeTree shapeTree, string text, int x, int y, int w, int h)
    {
        var shapeId = (uint)(shapeTree.Descendants<Shape>().Count() + 1);
        
        var shape = new Shape();
        shape.NonVisualShapeProperties = new NonVisualShapeProperties(
            new NonVisualDrawingProperties { Id = shapeId, Name = "Content" },
            new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
            new ApplicationNonVisualDrawingProperties());
        
        shape.ShapeProperties = new ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = x * 9525, Y = y * 9525 },
                new A.Extents { Cx = w * 9525, Cy = h * 9525 }),
            new A.PresetGeometry { Preset = A.ShapeTypeValues.Rectangle },
            new A.NoFill());
        
        shape.TextBody = new TextBody(
            new A.BodyProperties { Anchor = A.TextAnchoringTypeValues.Top, Wrap = A.TextWrappingValues.Square },
            new A.ListStyle(),
            new A.Paragraph(
                new A.Run(
                    new A.RunProperties { FontSize = 1800 },
                    new A.Text(text))));
        
        shapeTree.Append(shape);
    }

    private static async Task AddPptImage(SlidePart slidePart, ShapeTree shapeTree, string imageUrl, int x, int y, int w, int h)
    {
        try
        {
            using var http = new HttpClient();
            byte[] imgBytes = await http.GetByteArrayAsync(imageUrl);

            var imagePart = slidePart.AddImagePart(ImagePartType.Png);
            imagePart.FeedData(new MemoryStream(imgBytes));

            var shapeId = (uint)(shapeTree.Descendants<Shape>().Count() + 1);
            
            var shape = new Shape();
            shape.NonVisualShapeProperties = new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = shapeId, Name = "Image" },
                new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties());
            
            shape.ShapeProperties = new ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x * 9525, Y = y * 9525 },
                    new A.Extents { Cx = w * 9525, Cy = h * 9525 }),
                new A.BlipFill(
                    new A.Blip { Embed = slidePart.GetIdOfPart(imagePart) },
                    new A.Stretch(new A.FillRectangle())),
                new A.PresetGeometry { Preset = A.ShapeTypeValues.Rectangle });
            
            shapeTree.Append(shape);
        }
        catch { }
    }

    private static SS.Cell CreateTextCell(int column, int row, string value)
    {
        return new SS.Cell
        {
            CellReference = $"{GetColumnName(column)}{row}",
            CellValue = new SS.CellValue(value),
            DataType = SS.CellValues.String
        };
    }

    private static string GetColumnName(int index)
    {
        string result = "";
        while (index > 0)
        {
            index--;
            result = (char)('A' + (index % 26)) + result;
            index /= 26;
        }
        return result;
    }

    private static string GetCellValue(SS.Cell cell, WorkbookPart workbookPart)
    {
        if (cell.CellValue == null) return "";
        
        string value = cell.CellValue.Text;
        if (cell.DataType != null && cell.DataType.Value == SS.CellValues.SharedString)
        {
            var stringTable = workbookPart.GetPartsOfType<SharedStringTablePart>().FirstOrDefault();
            if (stringTable != null && stringTable.SharedStringTable != null)
            {
                int idx = int.Parse(value);
                if (idx < stringTable.SharedStringTable.Elements().Count())
                    value = stringTable.SharedStringTable.Elements().ElementAt(idx).InnerText;
            }
        }
        return value;
    }

    #endregion

    #region 窗口管理工具

    /// <summary>
    /// User32.dll API声明。
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    /// <summary>
    /// 执行 focus_window 工具:将指定进程的窗口置于前台。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteFocusWindowAsync(string argumentsJson)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                string? processName = null;
                if (root.TryGetProperty("process_name", out var proc)) processName = proc.GetString();
                else if (root.TryGetProperty("process", out var proc2)) processName = proc2.GetString();
                else if (root.TryGetProperty("name", out var proc3)) processName = proc3.GetString();

                if (string.IsNullOrWhiteSpace(processName))
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "focus_window: 缺少 process_name 参数" } };

                processName = processName.Trim();
                if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    processName = processName.Substring(0, processName.Length - 4);

                var processes = Process.GetProcessesByName(processName);
                if (processes.Length == 0)
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"未找到进程 '{processName}'，可能没有运行喵～" } };

                IntPtr targetWnd = IntPtr.Zero;
                foreach (var p in processes)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle))
                        {
                            targetWnd = p.MainWindowHandle;
                            break;
                        }
                    }
                    catch { }
                }

                if (targetWnd == IntPtr.Zero)
                {
                    foreach (var p in processes)
                    {
                        try
                        {
                            if (p.MainWindowHandle != IntPtr.Zero)
                            {
                                targetWnd = p.MainWindowHandle;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (targetWnd == IntPtr.Zero)
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"进程 '{processName}' 没有找到可见窗口喵～" } };

                try
                {
                    ShowWindow(targetWnd, SW_RESTORE);
                    SetForegroundWindow(targetWnd);
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"已将 '{processName}' 窗口置于前台喵～" } };
                }
                catch (Exception ex)
                {
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"切换窗口失败: {ex.Message}" } };
                }
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"focus_window 执行失败: {ex.Message}" } };
            }
        });
    }

    /// <summary>
    /// 执行 minimize_window 工具:将指定进程的窗口最小化。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteMinimizeWindowAsync(string argumentsJson)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                string? processName = null;
                if (root.TryGetProperty("process_name", out var proc)) processName = proc.GetString();
                else if (root.TryGetProperty("process", out var proc2)) processName = proc2.GetString();
                else if (root.TryGetProperty("name", out var proc3)) processName = proc3.GetString();

                if (string.IsNullOrWhiteSpace(processName))
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "minimize_window: 缺少 process_name 参数" } };

                processName = processName.Trim();
                if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    processName = processName.Substring(0, processName.Length - 4);

                var processes = Process.GetProcessesByName(processName);
                if (processes.Length == 0)
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"未找到进程 '{processName}'，可能已经关闭了喵～" } };

                int minimizedCount = 0;
                foreach (var p in processes)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            ShowWindow(p.MainWindowHandle, SW_MINIMIZE);
                            minimizedCount++;
                        }
                    }
                    catch { }
                }

                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"已将 {minimizedCount} 个 '{processName}' 窗口最小化喵～" } };
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"minimize_window 执行失败: {ex.Message}" } };
            }
        });
    }

    #endregion

    #region 键盘输入工具

    /// <summary>
    /// SendInput API相关结构。
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION DUMMYUNIONNAME;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [System.Runtime.InteropServices.FieldOffset(0)]
        public KEYBDINPUT ki;
        [System.Runtime.InteropServices.FieldOffset(0)]
        public MOUSEINPUT mi;
        [System.Runtime.InteropServices.FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    /// <summary>
    /// 执行 type_text 工具:模拟键盘输入文本。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteTypeTextAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string? text = null;
            if (root.TryGetProperty("text", out var textProp)) text = textProp.GetString();
            else if (root.TryGetProperty("content", out var contentProp)) text = contentProp.GetString();
            else if (root.TryGetProperty("message", out var msgProp)) text = msgProp.GetString();

            if (string.IsNullOrWhiteSpace(text))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "type_text: 缺少 text 参数" } };

            text = text.Trim();
            if (text.Length > 5000)
                text = text.Substring(0, 5000);

            try
            {
                bool success = false;
                System.Windows.IDataObject? originalClipboard = null;

                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    try
                    {
                        originalClipboard = System.Windows.Clipboard.GetDataObject();
                        System.Windows.Clipboard.SetText(text);
                        success = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[TypeText] 剪贴板设置失败: {ex.Message}", ex);
                    }
                });

                if (success)
                {
                    await Task.Delay(50);
                    SendCtrlV();
                    await Task.Delay(100);

                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        try
                        {
                            if (originalClipboard != null)
                            {
                                System.Windows.Clipboard.SetDataObject(originalClipboard);
                            }
                            else
                            {
                                System.Windows.Clipboard.Clear();
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[TypeText] 剪贴板恢复失败: {ex.Message}", ex);
                        }
                    });

                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"已成功输入 {text.Length} 个字符喵～" } };
                }
                else
                {
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = "输入文本失败: 无法设置剪贴板" } };
                }
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"输入文本失败: {ex.Message}" } };
            }
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"type_text 执行失败: {ex.Message}" } };
        }
    }

    /// <summary>
    /// 发送 Ctrl+V 粘贴快捷键。
    /// </summary>
    private static void SendCtrlV()
    {
        var inputs = new List<INPUT>();

        AddKeyInput(inputs, 0x11, false); // Ctrl down
        AddKeyInput(inputs, 0x56, false); // V down
        AddKeyInput(inputs, 0x56, true);  // V up
        AddKeyInput(inputs, 0x11, true);  // Ctrl up

        SendInput((uint)inputs.Count, inputs.ToArray(), System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)));
    }

    /// <summary>
    /// 添加键盘按键输入。
    /// </summary>
    private static void AddKeyInput(List<INPUT> inputs, ushort vkCode, bool isKeyUp)
    {
        inputs.Add(new INPUT
        {
            Type = INPUT_KEYBOARD,
            DUMMYUNIONNAME = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vkCode,
                    wScan = 0,
                    dwFlags = isKeyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        });
    }

    /// <summary>
    /// 添加Unicode字符输入。
    /// </summary>
    private static void AddUnicodeInput(List<INPUT> inputs, char c)
    {
        ushort unicode = (ushort)c;
        
        inputs.Add(new INPUT
        {
            Type = INPUT_KEYBOARD,
            DUMMYUNIONNAME = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = unicode,
                    dwFlags = KEYEVENTF_UNICODE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        });

        inputs.Add(new INPUT
        {
            Type = INPUT_KEYBOARD,
            DUMMYUNIONNAME = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = unicode,
                    dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        });
    }

    #endregion

    #region 屏幕涂鸦工具

    /// <summary>
    /// 涂鸦窗口单例。
    /// </summary>
    private static System.Windows.Window? _drawingWindow;
    private static System.Windows.Controls.Canvas? _drawingCanvas;
    private static readonly object _drawingLock = new object();

    /// <summary>
    /// 创建或获取涂鸦窗口。
    /// </summary>
    private static System.Windows.Controls.Canvas GetDrawingCanvas()
    {
        lock (_drawingLock)
        {
            if (_drawingWindow == null || !_drawingWindow.IsLoaded)
            {
                _drawingWindow = new System.Windows.Window
                {
                    WindowStyle = System.Windows.WindowStyle.None,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Topmost = true,
                    ShowInTaskbar = false,
                    WindowState = System.Windows.WindowState.Maximized
                };

                _drawingCanvas = new System.Windows.Controls.Canvas
                {
                    Width = System.Windows.SystemParameters.PrimaryScreenWidth,
                    Height = System.Windows.SystemParameters.PrimaryScreenHeight
                };

                _drawingWindow.Content = _drawingCanvas;
                _drawingWindow.Show();
            }

            return _drawingCanvas;
        }
    }

    /// <summary>
    /// 解析颜色字符串。
    /// </summary>
    private static System.Windows.Media.Color ParseColor(string colorStr)
    {
        if (string.IsNullOrWhiteSpace(colorStr))
            return System.Windows.Media.Colors.Red;

        colorStr = colorStr.Trim().ToLowerInvariant();

        var colorDict = new Dictionary<string, System.Windows.Media.Color>
        {
            { "red", System.Windows.Media.Colors.Red },
            { "green", System.Windows.Media.Colors.Green },
            { "blue", System.Windows.Media.Colors.Blue },
            { "yellow", System.Windows.Media.Colors.Yellow },
            { "black", System.Windows.Media.Colors.Black },
            { "white", System.Windows.Media.Colors.White },
            { "gray", System.Windows.Media.Colors.Gray },
            { "purple", System.Windows.Media.Colors.Purple },
            { "orange", System.Windows.Media.Colors.Orange },
            { "pink", System.Windows.Media.Colors.Pink },
            { "cyan", System.Windows.Media.Colors.Cyan },
            { "magenta", System.Windows.Media.Colors.Magenta }
        };

        if (colorDict.TryGetValue(colorStr, out var color))
            return color;

        if (colorStr.StartsWith("#"))
        {
            try
            {
                return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorStr);
            }
            catch { }
        }

        return System.Windows.Media.Colors.Red;
    }

    /// <summary>
    /// 执行 draw_on_screen 工具:在屏幕上绘制图形。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteDrawOnScreenAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            string? type = null;
            double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
            string? text = null;
            string colorStr = "red";
            double lineWidth = 3;

            if (root.TryGetProperty("type", out var typeProp)) type = typeProp.GetString();
            if (root.TryGetProperty("x1", out var x1Prop)) x1 = x1Prop.GetDouble();
            if (root.TryGetProperty("y1", out var y1Prop)) y1 = y1Prop.GetDouble();
            if (root.TryGetProperty("x2", out var x2Prop)) x2 = x2Prop.GetDouble();
            if (root.TryGetProperty("y2", out var y2Prop)) y2 = y2Prop.GetDouble();
            if (root.TryGetProperty("text", out var textProp)) text = textProp.GetString();
            if (root.TryGetProperty("color", out var colorProp)) colorStr = colorProp.GetString() ?? "red";
            if (root.TryGetProperty("line_width", out var lineWidthProp)) lineWidth = lineWidthProp.GetDouble();

            if (string.IsNullOrWhiteSpace(type))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "draw_on_screen: 缺少 type 参数" } };

            string drawType = type.ToLowerInvariant();

            try
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    var canvas = GetDrawingCanvas();
                    var color = ParseColor(colorStr);
                    var brush = new System.Windows.Media.SolidColorBrush(color);
                    var transparentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent);

                    switch (drawType)
                    {
                        case "line":
                            var line = new System.Windows.Shapes.Line
                            {
                                X1 = x1,
                                Y1 = y1,
                                X2 = x2,
                                Y2 = y2,
                                Stroke = brush,
                                StrokeThickness = lineWidth
                            };
                            canvas.Children.Add(line);
                            break;

                        case "rect":
                            var rect = new System.Windows.Shapes.Rectangle
                            {
                                Width = Math.Abs(x2 - x1),
                                Height = Math.Abs(y2 - y1),
                                Stroke = brush,
                                StrokeThickness = lineWidth,
                                Fill = transparentBrush
                            };
                            System.Windows.Controls.Canvas.SetLeft(rect, Math.Min(x1, x2));
                            System.Windows.Controls.Canvas.SetTop(rect, Math.Min(y1, y2));
                            canvas.Children.Add(rect);
                            break;

                        case "circle":
                            double radius = Math.Min(Math.Abs(x2 - x1), Math.Abs(y2 - y1)) / 2;
                            double cx = (x1 + x2) / 2;
                            double cy = (y1 + y2) / 2;
                            var ellipse = new System.Windows.Shapes.Ellipse
                            {
                                Width = radius * 2,
                                Height = radius * 2,
                                Stroke = brush,
                                StrokeThickness = lineWidth,
                                Fill = transparentBrush
                            };
                            System.Windows.Controls.Canvas.SetLeft(ellipse, cx - radius);
                            System.Windows.Controls.Canvas.SetTop(ellipse, cy - radius);
                            canvas.Children.Add(ellipse);
                            break;

                        case "text":
                            if (string.IsNullOrWhiteSpace(text))
                                text = "Hello";
                            var textBlock = new System.Windows.Controls.TextBlock
                            {
                                Text = text,
                                Foreground = brush,
                                FontSize = 24,
                                FontWeight = System.Windows.FontWeights.Bold
                            };
                            System.Windows.Controls.Canvas.SetLeft(textBlock, x1);
                            System.Windows.Controls.Canvas.SetTop(textBlock, y1);
                            canvas.Children.Add(textBlock);
                            break;

                        default:
                            throw new ArgumentException($"未知图形类型: {type}");
                    }
                });

                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"已在屏幕上绘制 {type} 图形喵～" } };
            }
            catch (Exception ex)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"绘制图形失败: {ex.Message}" } };
            }
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"draw_on_screen 执行失败: {ex.Message}" } };
        }
    }

    /// <summary>
    /// 执行 clear_drawings 工具:清空屏幕上所有涂鸦。
    /// </summary>
    private static async Task<List<ContentItem>> ExecuteClearDrawingsAsync()
    {
        try
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                if (_drawingCanvas != null)
                {
                    _drawingCanvas.Children.Clear();
                }

                if (_drawingWindow != null)
                {
                    _drawingWindow.Close();
                    _drawingWindow = null;
                    _drawingCanvas = null;
                }
            });

            return new List<ContentItem> { new ContentItem { Type = "text", Text = "已清空屏幕上所有涂鸦喵～" } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"清空涂鸦失败: {ex.Message}" } };
        }
    }

    #region 系统控制类工具

    private static async Task<List<ContentItem>> ExecuteSetVolumeAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            double? level = null;
            bool? mute = null;
            double? delta = null;

            if (root.TryGetProperty("level", out var levelElem)) level = levelElem.GetDouble();
            if (root.TryGetProperty("mute", out var muteElem)) mute = muteElem.GetBoolean();
            if (root.TryGetProperty("delta", out var deltaElem)) delta = deltaElem.GetDouble();

            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device == null)
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "无法获取音频设备喵～" } };

            if (mute.HasValue)
            {
                device.AudioEndpointVolume.Mute = mute.Value;
                return new List<ContentItem> { new ContentItem { Type = "text", Text = mute.Value ? "已静音喵～" : "已取消静音喵～" } };
            }

            if (level.HasValue)
            {
                float vol = (float)(Math.Max(0, Math.Min(100, level.Value)) / 100);
                device.AudioEndpointVolume.MasterVolumeLevelScalar = vol;
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"音量已设置为 {level.Value:F0}% 喵～" } };
            }

            if (delta.HasValue)
            {
                float currentVol = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                float currentPercent = currentVol * 100;
                float newPercent = Math.Max(0, Math.Min(100, currentPercent + (float)delta.Value));
                float newVol = newPercent / 100;
                device.AudioEndpointVolume.MasterVolumeLevelScalar = newVol;
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"音量已调整至 {newPercent:F0}% 喵～" } };
            }

            float currentVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar;
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"当前音量: {currentVolume * 100:F0}% 喵～" } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"音量调节失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteSetBrightnessAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("level", out var levelElem))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "set_brightness: 缺少 level 参数" } };

            double level = levelElem.GetDouble();
            level = Math.Max(0, Math.Min(100, level));

            foreach (var display in System.Windows.Forms.Screen.AllScreens)
            {
                IntPtr hMonitor = display.GetHashCode() != 0 ? new IntPtr(display.GetHashCode()) : IntPtr.Zero;
                
                int result = GetPhysicalMonitorsFromHMONITOR(hMonitor, 1, out IntPtr hPhysicalMonitor);
                if (result == 1)
                {
                    try
                    {
                        SetMonitorBrightness(hPhysicalMonitor, (uint)level);
                    }
                    finally
                    {
                        DestroyPhysicalMonitors(1, ref hPhysicalMonitor);
                    }
                }
            }

            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"亮度已调整至 {level:F0}% 喵～" } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"亮度调节失败(可能不支持DDC): {ex.Message}" } };
        }
    }

    [System.Runtime.InteropServices.DllImport("dxva2.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, out IntPtr hPhysicalMonitor);

    [System.Runtime.InteropServices.DllImport("dxva2.dll")]
    private static extern bool SetMonitorBrightness(IntPtr hPhysicalMonitor, uint dwNewBrightness);

    [System.Runtime.InteropServices.DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, ref IntPtr phPhysicalMonitors);

    private static async Task<List<ContentItem>> ExecuteGetBatteryAsync()
    {
        try
        {
            var searcher = new System.Management.ManagementObjectSearcher("root\\WMI", "SELECT * FROM BatteryStatus");
            var result = new System.Text.StringBuilder();
            int count = 0;

            foreach (var obj in searcher.Get())
            {
                count++;
                int charge = obj["ChargeStatus"] != null ? Convert.ToInt32(obj["ChargeStatus"]) : 0;
                int capacity = obj["Capacity"] != null ? Convert.ToInt32(obj["Capacity"]) : -1;
                bool charging = charge == 2 || charge == 3;
                
                result.AppendLine($"电池 {count}: {capacity}%{(charging ? " (充电中)" : "")}");
            }

            if (count == 0)
            {
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "未检测到电池设备喵～" } };
            }

            return new List<ContentItem> { new ContentItem { Type = "text", Text = result.ToString().Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"电池查询失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteLockScreenAsync()
    {
        try
        {
            var user32 = new System.Runtime.InteropServices.DllImportAttribute("user32.dll");
            bool result = LockWorkStation();
            return new List<ContentItem> { new ContentItem { Type = "text", Text = result ? "已锁定屏幕喵～" : "锁定屏幕失败喵～" } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"锁定屏幕失败: {ex.Message}" } };
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    private static async Task<List<ContentItem>> ExecuteEmptyRecycleBinAsync()
    {
        try
        {
            SHEmptyRecycleBin(IntPtr.Zero, null, 0);
            return new List<ContentItem> { new ContentItem { Type = "text", Text = "已清空回收站喵～" } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"清空回收站失败: {ex.Message}" } };
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

    #endregion

    #region 文件与生产力工具

    private static async Task<List<ContentItem>> ExecuteSearchFilesAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("pattern", out var patternElem))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "search_files: 缺少 pattern 参数" } };

            string pattern = patternElem.GetString() ?? "*";
            string path = root.TryGetProperty("path", out var p) ? p.GetString() ?? Environment.CurrentDirectory : Environment.CurrentDirectory;
            int maxResults = root.TryGetProperty("max_results", out var m) ? (int)m.GetDouble() : 20;

            if (!Directory.Exists(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"目录不存在: {path}" } };

            string[] searchPatterns = pattern.Split('|');
            var results = new List<string>();

            foreach (var sp in searchPatterns)
            {
                try
                {
                    var files = Directory.EnumerateFiles(path, sp.Trim(), SearchOption.AllDirectories)
                        .Take(maxResults)
                        .ToList();
                    results.AddRange(files);
                }
                catch { }
            }

            results = results.Distinct().Take(maxResults).ToList();

            if (results.Count == 0)
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"未找到匹配文件喵～" } };

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"找到 {results.Count} 个匹配文件:");
            foreach (var file in results)
            {
                sb.AppendLine($"  {file}");
            }

            return new List<ContentItem> { new ContentItem { Type = "text", Text = sb.ToString().Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"文件搜索失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteClipboardHistoryAsync()
    {
        try
        {
            var result = new System.Text.StringBuilder();
            
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (text.Length > 500)
                    text = text.Substring(0, 500) + "...";
                result.AppendLine("剪贴板文本内容:");
                result.AppendLine(text);
            }
            else
            {
                result.AppendLine("剪贴板中没有文本内容喵～");
            }

            if (Clipboard.ContainsImage())
            {
                result.AppendLine("\n剪贴板中包含图片喵～");
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                result.AppendLine("\n剪贴板文件列表:");
                foreach (string file in files)
                {
                    result.AppendLine($"  {file}");
                }
            }

            return new List<ContentItem> { new ContentItem { Type = "text", Text = result.ToString().Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"读取剪贴板失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteQuickNoteAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("text", out var textElem))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "quick_note: 缺少 text 参数" } };

            string text = textElem.GetString() ?? "";
            string filePath = root.TryGetProperty("file_path", out var p) ? p.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(filePath))
            {
                filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "QuickNote.txt");
            }

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string content = $"[{timestamp}] {text}\r\n";

            File.AppendAllText(filePath, content, System.Text.Encoding.UTF8);

            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"便签已保存到: {filePath} 喵～" } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"保存便签失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteReadTodoAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string filePath = root.TryGetProperty("file_path", out var p) ? p.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(filePath))
            {
                filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Todo.txt");
            }

            if (!File.Exists(filePath))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"待办文件不存在: {filePath}" } };

            string content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            return new List<ContentItem> { new ContentItem { Type = "text", Text = content.Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"读取待办失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteFolderSizeAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("path", out var pathElem))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "folder_size: 缺少 path 参数" } };

            string path = pathElem.GetString() ?? "";
            if (!Directory.Exists(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"目录不存在: {path}" } };

            long size = new System.IO.DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);

            string sizeStr = FormatFileSize(size);

            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"文件夹 {path} 占用空间: {sizeStr} 喵～" } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"计算文件夹大小失败: {ex.Message}" } };
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F2} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static async Task<List<ContentItem>> ExecuteBatchRenamePreviewAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("path", out var pathElem))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "batch_rename_preview: 缺少 path 参数" } };
            if (!root.TryGetProperty("pattern", out var patternElem))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "batch_rename_preview: 缺少 pattern 参数" } };

            string path = pathElem.GetString() ?? "";
            string pattern = patternElem.GetString() ?? "";
            string extension = root.TryGetProperty("extension", out var e) ? e.GetString() ?? "" : "";

            if (!Directory.Exists(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"目录不存在: {path}" } };

            string[] files = string.IsNullOrEmpty(extension) 
                ? Directory.GetFiles(path) 
                : Directory.GetFiles(path, $"*{extension}");

            if (files.Length == 0)
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "目录中没有匹配的文件喵～" } };

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("批量重命名预览(仅预览,未执行):");
            sb.AppendLine("─────────────────────────────");

            for (int i = 0; i < files.Length; i++)
            {
                string oldName = Path.GetFileName(files[i]);
                string ext = Path.GetExtension(files[i]);
                string newName = pattern.Replace("{index}", (i + 1).ToString("D3")) + ext;
                sb.AppendLine($"{oldName} → {newName}");
            }

            sb.AppendLine("─────────────────────────────");
            sb.AppendLine($"共 {files.Length} 个文件待重命名");

            return new List<ContentItem> { new ContentItem { Type = "text", Text = sb.ToString().Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"批量重命名预览失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteExecuteCommandAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("command", out var cmdElem))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "execute_command: 缺少 command 参数" } };

            string command = cmdElem.GetString() ?? "";
            string workingDir = root.TryGetProperty("working_dir", out var wd) ? wd.GetString() ?? Environment.CurrentDirectory : Environment.CurrentDirectory;

            if (!Directory.Exists(workingDir))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"工作目录不存在: {workingDir}" } };

            string outputFile = Path.GetTempFileName() + ".cmdout";
            string errorFile = Path.GetTempFileName() + ".cmderr";

            string cmdLine = $"cmd.exe /c \"{command} > \"{outputFile}\" 2> \"{errorFile}\"";

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = cmdLine.Split(' ')[0],
                Arguments = string.Join(" ", cmdLine.Split(' ').Skip(1)),
                WorkingDirectory = workingDir,
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };

            using Process process = Process.Start(psi)!;

            string trackingFile = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                "running_commands.tmp"
            );

            lock (typeof(ToolExecutor))
            {
                File.AppendAllText(trackingFile, $"{process.Id}|{DateTime.Now:yyyy-MM-dd HH:mm:ss}|{command}\n");
            }

            await process.WaitForExitAsync();

            lock (typeof(ToolExecutor))
            {
                var lines = File.Exists(trackingFile) ? File.ReadAllLines(trackingFile).ToList() : new List<string>();
                lines.RemoveAll(l => l.StartsWith(process.Id + "|"));
                File.WriteAllLines(trackingFile, lines);
            }

            string output = File.Exists(outputFile) ? File.ReadAllText(outputFile) : "";
            string error = File.Exists(errorFile) ? File.ReadAllText(errorFile) : "";

            File.Delete(outputFile);
            File.Delete(errorFile);

            string result = $"命令执行完成!\n退出码: {process.ExitCode}\n\n输出:\n{output}";
            if (!string.IsNullOrWhiteSpace(error))
                result += $"\n错误:\n{error}";

            return new List<ContentItem> { new ContentItem { Type = "text", Text = result.Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"命令执行失败: {ex.Message}" } };
        }
    }

    #endregion

    #region 网络与信息获取工具

    private static async Task<List<ContentItem>> ExecuteGetPublicIpAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            string ip = await client.GetStringAsync("https://api.ipify.org");
            ip = ip.Trim();

            string location = "";
            try
            {
                string geoJson = await client.GetStringAsync($"https://ipapi.co/{ip}/json/");
                using var doc = JsonDocument.Parse(geoJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("city", out var city)) location += city.GetString() ?? "";
                if (root.TryGetProperty("region", out var region)) 
                    location += (location.Length > 0 ? ", " : "") + region.GetString();
                if (root.TryGetProperty("country_name", out var country))
                    location += (location.Length > 0 ? ", " : "") + country.GetString();
            }
            catch { }

            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"公网IP: {ip}\n位置: {(string.IsNullOrEmpty(location) ? "未知" : location)} 喵～" } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"获取公网IP失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteSpeedTestAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("正在进行网速测试...");

            string testUrl = "http://speedtest.ftp.otenet.gr/files/test100k.db";
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            byte[] data = await client.GetByteArrayAsync(testUrl);
            stopwatch.Stop();

            double downloadSpeed = (data.Length * 8.0 / 1024 / 1024) / (stopwatch.Elapsed.TotalSeconds);
            
            sb.AppendLine($"下载速度: {downloadSpeed:F2} Mbps");
            sb.AppendLine("(注: 此为轻量级测试,仅供参考)");

            return new List<ContentItem> { new ContentItem { Type = "text", Text = sb.ToString().Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"网速测试失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecutePingHostAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("host", out var hostElem))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "ping_host: 缺少 host 参数" } };

            string host = hostElem.GetString() ?? "";
            int? port = root.TryGetProperty("port", out var p) ? (int?)p.GetDouble() : null;

            if (port.HasValue)
            {
                using var tcpClient = new System.Net.Sockets.TcpClient();
                var connectTask = tcpClient.ConnectAsync(host, port.Value);
                bool success = await Task.WhenAny(connectTask, Task.Delay(5000)) == connectTask;
                if (success)
                {
                    try { connectTask.Wait(); } catch { success = false; }
                }

                return new List<ContentItem> { new ContentItem { Type = "text", Text = success 
                    ? $"TCP连接成功: {host}:{port.Value} 喵～" 
                    : $"TCP连接失败: {host}:{port.Value} (超时或拒绝) 喵～" } };
            }
            else
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync(host, 5000);

                return new List<ContentItem> { new ContentItem { Type = "text", Text = reply.Status == System.Net.NetworkInformation.IPStatus.Success
                    ? $"Ping成功! {host} ({reply.Address}) - 延迟 {reply.RoundtripTime}ms 喵～"
                    : $"Ping失败: {reply.Status} 喵～" } };
            }
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"连通性检测失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteGetHotSearchAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string source = root.TryGetProperty("source", out var s) ? s.GetString()?.ToLower() ?? "weibo" : "weibo";

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            string result = "";

            switch (source)
            {
                case "weibo":
                    {
                        string weiboHtml = await client.GetStringAsync("https://s.weibo.com/top/summary?cate=realtimehot");
                        var weiboMatches = System.Text.RegularExpressions.Regex.Matches(weiboHtml, @"<td class=""td-02"">.*?href="".*?"">(.*?)</a>.*?</td>", System.Text.RegularExpressions.RegexOptions.Singleline);
                        result = "微博实时热搜:\n";
                        for (int i = 0; i < Math.Min(3, weiboMatches.Count); i++)
                        {
                            result += $"{i + 1}. {weiboMatches[i].Groups[1].Value.Trim()}\n";
                        }
                    }
                    break;

                case "bilibili":
                    {
                        string biliJson = await client.GetStringAsync("https://api.bilibili.com/x/web-interface/ranking/v2?rid=0&type=all");
                        using var biliDoc = JsonDocument.Parse(biliJson);
                        var biliData = biliDoc.RootElement.GetProperty("data").GetProperty("list");
                        result = "B站热门:\n";
                        int biliCount = 0;
                        foreach (var item in biliData.EnumerateArray())
                        {
                            if (biliCount >= 3) break;
                            result += $"{biliCount + 1}. {item.GetProperty("title").GetString()}\n";
                            biliCount++;
                        }
                    }
                    break;

                case "zhihu":
                    {
                        string zhihuHtml = await client.GetStringAsync("https://www.zhihu.com/hot");
                        var zhihuMatches = System.Text.RegularExpressions.Regex.Matches(zhihuHtml, @"<h2.*?>(.*?)</h2>", System.Text.RegularExpressions.RegexOptions.Singleline);
                        result = "知乎热榜:\n";
                        for (int i = 0; i < Math.Min(3, zhihuMatches.Count); i++)
                        {
                            result += $"{i + 1}. {zhihuMatches[i].Groups[1].Value.Trim()}\n";
                        }
                    }
                    break;
            }

            if (string.IsNullOrWhiteSpace(result))
                result = "获取热搜失败喵～";

            return new List<ContentItem> { new ContentItem { Type = "text", Text = result.Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"获取热搜失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteConvertCurrencyAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("from", out var fromElem))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "convert_currency: 缺少 from 参数" } };
            if (!root.TryGetProperty("to", out var toElem))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "convert_currency: 缺少 to 参数" } };

            string from = fromElem.GetString()?.ToUpper() ?? "USD";
            string to = toElem.GetString()?.ToUpper() ?? "CNY";
            double amount = root.TryGetProperty("amount", out var a) ? a.GetDouble() : 1;

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            string json = await client.GetStringAsync($"https://api.exchangerate-api.com/v4/latest/{from}");
            using var rateDoc = JsonDocument.Parse(json);
            
            double rate = 0;
            if (rateDoc.RootElement.TryGetProperty("rates", out var rates))
            {
                if (rates.TryGetProperty(to, out var rateVal))
                {
                    rate = rateVal.GetDouble();
                }
            }

            if (rate == 0)
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "未找到汇率数据喵～" } };

            double result = amount * rate;

            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"{amount} {from} = {result:F2} {to} (汇率: 1 {from} = {rate:F4} {to}) 喵～" } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"汇率换算失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteGetCalendarAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? t.GetString()?.ToLower() ?? "calendar" : "calendar";
            string zodiac = root.TryGetProperty("zodiac", out var z) ? z.GetString()?.ToLower() ?? "" : "";

            if (type == "zodiac")
            {
                if (string.IsNullOrWhiteSpace(zodiac))
                    zodiac = GetZodiacSign(DateTime.Now);

                var quotes = new Dictionary<string, string[]>
                {
                    { "aries", new[] { "今天适合冒险，大胆去尝试新事物吧！", "热情是你的武器，今天会有好运气～", "勇敢的白羊座，今天是你的主场！" } },
                    { "taurus", new[] { "今天财运不错，适合理财规划～", "稳重的金牛座，你的坚持会有回报！", "享受美食和舒适，今天宜放松～" } },
                    { "gemini", new[] { "今天思维活跃，适合学习新知识～", "双子座的你，今天社交运爆棚！", "多变是你的魅力，今天会有惊喜～" } },
                    { "cancer", new[] { "今天家庭运好，多陪陪家人吧～", "巨蟹座的温柔，今天会感动身边的人～", "安全感来自内心，今天宜自我充电～" } },
                    { "leo", new[] { "今天气场全开，你是全场的焦点！", "狮子座的自信，无人能挡～", "今天适合展示你的才华！" } },
                    { "virgo", new[] { "今天宜整理收纳，环境整洁心情好～", "处女座的细致，今天会帮你避开麻烦～", "注重细节，今天事事顺利～" } },
                    { "libra", new[] { "今天桃花运不错，适合约会哦～", "天秤座的优雅，今天闪闪发光～", "平衡是关键，今天宜和谐共处～" } },
                    { "scorpio", new[] { "今天直觉敏锐，相信你的第六感！", "天蝎座的神秘，今天更有魅力～", "深度思考，今天会有新发现～" } },
                    { "sagittarius", new[] { "今天适合旅行或户外活动～", "射手座的自由，今天放飞自我！", "乐观向上，今天充满正能量～" } },
                    { "capricorn", new[] { "今天事业运好，努力会有收获～", "摩羯座的踏实，今天值得信赖～", "规划未来，今天宜制定目标～" } },
                    { "aquarius", new[] { "今天创意无限，适合搞创作～", "水瓶座的独特，今天与众不同～", "关心社会，今天宜帮助他人～" } },
                    { "pisces", new[] { "今天充满艺术气息，适合欣赏美～", "双鱼座的浪漫，今天如梦似幻～", "善良是你的天性，今天宜关爱自己～" } }
                };

                if (quotes.TryGetValue(zodiac, out var zodiacQuotes))
                {
                    var rnd = new Random();
                    return new List<ContentItem> { new ContentItem { Type = "text", Text = $"♈ {GetZodiacName(zodiac)}今日运势:\n\n{zodiacQuotes[rnd.Next(zodiacQuotes.Length)]}" } };
                }
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "未知星座喵～" } };
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                DateTime today = DateTime.Now;
                
                sb.AppendLine($"📅 {today.Year}年{today.Month}月{today.Day}日");
                sb.AppendLine($"星期{GetChineseDayOfWeek(today.DayOfWeek)}");
                sb.AppendLine();
                sb.AppendLine("今日黄历(仅供娱乐):");
                sb.AppendLine("宜: 学习、工作、阅读、运动");
                sb.AppendLine("忌: 熬夜、拖延、生气");
                sb.AppendLine();
                sb.AppendLine($"今日星座: {GetZodiacName(GetZodiacSign(today))}");

                return new List<ContentItem> { new ContentItem { Type = "text", Text = sb.ToString().Trim() } };
            }
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"获取黄历失败: {ex.Message}" } };
        }
    }

    private static string GetZodiacSign(DateTime date)
    {
        int month = date.Month;
        int day = date.Day;

        if ((month == 3 && day >= 21) || (month == 4 && day <= 19)) return "aries";
        if ((month == 4 && day >= 20) || (month == 5 && day <= 20)) return "taurus";
        if ((month == 5 && day >= 21) || (month == 6 && day <= 21)) return "gemini";
        if ((month == 6 && day >= 22) || (month == 7 && day <= 22)) return "cancer";
        if ((month == 7 && day >= 23) || (month == 8 && day <= 22)) return "leo";
        if ((month == 8 && day >= 23) || (month == 9 && day <= 22)) return "virgo";
        if ((month == 9 && day >= 23) || (month == 10 && day <= 23)) return "libra";
        if ((month == 10 && day >= 24) || (month == 11 && day <= 22)) return "scorpio";
        if ((month == 11 && day >= 23) || (month == 12 && day <= 21)) return "sagittarius";
        if ((month == 12 && day >= 22) || (month == 1 && day <= 19)) return "capricorn";
        if ((month == 1 && day >= 20) || (month == 2 && day <= 18)) return "aquarius";
        return "pisces";
    }

    private static string GetZodiacName(string zodiac)
    {
        var names = new Dictionary<string, string>
        {
            { "aries", "白羊座" }, { "taurus", "金牛座" }, { "gemini", "双子座" },
            { "cancer", "巨蟹座" }, { "leo", "狮子座" }, { "virgo", "处女座" },
            { "libra", "天秤座" }, { "scorpio", "天蝎座" }, { "sagittarius", "射手座" },
            { "capricorn", "摩羯座" }, { "aquarius", "水瓶座" }, { "pisces", "双鱼座" }
        };
        return names.TryGetValue(zodiac, out var name) ? name : zodiac;
    }

    private static string GetChineseDayOfWeek(DayOfWeek day)
    {
        var days = new[] { "日", "一", "二", "三", "四", "五", "六" };
        return days[(int)day];
    }

    #endregion

    #region 开发与极客工具

    private static async Task<List<ContentItem>> ExecuteCheckPortAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("port", out var portElem))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "check_port: 缺少 port 参数" } };

            int port = (int)portElem.GetDouble();

            var ipProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var tcpConnections = ipProperties.GetActiveTcpConnections();
            var tcpListeners = ipProperties.GetActiveTcpListeners();

            var sb = new System.Text.StringBuilder();

            foreach (var listener in tcpListeners)
            {
                if (listener.Port == port)
                {
                    sb.AppendLine($"端口 {port} 正在监听");
                }
            }

            foreach (var conn in tcpConnections)
            {
                if (conn.LocalEndPoint.Port == port)
                {
                    sb.AppendLine($"端口 {port} 被进程使用: {conn.State}");
                }
            }

            try
            {
                var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_Service WHERE Started = TRUE");
                foreach (var obj in searcher.Get())
                {
                    try
                    {
                        string name = obj["Name"]?.ToString() ?? "";
                        string path = obj["PathName"]?.ToString() ?? "";
                    }
                    catch { }
                }
            }
            catch { }

            if (sb.Length == 0)
            {
                try
                {
                    var procList = Process.GetProcesses();
                    foreach (var proc in procList)
                    {
                        try
                        {
                            foreach (var module in proc.Modules)
                            {
                                // This is a simplified approach
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"端口 {port} 未被占用 喵～" } };
            }

            return new List<ContentItem> { new ContentItem { Type = "text", Text = sb.ToString().Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"端口查询失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteGitCommandAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("command", out var cmdElem))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "git_command: 缺少 command 参数" } };

            string command = cmdElem.GetString()?.ToLower() ?? "";
            string path = root.TryGetProperty("path", out var p) ? p.GetString() ?? Environment.CurrentDirectory : Environment.CurrentDirectory;

            if (!Directory.Exists(path))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = $"目录不存在: {path}" } };

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = command,
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process process = Process.Start(psi)!;
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            string result = output;
            if (!string.IsNullOrWhiteSpace(error))
                result += "\n错误: " + error;

            if (string.IsNullOrWhiteSpace(result))
                result = "Git命令执行完成 喵～";

            return new List<ContentItem> { new ContentItem { Type = "text", Text = result.Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"Git命令执行失败: {ex.Message}" } };
        }
    }

    #endregion

    #region 多媒体与截图工具

    private static async Task<List<ContentItem>> ExecuteColorPickerAsync()
    {
        try
        {
            System.Windows.Forms.Cursor.Hide();
            await Task.Delay(500);

            System.Drawing.Point cursorPos = System.Windows.Forms.Cursor.Position;
            
            using var bitmap = new System.Drawing.Bitmap(1, 1);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(cursorPos.X, cursorPos.Y, 0, 0, new System.Drawing.Size(1, 1));
            
            System.Drawing.Color color = bitmap.GetPixel(0, 0);

            System.Windows.Forms.Cursor.Show();

            string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            string rgb = $"RGB({color.R}, {color.G}, {color.B})";
            string hsl = $"HSL({color.GetHue():F0}, {color.GetSaturation() * 100:F0}%, {color.GetBrightness() * 100:F0}%)";

            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"取色结果:\n坐标: ({cursorPos.X}, {cursorPos.Y})\n十六进制: {hex}\nRGB: {rgb}\nHSL: {hsl} 喵～" } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"取色失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteGenerateQrcodeAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("text", out var textElem))
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "generate_qrcode: 缺少 text 参数" } };

            string text = textElem.GetString() ?? "";
            string url = $"https://v2.xxapi.cn/api/qrcode?return=json&text={Uri.EscapeDataString(text)}";

            using var httpClient = new HttpClient();
            string jsonResult = await httpClient.GetStringAsync(url);

            return new List<ContentItem> { new ContentItem { Type = "text", Text = jsonResult } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"生成二维码失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteToggleThemeAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            string theme = root.TryGetProperty("theme", out var t) ? t.GetString()?.ToLower() ?? "" : "";

            int currentTheme = (int)Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1
            );

            int newTheme = theme switch
            {
                "light" => 1,
                "dark" => 0,
                "system" => 2,
                _ => currentTheme == 1 ? 0 : 1
            };

            Microsoft.Win32.Registry.SetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                newTheme,
                Microsoft.Win32.RegistryValueKind.DWord
            );
            Microsoft.Win32.Registry.SetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme",
                newTheme,
                Microsoft.Win32.RegistryValueKind.DWord
            );

            string themeName = newTheme == 1 ? "浅色" : (newTheme == 2 ? "系统默认" : "深色");
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"已切换到{themeName}主题 喵～" } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"切换主题失败: {ex.Message}" } };
        }
    }

    #endregion

    #region 桌面宠物专属工具

    private static async Task<List<ContentItem>> ExecuteSystemMonitorAsync()
    {
        try
        {
            var cpuUsage = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
            cpuUsage.NextValue();
            await Task.Delay(100);
            double cpu = cpuUsage.NextValue();

            var ram = new System.Diagnostics.PerformanceCounter("Memory", "Available MBytes");
            double availableRam = ram.NextValue();
            double totalRam = new Microsoft.VisualBasic.Devices.ComputerInfo().TotalPhysicalMemory / (1024.0 * 1024);
            double usedRamPercent = ((totalRam - availableRam) / totalRam) * 100;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("系统资源监控:");
            sb.AppendLine($"CPU占用: {cpu:F1}%");
            sb.AppendLine($"内存占用: {usedRamPercent:F1}% ({(totalRam - availableRam):F0} MB / {totalRam:F0} MB)");

            if (cpu > 80) sb.AppendLine("\n⚠️ CPU占用过高，注意降温哦～");
            if (usedRamPercent > 85) sb.AppendLine("\n⚠️ 内存占用过高，建议关闭一些程序～");

            return new List<ContentItem> { new ContentItem { Type = "text", Text = sb.ToString().Trim() } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"系统监控失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteDailyQuoteAsync()
    {
        try
        {
            var quotes = new[]
            {
                "今天也要元气满满哦！✨",
                "每一个不曾起舞的日子，都是对生命的辜负。",
                "成功的秘诀在于始终如一地坚持目标。",
                "生活不是等待风暴过去，而是学会在雨中翩翩起舞。",
                "相信自己，你比想象中更强大！💪",
                "不要等待机会，而要创造机会。",
                "你的努力，终将成就独一无二的自己。",
                "每天进步一点点，积累起来就是大飞跃！",
                "保持热爱，奔赴山海。",
                "星光不问赶路人，时光不负有心人。",
                "今天的努力是明天最好的铺垫～",
                "无论遇到什么困难，都要微笑面对！😊",
                "坚持到底，胜利就在前方！",
                "做自己的太阳，无需借助谁的光。",
                "每一次挑战都是成长的机会！"
            };

            var rnd = new Random();
            string quote = quotes[rnd.Next(quotes.Length)];

            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"今日鸡汤:\n\n{quote} 喵～" } };
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"获取鸡汤失败: {ex.Message}" } };
        }
    }

    private static async Task<List<ContentItem>> ExecuteToggleTopmostAsync()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "未找到活动窗口 喵～" } };

            bool isTopmost = GetWindowLong(hwnd, GWL_EXSTYLE) != 0 && 
                (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

            if (isTopmost)
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) & ~WS_EX_TOPMOST);
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "已取消窗口置顶 喵～" } };
            }
            else
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOPMOST);
                return new List<ContentItem> { new ContentItem { Type = "text", Text = "已设置窗口置顶 喵～" } };
            }
        }
        catch (Exception ex)
        {
            return new List<ContentItem> { new ContentItem { Type = "text", Text = $"窗口置顶操作失败: {ex.Message}" } };
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    #endregion

    #endregion
}
