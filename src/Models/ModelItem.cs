using System;
using System.IO;
using System.Windows;

namespace LlmodelBox.Models
{
    public class ModelItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string Family { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;
        public string Parameters { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
        public string Pricing { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Icon { get; set; } = "🤖";
        public string WebUrl { get; set; } = string.Empty;
        public string LocalLogoPath { get; set; } = string.Empty;
        public bool IsCustom { get; set; } = false;
        public string CustomApiKey { get; set; } = string.Empty;

        // UI & Visibility Binding Helpers (Prevents icon & image double overlay)
        public bool HasLocalLogo => !string.IsNullOrWhiteSpace(LocalLogoPath) && File.Exists(LocalLogoPath);
        public Visibility LogoVisibility => HasLocalLogo ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FallbackIconVisibility => HasLocalLogo ? Visibility.Collapsed : Visibility.Visible;

        // Free vs paid classification, derived from the pricing string. Aggregator catalogs report
        // "Free" explicitly; free-tier providers use "Free Tier"; unpriced entries fall back to free.
        public bool IsFree =>
            string.IsNullOrWhiteSpace(Pricing) ||
            Pricing.Trim() == "—" ||
            Pricing.Contains("Free", StringComparison.OrdinalIgnoreCase);
        public string TierBadgeText => IsFree ? "FREE" : "PAID";
        public string TierBadgeColor => IsFree ? "#22c55e" : "#f59e0b";
        public string TierBadgeBg => IsFree ? "#0f2417" : "#2a1c07";

        public bool HasApiKey { get; set; } = false;
        public string KeyBadgeText => HasApiKey ? "● Key Configured" : "⚠️ Key Required";
        public string KeyBadgeColor => HasApiKey ? "#22c55e" : "#f59e0b";
        public string KeyBadgeBg => HasApiKey ? "#142618" : "#2e1808";
        public string ActionBtnText => HasApiKey ? "⚡ Load & Chat (API)" : "🔑 Enter Key to Load";
    }
}
