using System;
using System.IO;
using System.Linq;
using LlmodelBox.Runtime.Knowledge;
using Xunit;

namespace LlmodelBox.Tests
{
    // Deterministic tests for the Phase 6 Knowledge Engine. Retrieval is keyword/TF-IDF-lite, so these
    // assert vocabulary-overlap behaviour, not semantic recall. A temp directory stands in for a project.
    public class KnowledgeEngineTests : IDisposable
    {
        private readonly string _root;

        public KnowledgeEngineTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "kb_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void Write(string rel, string text)
        {
            var full = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        [Fact]
        public void Tokenize_DropsShortTokensAndStopwords()
        {
            var toks = KnowledgeEngine.Tokenize("The Widget handles Payment for the user").ToList();
            Assert.Contains("widget", toks);
            Assert.Contains("handles", toks);
            Assert.Contains("payment", toks);
            Assert.DoesNotContain("the", toks); // stopword
            Assert.DoesNotContain("for", toks); // stopword
        }

        [Fact]
        public void MissingRoot_YieldsEmptyIndex()
        {
            var engine = new KnowledgeEngine();
            Assert.True(engine.Index(null).IsEmpty);
            Assert.True(engine.Index(Path.Combine(_root, "does-not-exist")).IsEmpty);
        }

        [Fact]
        public void Index_SkipsBinaryDirsAndNonTextFiles()
        {
            Write("src/Payment.cs", "class PaymentProcessor { void Charge() {} }");
            Write("bin/ignored.cs", "class ShouldBeSkipped {}");
            Write("assets/logo.png", "not really an image but wrong extension");

            var engine = new KnowledgeEngine();
            var index = engine.Index(_root);

            Assert.False(index.IsEmpty);
            Assert.Contains(index.Chunks, c => c.RelativePath.Contains("Payment.cs"));
            Assert.DoesNotContain(index.Chunks, c => c.RelativePath.Contains("ignored.cs"));
            Assert.DoesNotContain(index.Chunks, c => c.RelativePath.Contains("logo.png"));
        }

        [Fact]
        public void Retrieve_RanksMostRelevantFileFirst()
        {
            Write("Payment.cs", "class PaymentProcessor { void ChargeCreditCard() { } }");
            Write("Logging.cs", "class Logger { void WriteInfo() { } }");

            var engine = new KnowledgeEngine();
            var index = engine.Index(_root);
            var hits = engine.Retrieve(index, "how does the payment credit charge work", 3);

            Assert.NotEmpty(hits);
            Assert.Contains("Payment.cs", hits[0].RelativePath);
        }

        [Fact]
        public void Retrieve_EmptyQuery_ReturnsNothing()
        {
            Write("A.cs", "class Alpha {}");
            var engine = new KnowledgeEngine();
            var index = engine.Index(_root);
            Assert.Empty(engine.Retrieve(index, "", 3));
            Assert.Empty(engine.Retrieve(index, null, 3));
        }

        [Fact]
        public void BuildKnowledgeBlock_RespectsCharBudget()
        {
            Write("Big.cs", new string('x', 50) + " charge payment " + new string('y', 5000));
            var engine = new KnowledgeEngine();
            var index = engine.Index(_root);
            var hits = engine.Retrieve(index, "charge payment", 3);
            var block = engine.BuildKnowledgeBlock(hits, 500);

            Assert.NotNull(block);
            Assert.True(block!.Length <= 520, $"block was {block.Length} chars");
        }

        [Fact]
        public void Index_IsCachedUntilFilesChange()
        {
            Write("A.cs", "class Alpha { void Charge() {} }");
            var engine = new KnowledgeEngine();
            var first = engine.Index(_root);
            var second = engine.Index(_root);
            Assert.Same(first, second); // unchanged tree → same cached instance

            Write("B.cs", "class Beta { void Refund() {} }");
            var third = engine.Index(_root);
            Assert.NotSame(first, third); // tree changed → rebuilt
        }
    }
}
