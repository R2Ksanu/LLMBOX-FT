using System.Linq;
using LlmodelBox.Runtime.Intent;
using Xunit;

namespace LlmodelBox.Tests
{
    // Deterministic tests for the Phase 7 Intent + Task Engine. Classification is keyword/heuristic, so
    // these assert the gating behaviour (tools/knowledge) and task decomposition, not semantic nuance.
    public class IntentEngineTests
    {
        private readonly IntentEngine _engine = new();

        [Theory]
        [InlineData("hi")]
        [InlineData("hello there")]
        [InlineData("thanks!")]
        [InlineData("ok cool")]
        public void SmallTalk_IsChat_NoToolsNoKnowledge(string prompt)
        {
            var r = _engine.Classify(prompt);
            Assert.Equal(IntentKind.Chat, r.Kind);
            Assert.False(r.WantsTools);
            Assert.False(r.WantsKnowledge);
        }

        [Theory]
        [InlineData("add a logout button")]
        [InlineData("fix the null reference in Payment.cs")]
        [InlineData("refactor the parser")]
        public void ImperativeEdits_WantToolsAndKnowledge(string prompt)
        {
            var r = _engine.Classify(prompt);
            Assert.Equal(IntentKind.EditCode, r.Kind);
            Assert.True(r.WantsTools);
            Assert.True(r.WantsKnowledge);
        }

        [Fact]
        public void ExplainQuestion_WantsKnowledgeNotTools()
        {
            var r = _engine.Classify("explain how the retry logic works");
            Assert.Equal(IntentKind.ExplainCode, r.Kind);
            Assert.True(r.WantsKnowledge);
            Assert.False(r.WantsTools);
        }

        [Fact]
        public void PlainQuestion_IsQuestion_WantsKnowledge()
        {
            var r = _engine.Classify("what is the capital of France?");
            Assert.Equal(IntentKind.Question, r.Kind);
            Assert.True(r.WantsKnowledge);
            Assert.False(r.WantsTools);
        }

        [Fact]
        public void Search_WantsKnowledge()
        {
            var r = _engine.Classify("find where the token budget is computed");
            Assert.Equal(IntentKind.Search, r.Kind);
            Assert.True(r.WantsKnowledge);
        }

        [Fact]
        public void Unknown_StaysPermissive()
        {
            var r = _engine.Classify("the deployment pipeline for release builds");
            Assert.Equal(IntentKind.Unknown, r.Kind);
            Assert.True(r.WantsTools);
            Assert.True(r.WantsKnowledge);
        }

        [Fact]
        public void Empty_IsChatWithNoTasks()
        {
            var r = _engine.Classify("   ");
            Assert.Equal(IntentKind.Chat, r.Kind);
            Assert.Empty(r.Tasks);
        }

        [Fact]
        public void SimpleTurn_YieldsSingleTask()
        {
            var r = _engine.Classify("add a save button");
            Assert.Single(r.Tasks);
        }

        [Fact]
        public void SequencedTurn_DecomposesOnThen()
        {
            var tasks = IntentEngine.Decompose("read the config then validate it and then write the output");
            Assert.Equal(3, tasks.Count);
            Assert.Equal("read the config", tasks[0]);
            Assert.Equal("validate it", tasks[1]);
            Assert.Equal("write the output", tasks[2]);
        }

        [Fact]
        public void NumberedList_DecomposesPerItem()
        {
            var tasks = IntentEngine.Decompose("1. parse input\n2. transform it\n3. emit json");
            Assert.Equal(3, tasks.Count);
            Assert.Equal("parse input", tasks[0]);
            Assert.Equal("emit json", tasks[2]);
        }

        [Fact]
        public void Decompose_CapsAtMaxTasks()
        {
            var tasks = IntentEngine.Decompose("a; b; c; d; e; f; g; h");
            Assert.True(tasks.Count <= 6);
        }
    }
}
