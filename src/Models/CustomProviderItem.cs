using System;
using System.IO;
using System.Windows;

namespace LlmodelBox.Models
{
    public class CustomProviderItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string WebUrl { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string LocalLogoPath { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;

        // UI & Visibility Binding Helpers (Prevents icon & image double overlay)
        public bool HasLocalLogo => !string.IsNullOrWhiteSpace(LocalLogoPath) && File.Exists(LocalLogoPath);
        public Visibility LogoVisibility => HasLocalLogo ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FallbackIconVisibility => HasLocalLogo ? Visibility.Collapsed : Visibility.Visible;
    }
}
