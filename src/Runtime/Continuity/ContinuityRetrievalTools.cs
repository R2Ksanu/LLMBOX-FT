using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Knowledge;
using LlmodelBox.Runtime.Tools;

namespace LlmodelBox.Runtime.Continuity
{
    internal static class ContinuityToolHelper
    {
        public static JsonDocument Parse(string? json)
            => string.IsNullOrWhiteSpace(json) ? JsonDocument.Parse("{}") : JsonDocument.Parse(json);

        public static string GetString(JsonElement root, string name, string fallback = "")
            => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? (el.GetString() ?? fallback)
                : fallback;

        public static int GetInt(JsonElement root, string name, int fallback)
            => root.TryGetProperty(name, out var el) && el.TryGetInt32(out int val) ? val : fallback;

        public static AiToolResult Ok(AiToolCall call, string content) => new()
        {
            ToolCallId = call.Id,
            Name = call.Name,
            IsError = false,
            Output = content
        };

        public static AiToolResult Fail(AiToolCall call, string error) => new()
        {
            ToolCallId = call.Id,
            Name = call.Name,
            IsError = true,
            Output = error
        };
    }

    public sealed class SearchConversationHistoryTool : ITool
    {
        private readonly IConversationRepository _repo;
        private readonly string? _defaultProjectKey;

