using System;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models.Ai;
using LlmodelBox.Services;

namespace LlmodelBox.Runtime.Tools
{
    // Resolves a model-requested call to a registered tool, gates it through the permission engine,
    // executes it, and normalizes every outcome (unknown tool, denied, thrown exception, success)
    // into an AiToolResult the agent loop can feed back to the model. Never throws for expected
    // failures — the model must always get a result to react to. Mirrors activity onto the
    // AgentActivityBus so the Neural Net view reflects tool use.
    public interface IToolExecutor
    {
        Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken);
    }

    public sealed class ToolExecutor : IToolExecutor
    {
        private readonly IToolRegistry _registry;
        private readonly IToolPermissionEngine _permissions;

        public ToolExecutor(IToolRegistry registry, IToolPermissionEngine permissions)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        }

        public async Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (call == null || string.IsNullOrWhiteSpace(call.Name))
                return Fail(call, "Malformed tool call (no name).");

            if (!_registry.TryGet(call.Name, out var tool))
            {
                AgentActivityBus.Emit(AgentNode.Reasoning, $"Unknown tool '{call.Name}'", "warn");
                return Fail(call, $"No tool named '{call.Name}' is registered.");
            }

            var decision = _permissions.Evaluate(tool, call);
            if (!decision.Allowed)
            {
                AgentActivityBus.Emit(AgentNode.Reasoning, $"Tool '{call.Name}' blocked: {decision.Reason}", "warn");
                return Fail(call, $"Permission denied: {decision.Reason}");
            }

            AgentActivityBus.Emit(AgentNode.Reasoning, $"Invoking tool '{call.Name}' ({tool.Risk})", "active");
            ToolExecutionTracker.Current?.EmitToolStart(call.Name, ToolCallFormatter.Summary(call.Name, call.ArgumentsJson));
            try
            {
                var result = await tool.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);
                result.ToolCallId = call.Id;
                result.Name = call.Name;
                AgentActivityBus.Emit(AgentNode.Response,
                    result.IsError ? $"Tool '{call.Name}' returned an error" : $"Tool '{call.Name}' completed",
                    result.IsError ? "warn" : "info");
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AgentActivityBus.Emit(AgentNode.Response, $"Tool '{call.Name}' threw: {ex.GetType().Name}", "error");
                return Fail(call, $"Tool threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static AiToolResult Fail(AiToolCall? call, string message) => new()
        {
            ToolCallId = call?.Id ?? string.Empty,
            Name = call?.Name ?? string.Empty,
            Output = message,
            IsError = true
        };
    }
}
