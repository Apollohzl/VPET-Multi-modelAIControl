namespace VPet.Plugin.PetChat.Models;

/// <summary>
/// 表示一个可选的大语言模型项,包含用于 API 调用的标识和用于界面展示的名称。
/// </summary>
/// <param name="Model">模型在接口中的唯一标识(如 qwen-safety)。</param>
/// <param name="Name">在界面上显示的人类可读名称(如 Qwen3Guard 8B)。</param>
/// <param name="SupportsImageInput">是否支持图片输入(input_modalities 包含 image)。</param>
/// <param name="SupportsAudioInput">是否支持音频输入(input_modalities 包含 audio)。</param>
/// <param name="SupportsVideoInput">是否支持视频输入(input_modalities 包含 video)。</param>
public record ModelOption(
    string Model,
    string Name,
    bool SupportsImageInput = false,
    bool SupportsAudioInput = false,
    bool SupportsVideoInput = false);