        public ToolRisk Risk => ToolRisk.Safe;
        public AiToolDefinition Definition { get; } = new()
        {
            Name = "SearchConversationHistory",
            Description = "Search prior project conversations, checkpoints, events, and memories (compact index only).",
            ParametersJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}},\"required\":[\"query\"]}"
        };

        public SearchConversationHistoryTool(IConversationRepository repo, string? defaultProjectKey = null)
        {
            _repo = repo;
            _defaultProjectKey = defaultProjectKey;
        }

        public Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var doc = ContinuityToolHelper.Parse(call.ArgumentsJson);
            string query = ContinuityToolHelper.GetString(doc.RootElement, "query");
            int limit = Math.Clamp(ContinuityToolHelper.GetInt(doc.RootElement, "limit", 8), 1, 20);
            if (string.IsNullOrWhiteSpace(query))
                return Task.FromResult(ContinuityToolHelper.Fail(call, "query is required."));

            var hits = _repo.Search(query, _defaultProjectKey, crossProject: false, limit);
            if (hits.Count == 0)
                return Task.FromResult(ContinuityToolHelper.Ok(call, "No matching history records in this project."));

            var sb = new StringBuilder();
            foreach (var h in hits)
                sb.AppendLine($"[{h.Kind}] id={h.Id} conv={h.ConversationId} ({h.TimestampUtc:yyyy-MM-dd HH:mm}Z) | {h.Title}: {h.Snippet}");
            return Task.FromResult(ContinuityToolHelper.Ok(call, sb.ToString().TrimEnd()));
        }
    }

    public sealed class GetConversationTimelineTool : ITool
    {
        private readonly IConversationRepository _repo;

        public ToolRisk Risk => ToolRisk.Safe;
        public AiToolDefinition Definition { get; } = new()
        {
            Name = "GetConversationTimeline",
            Description = "Fetch chronological messages around an anchor message ID.",
            ParametersJson = "{\"type\":\"object\",\"properties\":{\"anchorId\":{\"type\":\"string\"},\"before\":{\"type\":\"integer\"},\"after\":{\"type\":\"integer\"}},\"required\":[\"anchorId\"]}"
        };

        public GetConversationTimelineTool(IConversationRepository repo) => _repo = repo;

        public Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var doc = ContinuityToolHelper.Parse(call.ArgumentsJson);
            string anchorId = ContinuityToolHelper.GetString(doc.RootElement, "anchorId");
            int before = Math.Clamp(ContinuityToolHelper.GetInt(doc.RootElement, "before", 3), 0, 10);
            int after = Math.Clamp(ContinuityToolHelper.GetInt(doc.RootElement, "after", 3), 0, 10);
            if (string.IsNullOrWhiteSpace(anchorId))
                return Task.FromResult(ContinuityToolHelper.Fail(call, "anchorId is required."));

            var slice = _repo.GetMessagesAround(anchorId, before, after);
            if (slice.Count == 0)
                return Task.FromResult(ContinuityToolHelper.Ok(call, "No messages found for anchorId."));

            var sb = new StringBuilder();
            foreach (var m in slice)
                sb.AppendLine($"#{m.SequenceNumber} [{m.CreatedAtUtc:yyyy-MM-dd HH:mm}Z] {m.SenderRole} ({m.TurnStatus}): {m.Content}");
            return Task.FromResult(ContinuityToolHelper.Ok(call, sb.ToString().TrimEnd()));
        }
    }

    public sealed class GetConversationDetailsTool : ITool
    {
        private readonly IConversationRepository _repo;

        public ToolRisk Risk => ToolRisk.Safe;
        public AiToolDefinition Definition { get; } = new()
        {
            Name = "GetConversationDetails",
            Description = "Fetch full checkpoint and recent details for a conversation ID.",
            ParametersJson = "{\"type\":\"object\",\"properties\":{\"conversationId\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}},\"required\":[\"conversationId\"]}"
        };

        public GetConversationDetailsTool(IConversationRepository repo) => _repo = repo;

        public Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var doc = ContinuityToolHelper.Parse(call.ArgumentsJson);
            string cid = ContinuityToolHelper.GetString(doc.RootElement, "conversationId");
            int limit = Math.Clamp(ContinuityToolHelper.GetInt(doc.RootElement, "limit", 12), 1, 30);
            var conv = _repo.GetConversation(cid);
            if (conv == null) return Task.FromResult(ContinuityToolHelper.Ok(call, "Conversation not found."));

            var cp = _repo.GetLatestCheckpoint(cid);
            var msgs = _repo.GetMessages(cid).TakeLast(limit).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"Conversation: {conv.Title} ({conv.Status})");
            if (cp != null)
            {
                sb.AppendLine($"Checkpoint v{cp.CheckpointVersion}: Goal={cp.CurrentGoal}");
                sb.AppendLine($"NextStep={cp.NextStep}");
            }
            foreach (var m in msgs)
                sb.AppendLine($"- #{m.SequenceNumber} [{m.SenderRole}]: {m.Content}");
            return Task.FromResult(ContinuityToolHelper.Ok(call, sb.ToString().TrimEnd()));
        }
    }

    public sealed class GetProjectMemoryTool : ITool
    {
        private readonly IConversationRepository _repo;
        private readonly string? _defaultProjectKey;

        public ToolRisk Risk => ToolRisk.Safe;
        public AiToolDefinition Definition { get; } = new()
        {
            Name = "GetProjectMemory",
            Description = "Retrieve active scoped memory records with provenance for this project.",
            ParametersJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}}}"
        };

        public GetProjectMemoryTool(IConversationRepository repo, string? defaultProjectKey = null)
        {
            _repo = repo;
            _defaultProjectKey = defaultProjectKey;
        }

        public Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var doc = ContinuityToolHelper.Parse(call.ArgumentsJson);
            string q = ContinuityToolHelper.GetString(doc.RootElement, "query");
            int limit = Math.Clamp(ContinuityToolHelper.GetInt(doc.RootElement, "limit", 10), 1, 25);

            var recs = _repo.QueryMemories(null, _defaultProjectKey, includeInactive: false);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var terms = KnowledgeEngine.Tokenize(q).ToHashSet(StringComparer.OrdinalIgnoreCase);
                recs = recs
                    .OrderByDescending(r => KnowledgeEngine.Tokenize(r.Content).Count(t => terms.Contains(t)))
                    .ThenByDescending(r => r.Confidence)
                    .ToList();
            }

            var sb = new StringBuilder();
            foreach (var r in recs.Take(limit))
                sb.AppendLine($"- [{r.Scope} | {r.Provenance} | conf={r.Confidence:0.00}] {r.Content}");
            return Task.FromResult(ContinuityToolHelper.Ok(call, sb.Length > 0 ? sb.ToString().TrimEnd() : "No matching project memories."));
        }
    }

    public sealed class GetProjectKnowledgeTool : ITool
    {
        private readonly IKnowledgeEngine _knowledge;
        private readonly string? _projectRoot;

        public ToolRisk Risk => ToolRisk.Safe;
        public AiToolDefinition Definition { get; } = new()
        {
            Name = "GetProjectKnowledge",
            Description = "Retrieve relevant code excerpts with file paths and line ranges.",
            ParametersJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}},\"required\":[\"query\"]}"
        };

        public GetProjectKnowledgeTool(IKnowledgeEngine knowledge, string? projectRoot)
        {
            _knowledge = knowledge;
            _projectRoot = projectRoot;
        }

        public Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var doc = ContinuityToolHelper.Parse(call.ArgumentsJson);
            string q = ContinuityToolHelper.GetString(doc.RootElement, "query");
            int limit = Math.Clamp(ContinuityToolHelper.GetInt(doc.RootElement, "limit", 4), 1, 8);
            var index = _knowledge.Index(_projectRoot);
            if (index.IsEmpty) return Task.FromResult(ContinuityToolHelper.Ok(call, "Project index is empty."));
            var hits = _knowledge.Retrieve(index, q, limit);
            return Task.FromResult(ContinuityToolHelper.Ok(call, _knowledge.BuildKnowledgeBlock(hits, 4000) ?? "No matching code snippets."));
        }
    }
}
