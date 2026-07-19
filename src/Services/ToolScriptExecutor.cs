using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 脚本全局对象，提供给用户脚本访问的参数。
/// </summary>
public class ScriptGlobals
{
    /// <summary>
    /// 工具调用参数字典，用户脚本通过 args["参数名"] 访问。
    /// </summary>
    public Dictionary<string, object> args = new();
}

/// <summary>
/// C# 脚本编译结果。
/// </summary>
public record CompileResult
{
    /// <summary>编译是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>错误信息（失败时）。</summary>
    public string ErrorMessage { get; init; } = string.Empty;

    /// <summary>错误详情列表，含行号。</summary>
    public List<string> Errors { get; init; } = new();
}

/// <summary>
/// C# 脚本执行引擎。负责编译和执行用户编写的 C# 脚本代码，
/// 用于自定义 AI 工具的执行。
/// </summary>
public class ToolScriptExecutor
{
    /// <summary>
    /// 脚本执行超时时间（毫秒）。
    /// </summary>
    private const int ExecuteTimeoutMs = 30_000;

    /// <summary>
    /// 构建脚本编译选项，包含 using 引用与程序集引用。
    /// </summary>
    private static ScriptOptions BuildScriptOptions()
    {
        return ScriptOptions.Default
            .WithImports(
                "System",
                "System.IO",
                "System.Collections.Generic",
                "System.Linq",
                "System.Text",
                "System.Text.Json",
                "System.Threading.Tasks",
                "System.Diagnostics",
                "System.Net.Http",
                "System.Text.RegularExpressions")
            .WithReferences(
                typeof(object).Assembly,
                typeof(Console).Assembly,
                typeof(File).Assembly,
                typeof(HttpClient).Assembly,
                typeof(JsonDocument).Assembly,
                typeof(Enumerable).Assembly,
                typeof(Regex).Assembly);
    }

    /// <summary>
    /// 编译用户编写的 C# 脚本代码，返回编译结果。
    /// 脚本的入口为 Execute 方法，签名为
    /// <c>public async Task&lt;string&gt; Execute(Dictionary&lt;string, object&gt; args)</c>。
    /// </summary>
    /// <param name="code">用户编写的脚本代码，需包含上述 Execute 方法。</param>
    /// <returns>编译结果，包含成功/失败和错误信息。</returns>
    public CompileResult Compile(string code)
    {
        try
        {
            var options = BuildScriptOptions();
            var script = CSharpScript.Create(code, options, typeof(ScriptGlobals));
            var compilation = script.GetCompilation();
            var diagnostics = compilation.GetDiagnostics();

            var errors = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"行 {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")
                .ToList();

            if (errors.Count > 0)
            {
                return new CompileResult
                {
                    Success = false,
                    ErrorMessage = string.Join("\n", errors),
                    Errors = errors
                };
            }

            return new CompileResult
            {
                Success = true,
                ErrorMessage = string.Empty,
                Errors = new List<string>()
            };
        }
        catch (Exception ex)
        {
            Logger.Error("[ToolScriptExecutor] 编译异常", ex);
            return new CompileResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// 编译并执行用户编写的 C# 脚本代码。
    /// 将 args 作为全局变量传入脚本，脚本中可通过 args["参数名"] 访问参数。
    /// 执行超时时间为 30 秒。
    /// </summary>
    /// <param name="code">用户编写的脚本代码，需包含 public async Task&lt;string&gt; Execute(Dictionary&lt;string,object&gt; args) 方法。</param>
    /// <param name="args">传入脚本的参数字典，脚本中通过 args["参数名"] 访问。</param>
    /// <returns>脚本执行返回的字符串结果。</returns>
    public async Task<string> Execute(string code, Dictionary<string, object> args)
    {
        using var cts = new CancellationTokenSource(ExecuteTimeoutMs);
        try
        {
            var options = BuildScriptOptions();
            // 在用户代码末尾追加调用 Execute 方法的入口代码
            var fullCode = code + "\nreturn await Execute(args);\n";
            var globals = new ScriptGlobals { args = args };

            var result = await CSharpScript.EvaluateAsync<string>(
                fullCode, options, globals, typeof(ScriptGlobals), cts.Token);

            return result ?? string.Empty;
        }
        catch (CompilationErrorException ex)
        {
            Logger.Error("[ToolScriptExecutor] 编译错误", ex);
            var messages = ex.Diagnostics.Select(d => d.ToString());
            throw new Exception($"编译失败: {string.Join("\n", messages)}");
        }
        catch (OperationCanceledException ex)
        {
            Logger.Error("[ToolScriptExecutor] 执行超时（30秒）", ex);
            throw new Exception("脚本执行超时（30秒）");
        }
        catch (Exception ex)
        {
            Logger.Error("[ToolScriptExecutor] 执行异常", ex);
            throw;
        }
    }
}
