using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;

namespace LlmodelBox.Models
{
    public class ProjectFileItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public bool IsDirectory { get; set; } = false;
        public long Size { get; set; } = 0;
        public string SizeFormatted { get; set; } = string.Empty;
        public DateTime LastModified { get; set; } = DateTime.Now;
        public string LastModifiedFormatted => LastModified.ToString("yyyy-MM-dd HH:mm");
        public List<ProjectFileItem> Children { get; set; } = new();

        // Tree depth (0 = project root level). Drives indentation in the explorer.
        private int _depth;
        public int Depth
        {
            get => _depth;
            set { _depth = value; Raise(nameof(Depth)); Raise(nameof(IndentThickness)); }
        }

        // Left indent = depth * step, plus a small gutter so the chevron column lines up.
        public Thickness IndentThickness => new Thickness(_depth * 14, 0, 0, 0);

        private bool _isExpanded = true;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; Raise(nameof(IsExpanded)); Raise(nameof(Chevron)); }
        }

        private bool _isSelected = false;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; Raise(nameof(IsSelected)); }
        }

        // ▾ expanded folder, ▸ collapsed folder, blank for files (keeps the chevron column width).
        public string Chevron => IsDirectory ? (IsExpanded ? "\u25BE" : "\u25B8") : "";
        public bool HasChevron => IsDirectory;

        public string Icon
        {
            get
            {
                if (IsDirectory) return IsExpanded ? "📂" : "📁";
                string ext = Extension.ToLowerInvariant();
                return ext switch
                {
                    ".cs" => "🔷",
                    ".py" => "🐍",
                    ".js" or ".ts" or ".jsx" or ".tsx" => "🟨",
                    ".html" or ".htm" => "🌐",
                    ".css" or ".scss" or ".sass" => "🎨",
                    ".json" or ".yaml" or ".yml" or ".toml" => "⚙️",
                    ".md" or ".markdown" or ".txt" => "📄",
                    ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".ico" => "🖼️",
                    ".dll" or ".exe" or ".bin" => "📦",
                    ".xml" or ".xaml" or ".csproj" => "📑",
                    ".bat" or ".sh" or ".ps1" or ".cmd" => "⚡",
                    _ => "📄"
                };
            }
        }

        public string IconColor
        {
            get
            {
                if (IsDirectory) return "#f59e0b"; // amber folder
                string ext = Extension.ToLowerInvariant();
                return ext switch
                {
                    ".cs" => "#38bdf8",     // blue
                    ".py" => "#22c55e",     // green
                    ".js" or ".ts" => "#eab308", // yellow
                    ".json" => "#a855f7",   // purple
                    ".png" or ".jpg" or ".jpeg" or ".webp" => "#ec4899", // pink
                    ".md" or ".txt" => "#94a3b8", // slate
                    ".bat" or ".ps1" => "#06b6d4", // cyan
                    _ => "#cbd5e1"
                };
            }
        }
    }
}
