using System;
using System.Text.Json.Serialization;

namespace LlmodelBox.Models
{
    public class ProjectItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string LastOpened { get; set; } = string.Empty;

        [JsonIgnore]
        public int ConversationCount { get; set; }

        [JsonIgnore]
        public string ConversationSummary => ConversationCount == 0
            ? "No chats yet"
            : (ConversationCount == 1 ? "1 conversation" : $"{ConversationCount} conversations");
    }
}
