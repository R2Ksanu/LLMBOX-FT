using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Tools;
using Xunit;

namespace LlmodelBox.Tests
{
    // A trivial tool that echoes a fixed output and records that it ran.
    internal sealed class EchoTool : ITool
    {
        private readonly ToolRisk _risk;
        public int Calls { get; private set; }
        public EchoTool(string name, ToolRisk risk = ToolRisk.Safe)
        {
            _risk = risk;
            Definition = new AiToolDefinition { Name = name, Description = "echo" };
        }
        public AiToolDefinition Definition { get; }
        public ToolRisk Risk => _risk;
        public Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new AiToolResult { ToolCallId = call.Id, Name = call.Name, Output = "echo:" + call.Name });
        }
    }

    public class AgentLoopTests
    {
        [Fact]
        public async Task ExecutesToolThenReturnsFinalAnswer()
        {
            var tool = new EchoTool("ping");
            var registry = new ToolRegistry(new[] { (ITool)tool });
            var executor = new ToolExecutor(registry, new ToolPermissionEngine());
            var loop = new AgentLoop(executor);

            int turn = 0;
            Task<AiResponse> RunTurn(AiRequest req, CancellationToken ct)
            {
                turn++;
                if (turn == 1)
                {
                    var r = new AiResponse { Text = "" };
                    r.ToolCalls.Add(new AiToolCall { Name = "ping", ArgumentsJson = "{}" });
                    return Task.FromResult(r);
                }
                return Task.FromResult(new AiResponse { Text = "done" });
            }

            var result = await loop.RunAsync(new AiRequest(), RunTurn, CancellationToken.None);

            Assert.Equal("done", result.Text);
            Assert.Equal(1, tool.Calls);
            Assert.Equal(2, turn);
        }

        [Fact]
        public async Task FeedsToolResultBackIntoConversation()
        {
            var registry = new ToolRegistry(new[] { (ITool)new EchoTool("ping") });
            var loop = new AgentLoop(new ToolExecutor(registry, new ToolPermissionEngine()));
            AiRequest? seen = null;

            int turn = 0;
            Task<AiResponse> RunTurn(AiRequest req, CancellationToken ct)
            {
                turn++;
                if (turn == 1)
                {
                    var r = new AiResponse();
                    r.ToolCalls.Add(new AiToolCall { Name = "ping" });
                    return Task.FromResult(r);
                }
                seen = req;
                return Task.FromResult(new AiResponse { Text = "done" });
            }

            await loop.RunAsync(new AiRequest(), RunTurn, CancellationToken.None);

            Assert.NotNull(seen);
            Assert.Contains(seen!.Messages, m => m.Role == AiRole.Assistant && m.ToolCalls.Count == 1);
            Assert.Contains(seen!.Messages, m => m.Role == AiRole.Tool && m.Text.StartsWith("echo:"));
        }

        [Fact]
        public async Task StopsAtIterationCeiling()
        {
            var registry = new ToolRegistry(new[] { (ITool)new EchoTool("loop") });
            var loop = new AgentLoop(new ToolExecutor(registry, new ToolPermissionEngine()), maxIterations: 3);

            int turn = 0;
            Task<AiResponse> RunTurn(AiRequest req, CancellationToken ct)
            {
                turn++;
                var r = new AiResponse(); // always asks for another tool call
                r.ToolCalls.Add(new AiToolCall { Name = "loop" });
                return Task.FromResult(r);
            }

            var result = await loop.RunAsync(new AiRequest(), RunTurn, CancellationToken.None);
            Assert.True(result.IsError);
            Assert.Equal("tool_iteration_limit", result.FinishReason);

            // 1 initial turn + 3 loop iterations (each ending in another routed turn) = 4 turns max.
            Assert.Equal(4, turn);
        }

        [Fact]
        public async Task DangerousToolDeniedWhenNoApprover()
        {
            var tool = new EchoTool("write", ToolRisk.Dangerous);
            var registry = new ToolRegistry(new[] { (ITool)tool });
            var executor = new ToolExecutor(registry, new ToolPermissionEngine(approver: null));

            var result = await executor.ExecuteAsync(new AiToolCall { Name = "write" }, CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Equal(0, tool.Calls);
            Assert.Contains("Permission denied", result.Output);
        }

        [Fact]
        public async Task DangerousToolRunsWhenApproved()
        {
            var tool = new EchoTool("write", ToolRisk.Dangerous);
            var registry = new ToolRegistry(new[] { (ITool)tool });
            var executor = new ToolExecutor(registry, new ToolPermissionEngine(approver: _ => true));

            var result = await executor.ExecuteAsync(new AiToolCall { Name = "write" }, CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Equal(1, tool.Calls);
        }

        [Fact]
        public async Task UnknownToolReturnsErrorResult()
        {
            var executor = new ToolExecutor(new ToolRegistry(), new ToolPermissionEngine());
            var result = await executor.ExecuteAsync(new AiToolCall { Name = "nope" }, CancellationToken.None);
            Assert.True(result.IsError);
            Assert.Contains("nope", result.Output);
        }
    }
}
