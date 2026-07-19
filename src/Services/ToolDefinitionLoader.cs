using System;
using System.IO;
using System.Text;
using VPet.Plugin.PetChat.Models;

namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 负责 .moxi 文件（基于 ToolDefinition 模型）的读写加载服务。
/// </summary>
public static class ToolDefinitionLoader
{
    /// <summary>
    /// 从 .moxi 文件加载工具定义。
    /// 文件以 UTF-8 编码读取，使用 ToolDefinition.FromJsonString 反序列化。
    /// </summary>
    /// <param name="filePath">.moxi 文件完整路径。</param>
    /// <returns>反序列化后的 ToolDefinition 实例；失败时返回 null。</returns>
    public static ToolDefinition? LoadFromMoxi(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Logger.Error($"[ToolDefinitionLoader] 文件不存在: {filePath}");
                return null;
            }

            string json = File.ReadAllText(filePath, Encoding.UTF8);
            var def = ToolDefinition.FromJsonString(json);

            Logger.Info($"[ToolDefinitionLoader] 加载成功: {filePath}, 工具名: {def.Name}");
            return def;
        }
        catch (Exception ex)
        {
            Logger.Error($"[ToolDefinitionLoader] 加载失败: {filePath}", ex);
            return null;
        }
    }

    /// <summary>
    /// 将工具定义保存到 .moxi 文件。
    /// 使用 ToolDefinition.ToJsonString 序列化，以 UTF-8（带 BOM）编码写入。
    /// 若目标目录不存在则自动创建。
    /// </summary>
    /// <param name="def">要保存的工具定义。</param>
    /// <param name="filePath">.moxi 文件完整路径。</param>
    /// <returns>保存成功返回 true；失败返回 false。</returns>
    public static bool SaveToMoxi(ToolDefinition def, string filePath)
    {
        try
        {
            if (def == null)
            {
                Logger.Error("[ToolDefinitionLoader] 保存失败: ToolDefinition 为 null");
                return false;
            }

            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = def.ToJsonString();
            // 使用 UTF-8 编码并写入 BOM
            File.WriteAllText(filePath, json, new UTF8Encoding(true));

            Logger.Info($"[ToolDefinitionLoader] 保存成功: {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[ToolDefinitionLoader] 保存失败: {filePath}", ex);
            return false;
        }
    }

    /// <summary>
    /// 获取默认的 C# 工具执行代码模板（用户编写代码时使用的模板）。
    /// </summary>
    /// <returns>代码模板字符串。</returns>
    public static string GetDefaultCodeTemplate()
    {
        return ToolDefinition.CodeTemplate;
    }

    /// <summary>
    /// 创建一个新的 ToolDefinition 实例，并将 Code 字段预填为默认代码模板。
    /// </summary>
    /// <returns>预填模板代码的新 ToolDefinition 实例。</returns>
    public static ToolDefinition CreateNew()
    {
        return new ToolDefinition
        {
            Code = ToolDefinition.CodeTemplate
        };
    }
}
