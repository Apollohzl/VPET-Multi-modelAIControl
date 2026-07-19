using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 简单的文件日志组件,将信息写入到 mod 根目录下的 logs 文件夹。
/// </summary>
public static class Logger
{
    // 默认日志目录：从程序集位置派生 (plugin/ 目录的上一级即为 mod 根目录)
    private static readonly string DefaultLogDir = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory(),
        "..",
        "logs");

    private static string _logDir = DefaultLogDir;
    private static readonly object _lock = new();

    /// <summary>
    /// 记录一条 INFO 级别日志。
    /// </summary>
    public static void Info(string message)
    {
        WriteLine("info", message, null);
    }

    /// <summary>
    /// 记录一条 ERROR 级别日志,可选地附加异常堆栈。
    /// </summary>
    public static void Error(string message, Exception? ex = null)
    {
        WriteLine("errors", message, ex);
    }

    /// <summary>
    /// 获取或设置日志文件夹的完整路径。设置后会自动创建对应目录。
    /// 空值或空白表示使用默认目录。
    /// </summary>
    public static string LogDirectory
    {
        get => _logDir;
        set
        {
            lock (_lock)
            {
                string dir = string.IsNullOrWhiteSpace(value) ? DefaultLogDir : value.Trim();
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch
                {
                    dir = DefaultLogDir;
                }
                _logDir = dir;
            }
        }
    }

    private static void WriteLine(string kind, string message, Exception? ex)
    {
        try
        {
            lock (_lock)
            {
                if (!Directory.Exists(_logDir)) Directory.CreateDirectory(_logDir);
                string file = Path.Combine(_logDir, $"{kind}_{DateTime.Now:yyyyMMdd}.log");
                var line = new StringBuilder();
                line.Append($"[{DateTime.Now:HH:mm:ss}] [{kind.ToUpperInvariant()}] {message}");
                if (ex != null)
                {
                    line.AppendLine();
                    line.Append(ex.ToString());
                }
                line.AppendLine();
                File.AppendAllText(file, line.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 日志本身失败时静默,避免引发新的异常
        }
    }

    /// <summary>
    /// 获取今日日志文件的完整路径（info 文件）。
    /// </summary>
    public static string GetTodayLogFilePath()
    {
        return Path.Combine(_logDir, $"info_{DateTime.Now:yyyyMMdd}.log");
    }

    /// <summary>
    /// 获取今日错误日志文件的完整路径。
    /// </summary>
    public static string GetTodayErrorLogFilePath()
    {
        return Path.Combine(_logDir, $"errors_{DateTime.Now:yyyyMMdd}.log");
    }

    /// <summary>
    /// 记录工具调用开始。
    /// </summary>
    public static void ToolCall(string toolName, string arguments)
    {
        Info($"[ToolCall] 工具调用: {toolName}, 参数: {arguments}");
    }

    /// <summary>
    /// 记录工具调用结果。
    /// </summary>
    public static void ToolResult(string toolName, string resultType, string content)
    {
        // 内容过长则截断
        if (content.Length > 2000) content = content.Substring(0, 2000) + "...(已截断)";
        Info($"[ToolCall] 工具 {toolName} 结果类型: {resultType}, 内容: {content}");
    }

    /// <summary>
    /// 记录 AI 携带工具结果继续请求。
    /// </summary>
    public static void ToolAiContinue(int historyMessageCount)
    {
        Info($"[ToolCall] AI继续请求, 历史消息数: {historyMessageCount}");
    }

    /// <summary>
    /// 记录工具执行失败。
    /// </summary>
    public static void ToolError(string toolName, string error)
    {
        Error($"[ToolCall] 工具 {toolName} 执行失败: {error}");
    }
}
