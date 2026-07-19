using System;
using System.Collections.Generic;
using System.IO;

namespace VPet.Plugin.PetChat.Services
{
    public enum FileCategory
    {
        Text,
        Image,
        Audio,
        Video,
        Unsupported
    }

    public static class FileAnalyzer
    {
        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".txt", ".js", ".ts", ".html", ".css", ".json", ".xml", ".csv",
            ".py", ".cs", ".java", ".cpp", ".c", ".h", ".hpp", ".go", ".rs", ".php",
            ".rb", ".sh", ".yml", ".yaml", ".log", ".sql", ".r", ".lua", ".kt",
            ".swift", ".vue", ".tsx", ".jsx", ".ini", ".conf", ".toml", ".md5",
            ".sha1", ".sha256"
        };

        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
        };

        private static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".flac", ".opus", ".pcm16"
        };

        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".webm", ".mov", ".avi", ".mkv"
        };

        public static FileCategory Classify(string filePath)
        {
            string ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? string.Empty;
            if (TextExtensions.Contains(ext)) return FileCategory.Text;
            if (ImageExtensions.Contains(ext)) return FileCategory.Image;
            if (AudioExtensions.Contains(ext)) return FileCategory.Audio;
            if (VideoExtensions.Contains(ext)) return FileCategory.Video;
            return FileCategory.Unsupported;
        }

        public static string? GetAudioFormat(string filePath)
        {
            string ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? string.Empty;
            return ext switch
            {
                ".mp3" => "mp3",
                ".wav" => "wav",
                ".flac" => "flac",
                ".opus" => "opus",
                ".pcm16" => "pcm16",
                _ => null,
            };
        }

        public static string? GetMimeType(string filePath)
        {
            string ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? string.Empty;
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => null,
            };
        }
    }
}
