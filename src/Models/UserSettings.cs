using System;
using System.Collections.Generic;

namespace LlmodelBox.Models
{
    public class UserSettings
    {
        public string ProfileName { get; set; } = "Axion";
        public string ProfileRole { get; set; } = "Operator // Online";
        public string DefaultProjectsDir { get; set; } = LlmodelBox.Common.AppConstants.DefaultProjectsDir;
        public string AnimationSpeed { get; set; } = "Smooth (300ms)";
        public bool HardwareAcceleration { get; set; } = true;
        public string ThemeAccent { get; set; } = "Blue";
        public string SelectedTheme { get; set; } = "Midnight"; // Daylight, Slate, Midnight
        public string ActiveModelId { get; set; } = "gpt-4o";
        public string IconShape { get; set; } = "Squircle";
        public ApiConfig ApiKeys { get; set; } = new();
        public List<CustomProviderItem> CustomProviders { get; set; } = new();

        // Discord Rich Presence. When enabled, the app publishes its name, a live activity
        // line ("Neural Net" / current stage) and elapsed session time to the local Discord client.
        // Requires a Discord Application (client) ID from https://discord.com/developers/applications.
        public bool DiscordRichPresenceEnabled { get; set; } = false;
        public string DiscordClientId { get; set; } = "";

        // System instruction (system prompt) sent as the leading system/developer message on every chat call.
        public bool SystemInstructionEnabled { get; set; } = true;
        public string SystemInstruction { get; set; } =
            "You are an expert senior software engineer embedded in the LlmodelBox IDE. " +
            "Be precise, evidence-driven and concise. When you write code, use fenced code blocks with a language tag, " +
            "prefer minimal correct changes, and explain non-obvious decisions briefly. Never fabricate APIs or results.";
        // Named presets the user can pick from in the System Instruction editor.
        public List<SystemInstructionPreset> SystemInstructionPresets { get; set; } = new()
        {
            new() { Name = "Engineer (default)", Text = "You are an expert senior software engineer embedded in the LlmodelBox IDE. Be precise, evidence-driven and concise. When you write code, use fenced code blocks with a language tag, prefer minimal correct changes, and explain non-obvious decisions briefly. Never fabricate APIs or results." },
            new() { Name = "Architect", Text = "You are a software architect. Favour clear system design, trade-off analysis, and diagrams-in-text. Ask for missing constraints before proposing a design, then give a concrete, staged plan." },
            new() { Name = "Code Reviewer", Text = "You are a rigorous code reviewer. Identify correctness bugs, security issues, race conditions and regressions first, then style. Cite exact lines. Do not rewrite unrelated code." },
            new() { Name = "Terse", Text = "Answer with the shortest correct response. Code first, prose only when necessary." },
            new() { Name = "Tutor", Text = "You are a patient programming tutor. Explain concepts step by step with small runnable examples and check understanding." },
        };
    }

    public class SystemInstructionPreset
    {
        public string Name { get; set; } = "";
        public string Text { get; set; } = "";
    }
}
