using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows;
using LlmodelBox.Providers;

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

        private string Haystack() => $"{Id} {Name} {Family} {Description}".ToLowerInvariant();

        private static bool ContainsAny(string text, params string[] terms)
        {
            foreach (var t in terms)
            {
                if (text.Contains(t, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        [JsonIgnore]
        public bool IsCodingModel
        {
            get
            {
                var h = Haystack();
                return ContainsAny(h, "code", "coder", "codestral", "devstral", "starcoder", "programming", "software",
                    "deepseek", "claude", "gpt-4", "gpt-5", "o1", "o3", "o4", "qwen", "llama-3.3", "llama-4");
            }
        }

        [JsonIgnore]
        public bool IsVisionModel
        {
            get
            {
                var h = Haystack();
                if (ContainsAny(h, "vision", "-vl", " vl", "multimodal", "pixtral", "image", "llava")) return true;
                return ModelCapabilityResolver.Resolve(this).Vision;
            }
        }

        [JsonIgnore]
        public bool IsVideoModel
        {
            get
            {
                var h = Haystack();
                return ContainsAny(h, "video", "omni", "gemini-1.5", "gemini-2", "gemini-2.5", "gemini-3", "veo", "sora", "kling");
            }
        }

        [JsonIgnore]
        public bool IsAudioModel
        {
            get
            {
                var h = Haystack();
                if (ContainsAny(h, "audio", "speech", "whisper", "tts", "realtime", "voice")) return true;
                return ModelCapabilityResolver.Resolve(this).Audio;
            }
        }

        [JsonIgnore]
        public bool IsReasoningModel
        {
            get
            {
                var h = Haystack();
                if (ContainsAny(h, "reason", "thinking", "-r1", " r1", "qwq", "o1", "o3", "o4")) return true;
                return ModelCapabilityResolver.Resolve(this).Reasoning;
            }
        }

        [JsonIgnore]
        public string CategoryBadgeText
        {
            get
            {
                var tags = new List<string>(4);
                if (IsCodingModel) tags.Add("CODE");
                if (IsVisionModel) tags.Add("VISION");
                if (IsVideoModel) tags.Add("VIDEO");
                if (IsAudioModel && !IsVideoModel) tags.Add("AUDIO");
                if (IsReasoningModel && tags.Count < 3) tags.Add("REASON");
                if (tags.Count == 0 || (tags.Count == 1 && tags[0] == "CODE")) tags.Add("TEXT");
                return string.Join(" · ", tags);
            }
        }

        [JsonIgnore]
        public string PrimaryCategoryTag
        {
            get
            {
                if (IsVideoModel) return "VIDEO";
                if (IsVisionModel) return "VISION";
                if (IsCodingModel) return "CODE";
                if (IsReasoningModel) return "REASON";
                if (IsAudioModel) return "AUDIO";
                return "TEXT";
            }
        }

        public bool MatchesCapabilityFilter(string? filter)
        {
            if (string.IsNullOrWhiteSpace(filter) || string.Equals(filter, "All", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(filter, "Free", StringComparison.OrdinalIgnoreCase)) return IsFree;
            if (string.Equals(filter, "Paid", StringComparison.OrdinalIgnoreCase)) return !IsFree;
            if (string.Equals(filter, "Coding", StringComparison.OrdinalIgnoreCase) || string.Equals(filter, "Code", StringComparison.OrdinalIgnoreCase)) return IsCodingModel;
            if (string.Equals(filter, "Vision", StringComparison.OrdinalIgnoreCase)) return IsVisionModel;
            if (string.Equals(filter, "Video", StringComparison.OrdinalIgnoreCase)) return IsVideoModel;
            if (string.Equals(filter, "Audio", StringComparison.OrdinalIgnoreCase)) return IsAudioModel;
            if (string.Equals(filter, "Reasoning", StringComparison.OrdinalIgnoreCase)) return IsReasoningModel;
            if (string.Equals(filter, "Text", StringComparison.OrdinalIgnoreCase)) return true;
            return true;
        }
    }
}
