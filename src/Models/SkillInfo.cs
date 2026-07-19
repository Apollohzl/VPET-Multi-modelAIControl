using System.Collections.Generic;

namespace VPet.Plugin.PetChat.Models;

/// <summary>
/// 工具/技能信息。描述一个可被 AI 调用的工具函数。
/// </summary>
public class SkillInfo
{
    /// <summary>工具函数名,AI 通过此名称调用。</summary>
    public string Name { get; init; } = string.Empty;
    
    /// <summary>工具功能描述,发送给 AI 以便其决定何时调用。</summary>
    public string Description { get; init; } = string.Empty;
    
    /// <summary>工具简述(≤6字符),宠物说话时显示。</summary>
    public string Summary { get; set; } = string.Empty;
    
    /// <summary>是否为内置工具(不可删除)。</summary>
    public bool IsBuiltIn { get; init; }
    
    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>参数列表。</summary>
    public List<ParamInfo> Parameters { get; init; } = new();
}

/// <summary>
/// 工具参数信息。
/// </summary>
public class ParamInfo
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = "string";
    public bool Required { get; init; }
    public string Description { get; init; } = string.Empty;
}