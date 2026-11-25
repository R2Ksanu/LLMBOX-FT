using System.Linq;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Context;
using Xunit;

namespace LlmodelBox.Tests
{
    public class ContextEngineTests
    {
        private static ContextRequest MakeRequest(string prompt) => new()
        {
            Model = new ModelItem { Id = "m", Name = "M", Provider = "CloudApi" },
            Settings = new UserSettings(),
            UserPrompt = prompt
        };

        [Fact]
        public void Build_AlwaysIncludesUserTurnLast()
        {
            var engine = new ContextEngine();
            var result = engine.Build(MakeRequest("hello world"), ModelCapabilities.Default());

            Assert.NotEmpty(result.Request.Messages);
            var last = result.Request.Messages[^1];
            Assert.Equal(AiRole.User, last.Role);
            Assert.Contains("hello world", last.Text);
            Assert.True(result.InputTokenEstimate > 0);
        }

        [Fact]
        public void Build_DropsOldestHistoryWhenBudgetTiny()
        {
            var req = MakeRequest("current question");
            for (int i = 0; i < 50; i++)
            {
                req.History.Add(AiMessage.User($"old user turn number {i} with some filler text to spend tokens"));
                req.History.Add(AiMessage.Assistant($"old assistant reply number {i} with more filler content here"));
            }

            // Tiny window forces heavy compaction.
            var caps = new ModelCapabilities { ContextWindow = 600, InputTokenLimit = 600, OutputTokenLimit = 256 };
            var result = new ContextEngine().Build(req, caps);

            Assert.True(result.DroppedHistoryTurns > 0);
            Assert.Contains("current question", result.Request.Messages[^1].Text);
            Assert.True(result.InputTokenEstimate <= result.InputBudget + 64); // stays near budget
        }

        [Fact]
        public void Build_TruncatesOversizedOpenFile()
        {
            var req = MakeRequest("explain this file");
            req.OpenFilePath = @"C:\proj\big.cs";
            req.OpenFileText = new string('x', 100_000);

            var caps = new ModelCapabilities { ContextWindow = 4096, InputTokenLimit = 4096, OutputTokenLimit = 512 };
            var result = new ContextEngine().Build(req, caps);

            Assert.True(result.OpenFileIncluded);
            Assert.True(result.OpenFileTruncated);
            Assert.Contains("big.cs", result.Request.Messages[^1].Text);
        }

        [Fact]
        public void Build_KeepsRecentHistoryOldestFirstOrder()
        {
            var req = MakeRequest("latest");
            req.History.Add(AiMessage.User("first"));
            req.History.Add(AiMessage.Assistant("second"));

            var result = new ContextEngine().Build(req, ModelCapabilities.Default());

            // Order preserved: first, second, then the live user turn.
            var texts = result.Request.Messages.Select(m => m.Text).ToList();
            Assert.Equal("first", texts[0]);
            Assert.Equal("second", texts[1]);
            Assert.Contains("latest", texts[^1]);
        }
    }
}
