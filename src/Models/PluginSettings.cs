using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPet.Plugin.PetChat.Models;

/// <summary>
/// 插件配置(JSON 格式)。所有字段使用 snake_case 命名。
/// </summary>
public class PluginSettings
{
    /// <summary>当前供应商: pollenations / deepseek</summary>
    public string provider { get; set; } = "pollenations";

    /// <summary>pollenations 的 API Key</summary>
    public string api_key { get; set; } = string.Empty;

    /// <summary>pollenations 选择的模型 ID</summary>
    public string model { get; set; } = string.Empty;

    /// <summary>Deepseek 官方的 API Key</summary>
    public string deepseek_api_key { get; set; } = string.Empty;

    /// <summary>Deepseek 选择的模型 ID</summary>
    public string deepseek_model { get; set; } = string.Empty;

    /// <summary>当前 pollenations 模型是否支持图片输入</summary>
    public bool model_supports_image { get; set; } = true;

    /// <summary>当前 pollenations 模型是否支持音频输入</summary>
    public bool model_supports_audio { get; set; } = false;

    /// <summary>当前 pollenations 模型是否支持视频输入</summary>
    public bool model_supports_video { get; set; } = false;

    /// <summary>角色设定(系统提示词)</summary>
    public string system_prompt { get; set; } = string.Empty;

    /// <summary>日志目录(留空使用默认)</summary>
    public string log_dir { get; set; } = string.Empty;

    /// <summary>已启用的自定义工具名称列表</summary>
    public List<string> enabled_skills { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>从 JSON 文件加载配置,文件不存在时返回默认实例。</summary>
    public static PluginSettings Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return new PluginSettings();
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<PluginSettings>(json, JsonOpts);
            return settings ?? new PluginSettings();
        }
        catch (Exception)
        {
            return new PluginSettings();
        }
    }

    /// <summary>保存配置到 JSON 文件。</summary>
    public void Save(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }
        catch
        {
        }
    }

    /// <summary>获取配置文件路径(plugin 目录下 plugin_settings.json)。</summary>
    public static string GetDefaultPath(string pluginDir)
    {
        return Path.Combine(pluginDir, "plugin_settings.json");
    }

    /// <summary>
    /// 检测旧版 plugin_settings.lps 并迁移到 JSON。
    /// 迁移成功后删除旧文件。无旧文件或迁移失败时返回 null。
    /// </summary>
    public static PluginSettings? MigrateFromLps(string pluginDir)
    {
        string lpsPath = Path.Combine(pluginDir, "plugin_settings.lps");
        if (!File.Exists(lpsPath)) return null;

        try
        {
            string content = File.ReadAllText(lpsPath, Encoding.UTF8);
            var settings = new PluginSettings();

            // 旧 LPS 格式: key1#val1:|key2#val2:| ...
            string[] parts = content.Split(new[] { ":|" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                int idx = part.IndexOf('#');
                if (idx <= 0) continue;
                string key = part.Substring(0, idx).Trim();
                string val = part.Substring(idx + 1).Trim();
                if (string.IsNullOrEmpty(val)) continue;

                switch (key)
                {
                    case "api_key": settings.api_key = val; break;
                    case "model": settings.model = val; break;
                    case "system_prompt": settings.system_prompt = val; break;
                }
            }

            // 写入新 JSON
            string jsonPath = GetDefaultPath(pluginDir);
            settings.Save(jsonPath);

            // 删除旧文件
            File.Delete(lpsPath);

            return settings;
        }
        catch
        {
            return null; // 迁移失败不阻塞加载
        }
    }
}

/// <summary>供应商常量。</summary>
public static class ProviderNames
{
    public const string Pollenations = "pollenations";
    public const string Deepseek = "deepseek";
}

/// <summary>Deepseek 官方相关常量。</summary>
public static class DeepseekEndpoints
{
    public const string ChatUrl = "https://api.deepseek.com/chat/completions";
    /// <summary>Deepseek 固定模型列表（无需 API 请求）</summary>
    public static readonly List<ModelOption> Models = new()
    {
        new ModelOption("deepseek-v4-flash", "deepseek-v4-flash", false, false, false),
        new ModelOption("deepseek-v4-pro", "deepseek-v4-pro", false, false, false),
    };
}
