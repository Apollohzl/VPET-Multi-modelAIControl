using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using VPet.Plugin.PetChat.Models;

namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 文件处理器。根据文件类型与当前模型能力,将拖入的文件转换为 API.md 规范的 content 数组元素。
/// </summary>
public static class FileProcessor
{
    /// <summary>
    /// 处理单个文件,生成对应的 ContentItem。返回 null 表示拒绝处理(如模型不支持该类型)。
    /// </summary>
    /// <param name="filePath">文件绝对路径</param>
    /// <param name="supportsImage">当前模型是否支持图片输入</param>
    /// <param name="supportsAudio">当前模型是否支持音频输入</param>
    /// <param name="supportsVideo">当前模型是否支持视频输入</param>
    /// <param name="apiKey">用户 API Key(上传图片时需要)</param>
    public static async Task<ContentItem?> ProcessAsync(
        string filePath,
        bool supportsImage,
        bool supportsAudio,
        bool supportsVideo,
        string apiKey)
    {
        if (!File.Exists(filePath))
        {
            Logger.Info($"[FileProcessor] 文件不存在: {filePath}");
            return null;
        }

        var category = FileAnalyzer.Classify(filePath);
        Logger.Info($"[FileProcessor] 处理文件: {filePath} | 分类: {category}");

        try
        {
            return category switch
            {
                FileCategory.Text => RejectFile(filePath, "文本文件不再支持拖拽上传，仅支持图片/音频/视频"),
                FileCategory.Image => await ProcessImageFile(filePath, supportsImage, apiKey),
                FileCategory.Audio => ProcessAudioFile(filePath, supportsAudio),
                FileCategory.Video => await ProcessVideoFile(filePath, supportsVideo, apiKey),
                _ => RejectFile(filePath, "不支持的文件类型"),
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"[FileProcessor] 处理文件失败: {filePath}", ex);
            return null;
        }
    }

    /// <summary>
    /// 处理图片文件: 检查模型支持,上传到 hzliflow 获取 URL,生成 type=image_url 的 ContentItem。
    /// </summary>
    private static async Task<ContentItem?> ProcessImageFile(string filePath, bool supportsImage, string apiKey)
    {
        if (!supportsImage)
        {
            Logger.Info($"[FileProcessor] 拒绝图片文件: 当前模型不支持图片输入 - {filePath}");
            return null;
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            Logger.Info($"[FileProcessor] 拒绝图片文件: 未配置 API Key - {filePath}");
            return null;
        }

        byte[] imageBytes;
        try
        {
            imageBytes = File.ReadAllBytes(filePath);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FileProcessor] 读取图片文件失败: {filePath}", ex);
            return null;
        }

        string fileName = Path.GetFileName(filePath);
        Logger.Info($"[FileProcessor] 开始上传图片: {fileName}, 大小: {imageBytes.Length} bytes");

        string uploadResult = await ToolExecutor.UploadImageAsync(imageBytes, apiKey!, fileName);

        if (!uploadResult.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Error($"[FileProcessor] 图片上传失败: {fileName} - {uploadResult}");
            return null;
        }

        Logger.Info($"[FileProcessor] 图片上传成功: {fileName}, URL: {uploadResult}");

        return new ContentItem
        {
            Type = "image_url",
            ImageUrl = new ImageUrlObj
            {
                Url = uploadResult,
            }
        };
    }

    /// <summary>
    /// 处理音频文件: 检查模型支持,读取 base64,生成 type=input_audio 的 ContentItem。
    /// </summary>
    private static ContentItem? ProcessAudioFile(string filePath, bool supportsAudio)
    {
        if (!supportsAudio)
        {
            Logger.Info($"[FileProcessor] 拒绝音频文件: 当前模型不支持音频输入 - {filePath}");
            return null;
        }

        byte[] audioBytes;
        try
        {
            audioBytes = File.ReadAllBytes(filePath);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FileProcessor] 读取音频文件失败: {filePath}", ex);
            return null;
        }

        string format = FileAnalyzer.GetAudioFormat(filePath) ?? "mp3";
        string base64Data = Convert.ToBase64String(audioBytes);

        Logger.Info($"[FileProcessor] 音频文件处理成功: {Path.GetFileName(filePath)}, 格式: {format}, base64 长度: {base64Data.Length}");

        return new ContentItem
        {
            Type = "input_audio",
            InputAudio = new InputAudioObj
            {
                Data = base64Data,
                Format = format,
            }
        };
    }

    /// <summary>
    /// 处理视频文件: 检查模型支持,生成 type=video_url 的 ContentItem。
    /// 注: 当前 hzliflow API 仅明确支持图片上传; 视频暂以本地路径作为 URL 占位。
    /// </summary>
    private static async Task<ContentItem?> ProcessVideoFile(string filePath, bool supportsVideo, string apiKey)
    {
        if (!supportsVideo)
        {
            Logger.Info($"[FileProcessor] 拒绝视频文件: 当前模型不支持视频输入 - {filePath}");
            return null;
        }

        string fileName = Path.GetFileName(filePath);
        Logger.Info($"[FileProcessor] 视频文件处理(占位 URL=本地路径): {fileName}");

        await Task.CompletedTask;

        return new ContentItem
        {
            Type = "video_url",
            VideoUrl = new VideoUrlObj
            {
                Url = filePath,
            }
        };
    }

    /// <summary>
    /// 拒绝处理某文件,记录原因并返回 null。
    /// </summary>
    private static ContentItem? RejectFile(string filePath, string reason)
    {
        Logger.Info($"[FileProcessor] 拒绝文件: {filePath} | 原因: {reason}");
        return null;
    }
}
