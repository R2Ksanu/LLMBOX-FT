using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using LlmodelBox.Infrastructure.Editor;

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

        private int _depth;
        public int Depth
        {
            get => _depth;
            set { _depth = value; Raise(nameof(Depth)); Raise(nameof(IndentThickness)); }
        }

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

        public string Chevron => IsDirectory ? (IsExpanded ? "\u25BE" : "\u25B8") : "";
        public bool HasChevron => IsDirectory;
        public Visibility FolderVisibility => IsDirectory ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FileBadgeVisibility => IsDirectory ? Visibility.Collapsed : Visibility.Visible;

        private EditorLanguageInfo DetectedLanguage => EditorLanguageService.DetectLanguage(string.IsNullOrEmpty(Name) ? Extension : Name);

        public string LanguageBadge => IsDirectory ? string.Empty : DetectedLanguage.Badge;
        public string LanguageDisplayName => IsDirectory ? "Folder" : DetectedLanguage.DisplayName;

        public string Icon => LanguageBadge;

        public string IconColor
        {
            get
            {
                if (IsDirectory) return "#f59e0b";
                return EditorLanguageService.GetBadgePalette(DetectedLanguage.Id).FgHex;
            }
        }

        public string BadgeBgHex => IsDirectory ? "#1E78350f" : EditorLanguageService.GetBadgePalette(DetectedLanguage.Id).BgHex;
        public string BadgeBorderHex => IsDirectory ? "#4Df59e0b" : EditorLanguageService.GetBadgePalette(DetectedLanguage.Id).BorderHex;
    }
}
