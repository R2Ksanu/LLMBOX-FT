using System;
using System.IO;

namespace LlmodelBox.Models
{
    public class ArtifactItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string FileType { get; set; } = "Document";
        public long Size { get; set; } = 0;
        public string SizeFormatted { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        public bool IsImage { get; set; } = false;
        public string ThumbnailUri => IsImage && File.Exists(FilePath) ? FilePath : string.Empty;

        public string Icon => FileType switch
        {
            "Image" => "🖼️",
            "Code" => "💻",
            "Data" or "JSON" => "⚙️",
            "Log" => "📑",
            _ => "📦"
        };
    }
}
