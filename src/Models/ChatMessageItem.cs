using System;
using System.Collections.Generic;
using System.Windows;

namespace LlmodelBox.Models
{
    public class ChatMessageItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string SenderRole { get; set; } = "User"; // User, Assistant, System
        public string SenderName { get; set; } = "Operator";
        public string Content { get; set; } = string.Empty;
        public string Timestamp { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public string ModelName { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public bool IsError { get; set; } = false;
        public List<string> ImageAttachments { get; set; } = new();

        public bool IsUser => string.Equals(SenderRole, "User", StringComparison.OrdinalIgnoreCase);
        public bool IsAssistant => string.Equals(SenderRole, "Assistant", StringComparison.OrdinalIgnoreCase);
        public bool IsSystem => string.Equals(SenderRole, "System", StringComparison.OrdinalIgnoreCase);

        public bool HasImages => ImageAttachments != null && ImageAttachments.Count > 0;
        public Visibility ImagesVisibility => HasImages ? Visibility.Visible : Visibility.Collapsed;

        public HorizontalAlignment Alignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        // OLED-clean bubbles: quiet surfaces on true black, hairline borders, no neon outlines.
        public string BubbleBackground => IsUser
            ? "#12161f" // soft navy surface
            : (IsError ? "#1f1112" : "#0b0b10"); // near-black assistant / muted red on error

        public string BubbleBorder => IsUser
            ? "#26324a"
            : (IsError ? "#5b2626" : "#1a1a22");

        public string AvatarText => IsUser ? "AX" : (IsSystem ? "SYS" : "AI");
        public string AvatarBackground => IsUser ? "#3f6fd1" : (IsSystem ? "#5f6672" : "#2f6fe6");
        public string RoleColor => IsUser ? "#7fa8f0" : (IsError ? "#f87171" : "#5fd08a");
        public string RoleBadgeText => IsUser ? "OPERATOR" : (!string.IsNullOrWhiteSpace(ModelName) ? ModelName.ToUpperInvariant() : "AI AGENT");
    }
}
