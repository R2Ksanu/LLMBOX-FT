using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LlmodelBox.Services
{
    // Runtime theme + accent engine. Themes set neutral SURFACE/TEXT/BORDER brushes;
    // the accent is applied separately on top so it survives theme switches. All brushes
    // are pushed into the app ResourceDictionary as DynamicResource "Brush*" keys.
    public static class ThemeManager
    {
        public const string ThemeDaylight = "Daylight";
        public const string ThemeSlate = "Slate";
        public const string ThemeMidnight = "Midnight";

        public const string DefaultAccent = "Blue";

        // Tracks the last theme applied so ApplyAccent can pick light- vs dark-appropriate
        // glow/muted tints (a dark navy glow behind a light-theme card looks broken).
        private static string _currentTheme = ThemeMidnight;

        // Accent palettes: base / light / glow (dark-theme tint) / muted (dark-theme deep tint)
        //                   / glowLite / mutedLite (light-theme tints, pale).
        private static readonly Dictionary<string, (string Base, string Light, string Glow, string Muted, string GlowLite, string MutedLite)> Accents =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Blue"]   = ("#3b82f6", "#60a5fa", "#172554", "#1e3a8a", "#eff6ff", "#bfdbfe"),
                ["Violet"] = ("#8b5cf6", "#a78bfa", "#2e1065", "#4c1d95", "#f5f3ff", "#ddd6fe"),
                ["Green"]  = ("#22c55e", "#4ade80", "#052e16", "#166534", "#f0fdf4", "#bbf7d0"),
                ["Amber"]  = ("#f59e0b", "#fbbf24", "#451a03", "#92400e", "#fffbeb", "#fde68a"),
                ["Rose"]   = ("#f43f5e", "#fb7185", "#4c0519", "#9f1239", "#fff1f2", "#fecdd3"),
            };

        public static IEnumerable<string> AccentNames => Accents.Keys;

        // Back-compat: older persisted names map onto the new three.
        private static string NormalizeTheme(string? name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "daylight": case "light": return ThemeDaylight;
                case "slate": case "carbon": case "midnight-navy": return ThemeSlate;
                case "midnight": case "oled": case "navy": case "azure": default: return ThemeMidnight;
            }
        }

        public static void ApplyTheme(string? themeName, ResourceDictionary resources, string? accentName = null)
        {
            if (resources == null) return;
            _currentTheme = NormalizeTheme(themeName);
            switch (_currentTheme)
            {
                case ThemeDaylight: ApplyDaylightTheme(resources); break;
                case ThemeSlate: ApplySlateTheme(resources); break;
                default: ApplyMidnightTheme(resources); break;
            }
            ApplyAccent(accentName ?? DefaultAccent, resources);
        }

        public static void ApplyAccent(string? accentName, ResourceDictionary resources)
        {
            if (resources == null) return;
            if (!Accents.TryGetValue((accentName ?? DefaultAccent).Trim(), out var a))
                a = Accents[DefaultAccent];
            bool light = _currentTheme == ThemeDaylight;
            SetBrush(resources, "BrushAccent", a.Base);
            SetBrush(resources, "BrushAccentLight", a.Light);
            SetBrush(resources, "BrushAccentGlow", light ? a.GlowLite : a.Glow);
            SetBrush(resources, "BrushAccentMuted", light ? a.MutedLite : a.Muted);
            SetBrush(resources, "BrushAccentText", "#ffffff");
        }

        private static void SetBrush(ResourceDictionary dict, string key, string hexColor)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hexColor);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                dict[key] = brush;
            }
            catch { }
        }
        // PLACEHOLDER_THEMES

        // Deep near-black, the OLED-style default. Geist neutrals.
        private static void ApplyMidnightTheme(ResourceDictionary dict)
        {
            SetBrush(dict, "BrushWindowBg", "#000000");
            SetBrush(dict, "BrushTitleBarBg", "#050506");
            SetBrush(dict, "BrushCardBg", "#0a0a0c");
            SetBrush(dict, "BrushCardInnerBg", "#101013");
            SetBrush(dict, "BrushInputBg", "#0a0a0c");

            SetBrush(dict, "BrushBorder", "#1f1f24");
            SetBrush(dict, "BrushBorderSubtle", "#161619");
            SetBrush(dict, "BrushBorderHover", "#33333b");

            SetBrush(dict, "BrushTextPrimary", "#fafafa");
            SetBrush(dict, "BrushTextSecondary", "#a1a1aa");
            SetBrush(dict, "BrushTextMuted", "#71717a");
            SetBrush(dict, "BrushTextDim", "#52525b");

            SetBrush(dict, "BrushSuccess", "#22c55e");
            SetBrush(dict, "BrushSuccessGlow", "#052e16");
            SetBrush(dict, "BrushDanger", "#f43f5e");
            SetBrush(dict, "BrushDangerGlow", "#4c0519");
            SetBrush(dict, "BrushWarning", "#f59e0b");
            SetBrush(dict, "BrushWarningGlow", "#451a03");

            // Translucent glassmorphism layer tokens (Midnight Obsidian)
            SetBrush(dict, "BrushGlassSurface", "#CC0b0e15");
            SetBrush(dict, "BrushGlassHeader", "#D9101521");
            SetBrush(dict, "BrushGlassCardHover", "#CC171e2e");
            SetBrush(dict, "BrushGlassBorder", "#1Fffffff");
            SetBrush(dict, "BrushGlassBorderHover", "#3D60a5fa");
        }

        // Cool blue-gray dark theme (Vercel/Geist slate ramp).
        private static void ApplySlateTheme(ResourceDictionary dict)
        {
            SetBrush(dict, "BrushWindowBg", "#0b0e14");
            SetBrush(dict, "BrushTitleBarBg", "#0f131b");
            SetBrush(dict, "BrushCardBg", "#111721");
            SetBrush(dict, "BrushCardInnerBg", "#18202e");
            SetBrush(dict, "BrushInputBg", "#0f151f");

            SetBrush(dict, "BrushBorder", "#232c3b");
            SetBrush(dict, "BrushBorderSubtle", "#1a2230");
            SetBrush(dict, "BrushBorderHover", "#38445a");

            SetBrush(dict, "BrushTextPrimary", "#f8fafc");
            SetBrush(dict, "BrushTextSecondary", "#a9b4c4");
            SetBrush(dict, "BrushTextMuted", "#71809a");
            SetBrush(dict, "BrushTextDim", "#4d5a72");

            SetBrush(dict, "BrushSuccess", "#34d399");
            SetBrush(dict, "BrushSuccessGlow", "#064e3b");
            SetBrush(dict, "BrushDanger", "#f87171");
            SetBrush(dict, "BrushDangerGlow", "#450a0a");
            SetBrush(dict, "BrushWarning", "#fbbf24");
            SetBrush(dict, "BrushWarningGlow", "#451a03");

            // Translucent glassmorphism layer tokens (Slate)
            SetBrush(dict, "BrushGlassSurface", "#CC101724");
            SetBrush(dict, "BrushGlassHeader", "#D9151e2e");
            SetBrush(dict, "BrushGlassCardHover", "#CC1c283d");
            SetBrush(dict, "BrushGlassBorder", "#22ffffff");
            SetBrush(dict, "BrushGlassBorderHover", "#4860a5fa");
        }

        // True light theme — near-white surfaces, dark text (Geist light).
        private static void ApplyDaylightTheme(ResourceDictionary dict)
        {
            SetBrush(dict, "BrushWindowBg", "#fafafa");
            SetBrush(dict, "BrushTitleBarBg", "#ffffff");
            SetBrush(dict, "BrushCardBg", "#ffffff");
            SetBrush(dict, "BrushCardInnerBg", "#f4f4f5");
            SetBrush(dict, "BrushInputBg", "#ffffff");

            SetBrush(dict, "BrushBorder", "#e4e4e7");
            SetBrush(dict, "BrushBorderSubtle", "#f0f0f2");
            SetBrush(dict, "BrushBorderHover", "#d4d4d8");

            SetBrush(dict, "BrushTextPrimary", "#18181b");
            SetBrush(dict, "BrushTextSecondary", "#52525b");
            SetBrush(dict, "BrushTextMuted", "#71717a");
            SetBrush(dict, "BrushTextDim", "#a1a1aa");

            SetBrush(dict, "BrushSuccess", "#16a34a");
            SetBrush(dict, "BrushSuccessGlow", "#dcfce7");
            SetBrush(dict, "BrushDanger", "#dc2626");
            SetBrush(dict, "BrushDangerGlow", "#fee2e2");
            SetBrush(dict, "BrushWarning", "#d97706");
            SetBrush(dict, "BrushWarningGlow", "#fef3c7");

            // Translucent glassmorphism layer tokens (Daylight)
            SetBrush(dict, "BrushGlassSurface", "#E8ffffff");
            SetBrush(dict, "BrushGlassHeader", "#F2f8fafc");
            SetBrush(dict, "BrushGlassCardHover", "#F2f1f5f9");
            SetBrush(dict, "BrushGlassBorder", "#1A0f172a");
            SetBrush(dict, "BrushGlassBorderHover", "#403b82f6");
        }
    }
}
