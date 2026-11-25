using System;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models.Ai;
using LlmodelBox.Services;

namespace LlmodelBox.Runtime.Tools
{
    // Drives the tool-use cycle: send the request, and while the model responds with tool calls,
    // execute them, append the calls + their results to the conversation, and send again — until the
    // model returns a plain answer or the iteration ceiling is hit. The per-turn transport is injected
    // as a delegate (AiRuntime.RunAsync(AiRequest)) so the loop is testable without a live provider
    // and stays decoupled from routing.
    //
    // If the provider never populates AiResponse.ToolCalls (e.g. a model/adapter without function
    // calling), this collapses to a single turn — identical to pre-Phase-5 behavior. Nothing fake.
    public interface IAgentLoop
    {
        Task<AiResponse> RunAsync(
            AiRequest request,
            Func<AiRequest, CancellationToken, Task<AiResponse>> runTurn,
            CancellationToken cancellationToken);
    }

    public sealed class AgentLoop : IAgentLoop
    {
        // Hard ceiling on tool round-trips to guarantee termination even if the model loops.
        private readonly int _maxIterations;
        private readonly IToolExecutor _executor;

        public AgentLoop(IToolExecutor executor, int maxIterations = 8)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _maxIterations = maxIterations < 1 ? 1 : maxIterations;
        }

        public async Task<AiResponse> RunAsync(
            AiRequest request,
            Func<AiRequest, CancellationToken, Task<AiResponse>> runTurn,
            CancellationToken cancellationToken)
        {
            if (runTurn == null) throw new ArgumentNullException(nameof(runTurn));
            cancellationToken.ThrowIfCancellationRequested();

            AiResponse response = await runTurn(request, cancellationToken).ConfigureAwait(false);

            int iteration = 0;
            while (!response.IsError && response.ToolCalls.Count > 0)
            {
                if (iteration >= _maxIterations)
                {
                    AgentActivityBus.Emit(AgentNode.Reasoning,
                        $"Tool loop stopped at {_maxIterations} iterations", "warn");
                    response.IsError = true;
                    response.FinishReason = "tool_iteration_limit";
                    response.ErrorMessage = $"Stopped after {_maxIterations} tool rounds. The task may be incomplete.";
                    response.Text = response.ErrorMessage;
                    break;
                }
                iteration++;
                AgentActivityBus.Emit(AgentNode.Reasoning,
                    $"Planning tool use · round {iteration} · {response.ToolCalls.Count} call(s)", "active");

                // Record the assistant turn that requested the tools.
                var assistantTurn = new AiMessage { Role = AiRole.Assistant };
                if (!string.IsNullOrEmpty(response.Text))
                    assistantTurn.Parts.Add(AiContentPart.FromText(response.Text));
                assistantTurn.ToolCalls = response.ToolCalls;
                request.Messages.Add(assistantTurn);

                // Execute each requested call and feed the results back as Tool-role messages. Each
                // step is mirrored onto the bus (call → result) so the UI can show what the agent is
                // actually doing — reading, writing, listing — rather than an opaque "thinking".
                foreach (var call in response.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AgentActivityBus.Emit(AgentNode.Reasoning, $"→ {Describe(call)}", "active");

                    var result = await _executor.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);

                    AgentActivityBus.Emit(AgentNode.Response,
                        result.IsError
                            ? $"✗ {call.Name} failed: {Truncate(result.Output, 100)}"
                            : $"✓ {call.Name} → {result.Output?.Length ?? 0:N0} chars",
                        result.IsError ? "error" : "info");

                    request.Messages.Add(new AiMessage
                    {
                        Role = AiRole.Tool,
                        Name = result.Name,
                        ToolCallId = result.ToolCallId,
                        Parts = { AiContentPart.FromText(result.Output ?? string.Empty) }
                    });
                }

                response = await runTurn(request, cancellationToken).ConfigureAwait(false);
            }

            return response;
        }

        // A short, human-readable action label for a tool call — e.g. "Writing info.txt" — so the
        // step reads as an activity rather than a raw JSON blob in the chat pill / activity view.
        private static string Describe(AiToolCall call)
            => ToolCallFormatter.Summary(call.Name, call.ArgumentsJson);

        private static string Truncate(string? s, int max)
        {
            s ??= string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
