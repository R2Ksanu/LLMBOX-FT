using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Common;
using LlmodelBox.Controls.RichChat;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Providers;
using LlmodelBox.Runtime;
using LlmodelBox.Runtime.Content;
using LlmodelBox.Runtime.Context;
using LlmodelBox.Runtime.Memory;
using LlmodelBox.Runtime.Tools;
using LlmodelBox.Runtime.Tools.Builtin;
using Xunit;

namespace LlmodelBox.Tests
{
    public class RichChatTests
    {
        private sealed class FakeStreamingRouter : IModelRouter, IModelProvider
        {
            private readonly Func<AiRequest, CancellationToken, IEnumerable<AiEvent>>? _streamFactory;
            private readonly Func<AiRequest, CancellationToken, AiResponse>? _generateFactory;
            private readonly ModelCapabilities _caps;

            public FakeStreamingRouter(
                ModelCapabilities? caps = null,
                Func<AiRequest, CancellationToken, IEnumerable<AiEvent>>? streamFactory = null,
                Func<AiRequest, CancellationToken, AiResponse>? generateFactory = null)
            {
                _caps = caps ?? new ModelCapabilities { Streaming = true, ToolCalling = true };
                _streamFactory = streamFactory;
                _generateFactory = generateFactory;
            }

            public string Name => "Fake";
            public bool CanHandle(ModelItem model) => true;
            public ModelCapabilities GetCapabilities(ModelItem model) => _caps;
            public IModelProvider Select(AiRequest request) => this;

            public Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(_generateFactory?.Invoke(request, ct) ?? new AiResponse { Text = "ok" });
            }

            public Task<AiResponse> RouteAsync(AiRequest request, CancellationToken ct)
                => GenerateAsync(request, ct);

            public async IAsyncEnumerable<AiEvent> StreamAsync(
                AiRequest request,
                [EnumeratorCancellation] CancellationToken ct)
            {
                if (_streamFactory != null)
                {
                    foreach (var evt in _streamFactory(request, ct))
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Yield();
                        yield return evt;
                    }
                    yield break;
                }

                yield return AiEvent.Done(await GenerateAsync(request, ct));
            }

