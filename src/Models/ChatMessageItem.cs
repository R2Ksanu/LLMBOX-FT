using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows;

namespace LlmodelBox.Models
{
    public class ChatMessageItem : INotifyPropertyChanged
    {
        private string _content = string.Empty;
        private string _modelName = string.Empty;
        private string _provider = string.Empty;
        private bool _isError = false;
        private MessageActivityState _activityState = MessageActivityState.Completed;
        private string _activityDetail = string.Empty;
        private string _activeFilePath = string.Empty;
        private int _filesModifiedCount = 0;
        private int _addedLinesCount = 0;
        private int _removedLinesCount = 0;
        private double _elapsedSeconds = 0;
        private bool _isStreaming = false;
        private List<ChatContentBlock> _blocks = new();
        private List<string> _toolSteps = new();
        private int _blocksRevision = 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string SenderRole { get; set; } = "User"; // User, Assistant, System
        public string SenderName { get; set; } = "Operator";
        public string Timestamp { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public List<string> ImageAttachments { get; set; } = new();

        public string Content
        {
            get => _content;
            set { if (_content != value) { _content = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public string ModelName
        {
            get => _modelName;
            set
            {
                if (_modelName != value)
                {
                    _modelName = value ?? string.Empty;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RoleBadgeText));
                }
            }
        }

        public string Provider
        {
            get => _provider;
            set { if (_provider != value) { _provider = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public bool IsError
        {
            get => _isError;
            set
            {
                if (_isError != value)
                {
                    _isError = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BubbleBackground));
                    OnPropertyChanged(nameof(BubbleBorder));
                    OnPropertyChanged(nameof(RoleColor));
                }
            }
        }

        public MessageActivityState ActivityState
        {
            get => _activityState;
            set
            {
                if (_activityState != value)
                {
                    _activityState = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsActiveInProgress));
                    OnPropertyChanged(nameof(ActivityBadgeText));
                }
            }
        }

        public string ActivityDetail
        {
            get => _activityDetail;
            set { if (_activityDetail != value) { _activityDetail = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public string ActiveFilePath
        {
            get => _activeFilePath;
            set { if (_activeFilePath != value) { _activeFilePath = value ?? string.Empty; OnPropertyChanged(); } }
        }

        public int FilesModifiedCount
        {
            get => _filesModifiedCount;
            set { if (_filesModifiedCount != value) { _filesModifiedCount = value; OnPropertyChanged(); } }
        }

        public int AddedLinesCount
        {
            get => _addedLinesCount;
            set { if (_addedLinesCount != value) { _addedLinesCount = value; OnPropertyChanged(); } }
        }

        public int RemovedLinesCount
        {
            get => _removedLinesCount;
            set { if (_removedLinesCount != value) { _removedLinesCount = value; OnPropertyChanged(); } }
        }

        public double ElapsedSeconds
        {
            get => _elapsedSeconds;
            set { if (Math.Abs(_elapsedSeconds - value) > 0.01) { _elapsedSeconds = value; OnPropertyChanged(); } }
        }

        [JsonIgnore]
        public bool IsStreaming
        {
            get => _isStreaming;
            set { if (_isStreaming != value) { _isStreaming = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsActiveInProgress)); } }
        }

        public List<ChatContentBlock> Blocks
        {
            get => _blocks;
            set { _blocks = value ?? new(); _blocksRevision++; OnPropertyChanged(); OnPropertyChanged(nameof(BlocksRevision)); }
        }

        public List<string> ToolSteps
        {
            get => _toolSteps;
            set { _toolSteps = value ?? new(); OnPropertyChanged(); }
        }

        [JsonIgnore]
        public int BlocksRevision
        {
            get => _blocksRevision;
            private set { _blocksRevision = value; OnPropertyChanged(); }
        }

        public void NotifyBlocksUpdated()
        {
            _blocksRevision++;
            OnPropertyChanged(nameof(Blocks));
            OnPropertyChanged(nameof(BlocksRevision));
        }

        [JsonIgnore]
        public bool IsUser => string.Equals(SenderRole, "User", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool IsAssistant => string.Equals(SenderRole, "Assistant", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool IsSystem => string.Equals(SenderRole, "System", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool HasImages => ImageAttachments != null && ImageAttachments.Count > 0;

        [JsonIgnore]
        public Visibility ImagesVisibility => HasImages ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public bool IsActiveInProgress =>
            _isStreaming ||
            (_activityState != MessageActivityState.Idle &&
             _activityState != MessageActivityState.Completed &&
             _activityState != MessageActivityState.Cancelled &&
             _activityState != MessageActivityState.Failed);

        [JsonIgnore]
        public string ActivityBadgeText => _activityState switch
        {
            MessageActivityState.Queued => "QUEUED",
            MessageActivityState.Connecting => "CONNECTING",
            MessageActivityState.Thinking => "THINKING",
            MessageActivityState.Generating => "GENERATING",
            MessageActivityState.Streaming => "STREAMING",
            MessageActivityState.ExecutingTool => "RUNNING",
            MessageActivityState.EditingFiles => "EDITING",
            MessageActivityState.Cancelled => "CANCELLED",
            MessageActivityState.Failed => "ERROR",
            _ => string.Empty
        };

        [JsonIgnore]
        public HorizontalAlignment Alignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        [JsonIgnore]
        public string BubbleBackground => IsUser
            ? "#12161f"
            : (IsError ? "#1f1112" : "#0b0b10");

        [JsonIgnore]
        public string BubbleBorder => IsUser
            ? "#26324a"
            : (IsError ? "#5b2626" : "#1a1a22");

        [JsonIgnore]
        public string AvatarText => IsUser ? "AX" : (IsSystem ? "SYS" : "AI");

        [JsonIgnore]
        public string AvatarBackground => IsUser ? "#3f6fd1" : (IsSystem ? "#5f6672" : "#2f6fe6");

        [JsonIgnore]
        public string RoleColor => IsUser ? "#7fa8f0" : (IsError ? "#f87171" : "#5fd08a");

        [JsonIgnore]
        public string RoleBadgeText => IsUser ? "OPERATOR" : (!string.IsNullOrWhiteSpace(ModelName) ? ModelName.ToUpperInvariant() : "AI AGENT");

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
