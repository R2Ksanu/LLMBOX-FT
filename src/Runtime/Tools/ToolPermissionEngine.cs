using System;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Runtime.Tools
{
    // A request for the user to approve (or deny) a tool invocation the model asked for.
    public sealed class ToolPermissionRequest
    {
        public string ToolName { get; init; } = string.Empty;
        public ToolRisk Risk { get; init; }
        public string ArgumentsJson { get; init; } = "{}";
        public string CallId { get; init; } = string.Empty;
    }

    // The outcome of a permission evaluation.
    public sealed class ToolPermissionDecision
    {
        public bool Allowed { get; init; }
        public string Reason { get; init; } = string.Empty;

        public static ToolPermissionDecision Allow(string reason = "") => new() { Allowed = true, Reason = reason };
        public static ToolPermissionDecision Deny(string reason) => new() { Allowed = false, Reason = reason };
    }

    // Decides whether a specific tool call may run. Safe tools pass automatically; anything Sensitive
    // or Dangerous is referred to a host-supplied approval callback. When no callback is wired, the
    // engine fails closed (deny) so nothing risky ever runs without an explicit decision.
    public interface IToolPermissionEngine
    {
        ToolPermissionDecision Evaluate(ITool tool, AiToolCall call);
    }

    public sealed class ToolPermissionEngine : IToolPermissionEngine
    {
        private readonly Func<ToolPermissionRequest, bool>? _approver;

        // approver: host callback (e.g. a UI prompt) returning true to allow. Null = deny all
        // non-Safe tools (fail closed).
        public ToolPermissionEngine(Func<ToolPermissionRequest, bool>? approver = null) => _approver = approver;

        public ToolPermissionDecision Evaluate(ITool tool, AiToolCall call)
        {
            if (tool == null) return ToolPermissionDecision.Deny("Unknown tool.");
            if (tool.Risk == ToolRisk.Safe) return ToolPermissionDecision.Allow("safe tool");

            if (_approver == null)
                return ToolPermissionDecision.Deny($"'{tool.Definition.Name}' requires approval but no approver is configured.");

            var req = new ToolPermissionRequest
            {
                ToolName = tool.Definition.Name,
                Risk = tool.Risk,
                ArgumentsJson = call?.ArgumentsJson ?? "{}",
                CallId = call?.Id ?? string.Empty
            };

            bool ok;
            try { ok = _approver(req); }
            catch { ok = false; } // a throwing approver fails closed

            return ok
                ? ToolPermissionDecision.Allow("approved by user")
                : ToolPermissionDecision.Deny("denied by user");
        }
    }
}
