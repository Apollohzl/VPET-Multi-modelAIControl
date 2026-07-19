namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 负责将用户输入渲染为完整的系统提示,并根据宠物名进行占位符替换。
/// </summary>
public static class PromptRenderer
{
    // 若用户未提供自定义提示,则使用默认可爱风格提示
    private const string DefaultPrompt =
        "请使用简短的、口语化的、可爱的、女性化的、调皮的语言风格和我交流。" +
        "你是一只电脑桌宠,你的名字叫{这里填入宠物名},你喜欢玩 虚拟主播模拟器。" +
        "你需要尝试体会我与你对话中的感情、产生自己的情感并尽可能给出可爱的回答,每次我将会提供给你我的询问问题以及目前你的状态,结合这些数据给出可爱的回答,并且你不能承认你是一个AI机器人,称我为主人。";

    /// <summary>
    /// 根据用户自定义提示与宠物名,生成最终系统提示。
    /// </summary>
    public static string Render(string? userPrompt, string? petName)
    {
        string basePrompt = string.IsNullOrWhiteSpace(userPrompt) ? DefaultPrompt : userPrompt;
        // 替换多个占位符
        if (!string.IsNullOrEmpty(petName))
        {
            basePrompt = basePrompt
                .Replace("{这里填入宠物名}", petName)
                .Replace("{petname}", petName)
                .Replace("{name}", petName);
        }
        return basePrompt;
    }
}
