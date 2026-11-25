using System.Collections.Generic;
using System.Linq;
using LlmodelBox.Runtime.Memory;
using Xunit;

namespace LlmodelBox.Tests
{
    // In-memory fake so the engine's distillation logic is tested without touching disk.
    internal sealed class FakeMemoryStore : IMemoryStore
    {
        private readonly Dictionary<string, ConversationMemory> _data = new();
        public ConversationMemory Load(string projectKey)
            => _data.TryGetValue(projectKey, out var m) ? m : new ConversationMemory();
        public void Save(string projectKey, ConversationMemory memory) => _data[projectKey] = memory;
        public void Clear(string projectKey) => _data.Remove(projectKey);
    }

    public class MemoryEngineTests
    {
        [Fact]
        public void FirstUserTurn_BecomesSessionGoal()
        {
            var engine = new MemoryEngine(new FakeMemoryStore());
            engine.Observe("proj", "Help me build a REST API in Rust", "ok");
            var snap = engine.Recall("proj");
            Assert.Equal("Help me build a REST API in Rust", snap.SessionGoal);
        }

        [Fact]
        public void SessionGoal_IsStableAcrossTurns()
        {
            var engine = new MemoryEngine(new FakeMemoryStore());
            engine.Observe("proj", "First goal", "ok");
            engine.Observe("proj", "A later unrelated question", "ok");
            Assert.Equal("First goal", engine.Recall("proj").SessionGoal);
        }

        [Fact]
        public void ExtractsName_LikesAndStack()
        {
            var facts = MemoryEngine.ExtractFacts("My name is Alex and I love Rust and I'm using .NET 8").ToList();
            Assert.Contains(facts, f => f.Contains("Alex"));
            Assert.Contains(facts, f => f.Contains("Rust"));
            Assert.Contains(facts, f => f.Contains(".NET 8"));
        }

        [Fact]
        public void Facts_AreDeduplicated()
        {
            var store = new FakeMemoryStore();
            var engine = new MemoryEngine(store);
            engine.Observe("proj", "my name is Alex", "ok");
            engine.Observe("proj", "my name is Alex", "ok");
            Assert.Single(engine.Recall("proj").Facts, f => f.Contains("Alex"));
        }

        [Fact]
        public void BuildPreamble_NullWhenEmpty()
        {
            var engine = new MemoryEngine(new FakeMemoryStore());
            Assert.Null(engine.BuildPreamble(new MemorySnapshot()));
        }

        [Fact]
        public void BuildPreamble_ContainsGoalAndFacts()
        {
            var engine = new MemoryEngine(new FakeMemoryStore());
            engine.Observe("proj", "Build a compiler; my name is Alex", "ok");
            var text = engine.BuildPreamble(engine.Recall("proj"));
            Assert.NotNull(text);
            Assert.Contains("Session goal:", text);
            Assert.Contains("Alex", text);
        }

        [Fact]
        public void Forget_ClearsEverything()
        {
            var engine = new MemoryEngine(new FakeMemoryStore());
            engine.Observe("proj", "my name is Alex", "ok");
            engine.Forget("proj");
            Assert.True(engine.Recall("proj").IsEmpty);
        }

        [Fact]
        public void NullProjectKey_IsNoOp()
        {
            var engine = new MemoryEngine(new FakeMemoryStore());
            engine.Observe(null, "my name is Alex", "ok");
            Assert.True(engine.Recall(null).IsEmpty);
        }
    }
}