            public IAsyncEnumerable<AiEvent> RouteStreamAsync(AiRequest request, CancellationToken ct)
                => StreamAsync(request, ct);
        }

        [Fact]
        public void StructuredContentParser_ParsesMarkdownCodeTableTasksMermaidChartDiffAndCitations()
        {
            string input = """
# Architecture Overview

Here is **bold**, *italic*, ~~strike~~, escaped \*star\*, and [Docs](https://example.com/docs).

```csharp
public class Hello
{
    public int Value => 42;
}
```

| Model | LatencyMs | Cost |
|---|---:|---:|
| GPT-4o | 120 | 0.01 |
| Nemotron | 95 | 0.00 |

- [x] Inspect architecture
- [ ] Add rich visual cards

```mermaid
flowchart LR
    User --> Runtime --> Cards
```

```chart
{
  "kind": "Bar",
  "title": "Latency Comparison",
  "series": [
    {
      "name": "ms",
      "points": [
        { "label": "A", "y": 120 },
        { "label": "B", "y": 95 }
      ]
    }
  ]
}
```

```diff
--- a/Foo.cs
+++ b/Foo.cs
@@ -1 +1 @@
-old
+new
```

```citations
[
  { "title": "Markdig Docs", "domain": "github.com", "url": "https://github.com/xoofx/markdig", "description": "CommonMark + GFM parser" }
]
```
""";

            var blocks = StructuredContentParser.Parse(input);

            Assert.Contains(blocks, b => b is MarkdownBlock md && md.Text.Contains("Architecture Overview"));
            var code = Assert.Single(blocks.OfType<CodeBlock>());
            Assert.Equal("csharp", code.Language);
            Assert.Contains("Value => 42", code.Code);

            var table = Assert.Single(blocks.OfType<TableBlock>());
            Assert.Equal(new[] { "Model", "LatencyMs", "Cost" }, table.Headers);
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal("Nemotron", table.Rows[1][0]);

            var tasks = Assert.Single(blocks.OfType<TaskListBlock>());
            Assert.Equal(2, tasks.Tasks.Count);
            Assert.True(tasks.Tasks[0].IsCompleted);
            Assert.False(tasks.Tasks[1].IsCompleted);

            var mermaid = Assert.Single(blocks.OfType<MermaidBlock>());
            Assert.Equal("flowchart", mermaid.DiagramType);
            Assert.Contains("User --> Runtime", mermaid.Source);

            var chart = Assert.Single(blocks.OfType<ChartBlock>());
            Assert.Equal(ChartKind.Bar, chart.Kind);
            Assert.Equal("Latency Comparison", chart.Title);
            Assert.Equal(2, chart.Series[0].Points.Count);

            Assert.Single(blocks.OfType<CodeDiffBlock>());

            var cites = Assert.Single(blocks.OfType<CitationBlock>());
            Assert.Single(cites.Sources);
            Assert.Equal("github.com", cites.Sources[0].Domain);
        }

        [Fact]
        public void StructuredContentParser_IncompleteStreamingMarkdownAndMalformedJson_NeverThrow()
        {
            string partial = "## Streaming\n\nHere is `unclosed code\n\n```chart\n{ broken json\n\n| A | B |\n|---|";
            var blocks = StructuredContentParser.Parse(partial);

            Assert.NotEmpty(blocks);
            // Broken chart JSON safely falls back rather than throwing
            Assert.DoesNotContain(blocks, b => b is ChartBlock);
        }

        [Theory]
        [InlineData("Line")]
        [InlineData("Bar")]
        [InlineData("HorizontalBar")]
        [InlineData("Pie")]
        [InlineData("Donut")]
        [InlineData("Area")]
        [InlineData("Scatter")]
        public void StructuredContentParser_SupportsAllChartKinds(string kindName)
        {
            string md = "```json-chart\n{\"kind\":\"" + kindName + "\",\"title\":\"Test\",\"series\":[{\"name\":\"S1\",\"points\":[{\"label\":\"X\",\"x\":1,\"y\":10},{\"label\":\"Y\",\"x\":2,\"y\":25}]}]}\n```";
            var blocks = StructuredContentParser.Parse(md);
            var chart = Assert.Single(blocks.OfType<ChartBlock>());
            Assert.Equal(Enum.Parse<ChartKind>(kindName), chart.Kind);
        }

        [Fact]
        public void WebLinkSanitizer_AllowsHttpHttpsAndRejectsUnsafeSchemes()
        {
            Assert.True(WebLink.TryParse("https://example.com/path", out _));
            Assert.True(WebLink.TryParse("http://localhost:8080", out _));
            Assert.False(WebLink.TryParse("javascript:alert(1)", out _));
            Assert.False(WebLink.TryParse("file:///C:/Windows/System32/cmd.exe", out _));
        }

        [Fact]
        public async Task AiRuntime_RunWithProgressAsync_StreamsTokenDeltas()
        {
            var router = new FakeStreamingRouter(
                new ModelCapabilities { Streaming = true, ToolCalling = false },
                streamFactory: (_, _) => new[]
                {
                    AiEvent.Delta("Hello "),
                    AiEvent.Delta("**world**!"),
                    AiEvent.Done(new AiResponse { Text = "Hello **world**!" })
                });

            var runtime = new AiRuntime(router);
            var progressEvents = new List<RuntimeProgressEvent>();

            var resp = await runtime.RunWithProgressAsync(new ContextRequest
            {
                Model = new ModelItem { Id = "test", Name = "TestModel", Provider = "Fake" },
                Settings = new UserSettings(),
                UserPrompt = "hi"
            }, progressEvents.Add, CancellationToken.None);

            Assert.False(resp.IsError);
            Assert.Equal("Hello **world**!", resp.Text);
            Assert.Contains(progressEvents, e => e.State == MessageActivityState.Streaming && e.TokenDelta == "Hello ");
            Assert.Contains(progressEvents, e => e.State == MessageActivityState.Streaming && e.TokenDelta == "**world**!");
            Assert.Contains(progressEvents, e => e.State == MessageActivityState.Completed);
        }

        [Fact]
        public async Task AiRuntime_RunWithProgressAsync_RecordsRealWriteFileEditsAndDiffSummary()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "LlmodelBox_RichTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string targetFile = Path.Combine(tempDir, "Demo.cs");
            await File.WriteAllTextAsync(targetFile, "line1\nline2\n");

            try
            {
                int call = 0;
                var router = new FakeStreamingRouter(
                    new ModelCapabilities { Streaming = false, ToolCalling = true },
                    generateFactory: (_, _) =>
                    {
                        call++;
                        if (call == 1)
                        {
                            return new AiResponse
                            {
                                ToolCalls = new List<AiToolCall>
                                {
                                    new AiToolCall
                                    {
                                        Id = "tc1",
                                        Name = "write_file",
                                        ArgumentsJson = "{\"path\":\"Demo.cs\",\"content\":\"line1\\nline2_updated\\nline3\\n\"}"
                                    }
                                }
                            };
                        }
                        return new AiResponse { Text = "Updated Demo.cs cleanly." };
                    });

                var registry = new ToolRegistry();
                registry.Register(new WriteFileTool(tempDir));
                var executor = new ToolExecutor(registry, new ToolPermissionEngine(_ => true));
                var loop = new AgentLoop(executor);
                var runtime = new AiRuntime(router, new ContextEngine(), new MemoryEngine(), registry, loop);

                var events = new List<RuntimeProgressEvent>();
                var resp = await runtime.RunWithProgressAsync(new ContextRequest
                {
                    Model = new ModelItem { Id = "m1", Name = "AgentModel", Provider = "Fake" },
                    Settings = new UserSettings(),
                    UserPrompt = "Update Demo.cs",
                    ProjectKey = tempDir
                }, events.Add, CancellationToken.None);

                Assert.False(resp.IsError);
                Assert.Contains(events, e => e.State == MessageActivityState.EditingFiles && (e.FilePath?.EndsWith("Demo.cs", StringComparison.OrdinalIgnoreCase) ?? false));

                var summary = Assert.Single(resp.StructuredBlocks.OfType<DiffSummaryBlock>());
                Assert.Equal(1, summary.FilesChanged);
                Assert.True(summary.Additions >= 2);
                Assert.True(summary.Deletions >= 1);
                var entry = Assert.Single(summary.Files);
                Assert.Equal("Demo.cs", entry.FilePath);
                Assert.Contains("line2_updated", entry.AfterContent);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task AiRuntime_RunWithProgressAsync_CancellationPropagatesCleanly()
        {
            using var cts = new CancellationTokenSource();
            var router = new FakeStreamingRouter(
                new ModelCapabilities { Streaming = true, ToolCalling = false },
                streamFactory: (_, token) =>
                {
                    cts.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Array.Empty<AiEvent>();
                });

            var runtime = new AiRuntime(router);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runtime.RunWithProgressAsync(new ContextRequest
                {
                    Model = new ModelItem { Id = "m1", Name = "M1", Provider = "Fake" },
                    Settings = new UserSettings(),
                    UserPrompt = "stream"
                }, _ => { }, cts.Token));
        }

        [Fact]
        public void JsonChatHistoryStore_RoundTripsStructuredBlocksAndTaskStatuses()
        {
            string tempProject = Path.Combine(Path.GetTempPath(), "LlmodelBox_Persist_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempProject);
            try
            {
                var store = new JsonChatHistoryStore();
                var messages = new List<ChatMessageItem>
                {
                    new ChatMessageItem
                    {
                        SenderRole = "Assistant",
                        SenderName = "Nemotron",
                        ModelName = "Nemotron",
                        ActivityState = MessageActivityState.Completed,
                        Content = "Done",
                        ToolSteps = new List<string> { "write_file: Demo.cs (+3/-1)" },
                        Blocks = new List<ChatContentBlock>
                        {
                            new MarkdownBlock { Text = "Done" },
                            new TaskListBlock
                            {
                                Title = "Checklist",
                                Tasks = new List<TaskChecklistItem>
                                {
                                    new TaskChecklistItem { Title = "Build", IsCompleted = true },
                                    new TaskChecklistItem { Title = "Test", IsCompleted = false }
                                }
                            },
                            new DiffSummaryBlock
                            {
                                FilesChanged = 1,
                                Additions = 3,
                                Deletions = 1,
                                Files = new List<ChangedFileEntry>
                                {
                                    new ChangedFileEntry { FilePath = "Demo.cs", Additions = 3, Deletions = 1 }
                                }
                            },
                            new ChartBlock
                            {
                                Kind = ChartKind.Donut,
                                Title = "Tokens",
                                Series = new List<ChartSeries>
                                {
                                    new ChartSeries
                                    {
                                        Name = "Usage",
                                        Points = new List<ChartDataPoint> { new() { Label = "In", Y = 100 }, new() { Label = "Out", Y = 50 } }
                                    }
                                }
                            }
                        }
                    }
                };

                store.Save(tempProject, messages);
                var reloaded = store.Load(tempProject);

                var msg = Assert.Single(reloaded);
                Assert.Equal(4, msg.Blocks.Count);
                Assert.Single(msg.ToolSteps);

                var taskList = Assert.IsType<TaskListBlock>(msg.Blocks[1]);
                Assert.True(taskList.Tasks[0].IsCompleted);
                Assert.False(taskList.Tasks[1].IsCompleted);

                var diff = Assert.IsType<DiffSummaryBlock>(msg.Blocks[2]);
                Assert.Equal("Demo.cs", diff.Files[0].FilePath);

                var chart = Assert.IsType<ChartBlock>(msg.Blocks[3]);
                Assert.Equal(ChartKind.Donut, chart.Kind);
            }
            finally
            {
                try { Directory.Delete(tempProject, recursive: true); } catch { }
            }
        }
    }
}
