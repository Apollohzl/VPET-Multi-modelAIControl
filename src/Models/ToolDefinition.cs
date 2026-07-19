using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPet.Plugin.PetChat.Models;

/// <summary>
/// 自定义 AI 工具的完整定义,包含工具结构(用于 API 请求)和 C# 脚本代码(用于执行)。
/// </summary>
public class ToolDefinition
{
    /// <summary>工具函数名,AI 通过此名称调用。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>工具功能描述,发送给 AI 以便其决定何时调用。</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>简述(≤6字符,宠物说话时显示)。</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>参数列表。</summary>
    [JsonPropertyName("parameters")]
    public List<ToolParameter> Parameters { get; set; } = new();

    /// <summary>C# 脚本代码,用于工具执行。</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// JSON 序列化选项(读写共享)。
    /// </summary>
    private static JsonSerializerOptions JsonOpts { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>
    /// 生成符合 OpenAI 兼容 API 规范的 tool 结构 JSON 字符串。
    /// 若参数的 DefaultValue 为空,则不包含 default 字段。
    /// </summary>
    public string ToApiToolJson()
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var p in Parameters)
        {
            var prop = new Dictionary<string, object>
            {
                ["type"] = p.Type,
                ["description"] = p.Description,
            };
            if (!string.IsNullOrEmpty(p.DefaultValue))
            {
                prop["default"] = p.DefaultValue;
            }
            properties[p.Name] = prop;

            if (p.Required)
            {
                required.Add(p.Name);
            }
        }

        var tool = new
        {
            type = "function",
            function = new
            {
                name = Name,
                description = Description,
                parameters = new
                {
                    type = "object",
                    properties = properties,
                    required = required,
                },
            },
        };

        return JsonSerializer.Serialize(tool, JsonOpts);
    }

    /// <summary>
    /// 将工具定义序列化为 .moxi 文件格式的 JSON 字符串
    /// (包含 name/description/summary/parameters/code)。
    /// </summary>
    public string ToJsonString()
    {
        return JsonSerializer.Serialize(this, JsonOpts);
    }

    /// <summary>
    /// 从 JSON 反序列化为 ToolDefinition 对象。
    /// </summary>
    public static ToolDefinition FromJsonString(string json)
    {
        return JsonSerializer.Deserialize<ToolDefinition>(json, JsonOpts)
               ?? new ToolDefinition();
    }

    /// <summary>
    /// C# 脚本模板代码字符串。
    /// </summary>
    public static string CodeTemplate =>
        @"// ====== 工具执行代码编写说明 ======
// 函数签名：public async Task<string> Execute(Dictionary<string, object> args)
// - args[""参数名""] 获取 AI 传入的参数值
// - 返回 string 类型结果给 AI
//
// 可用 API：
// - System.IO：文件读写操作
// - System.Diagnostics.Process：启动进程/打开网页
// - System.Net.Http.HttpClient：网络请求
// - System.Text.Json：JSON 处理
// - System.Text.RegularExpressions：正则匹配
//
// 示例：打开网页
// public async Task<string> Execute(Dictionary<string, object> args)
// {
//     var url = args[""url""]?.ToString();
//     System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
//     {
//         FileName = url,
//         UseShellExecute = true
//     });
//     return $""已打开网页: {url}"";
// }
// ================================

public async Task<string> Execute(Dictionary<string, object> args)
{
    // 在此编写工具执行代码

    return ""执行完成"";
}";
}

/// <summary>
/// 工具参数定义,实现 INotifyPropertyChanged 以支持 WPF 数据绑定。
/// </summary>
public class ToolParameter : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _type = "string";
    private bool _required;
    private string _defaultValue = string.Empty;
    private string _description = string.Empty;

    /// <summary>参数名。</summary>
    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>参数类型(string/number/boolean/integer)。</summary>
    [JsonPropertyName("type")]
    public string Type
    {
        get => _type;
        set { _type = value; OnPropertyChanged(); }
    }

    /// <summary>是否必填。</summary>
    [JsonPropertyName("required")]
    public bool Required
    {
        get => _required;
        set { _required = value; OnPropertyChanged(); }
    }

    /// <summary>默认值。</summary>
    [JsonPropertyName("defaultValue")]
    public string DefaultValue
    {
        get => _defaultValue;
        set { _defaultValue = value; OnPropertyChanged(); }
    }

    /// <summary>参数描述。</summary>
    [JsonPropertyName("description")]
    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
