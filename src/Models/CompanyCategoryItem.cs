using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace LlmodelBox.Models
{
    public class CompanyCategoryItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Tagline { get; set; } = string.Empty;
        public string WebUrl { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public string LocalLogoPath { get; set; } = string.Empty;
        public string FallbackIcon { get; set; } = "⚡";
        public bool HasApiKey { get; set; } = false;
        public string MaskedApiKey { get; set; } = string.Empty;
        public string KeyDocUrl { get; set; } = string.Empty;
        public string EnvVarName { get; set; } = string.Empty;
        public List<ModelItem> Models { get; set; } = new();

        // UI & Visibility Binding Helpers (Prevents icon & image double overlay)
        public bool HasLocalLogo => !string.IsNullOrWhiteSpace(LocalLogoPath) && File.Exists(LocalLogoPath);
        public Visibility LogoVisibility => HasLocalLogo ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FallbackIconVisibility => HasLocalLogo ? Visibility.Collapsed : Visibility.Visible;

        public string KeyStatusBadgeText => HasApiKey ? "● API KEY ACTIVE" : "⚠️ API KEY REQUIRED";
        public string KeyStatusBadgeColor => HasApiKey ? "#22c55e" : "#f59e0b";
        public string KeyStatusBadgeBg => HasApiKey ? "#064e3b" : "#451a03";
        public string KeyStatusBorder => HasApiKey ? "#10b981" : "#d97706";
        public string ActionBtnText => HasApiKey ? "🔑 Update Key" : "🔑 Enter API Key";
        public int ModelCount => Models?.Count ?? 0;
    }
}
