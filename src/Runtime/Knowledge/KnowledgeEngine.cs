using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LlmodelBox.Runtime.Knowledge
{
    // Phase 6 Knowledge Engine implementation. Deterministic TF-IDF-lite retrieval over a project's
    // text/code files. No embeddings, no network, no model round-trip — just term-overlap scoring, so
    // its recall is bounded by shared vocabulary. The index is cached per-root and rebuilt only when
    // the project's file set or newest write time changes.
    public sealed class KnowledgeEngine : IKnowledgeEngine
    {
        private const int ChunkChars = 1400;
        private const int MaxFileBytes = 512 * 1024; // skip files larger than 512 KB
        private const int MaxFiles = 400;
        private const int MaxChunks = 1200;
        private const int MinTokenLen = 3;

        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".xaml", ".txt", ".md", ".json", ".xml", ".yml", ".yaml", ".ini", ".cfg", ".config",
            ".js", ".ts", ".jsx", ".tsx", ".css", ".html", ".htm", ".py", ".java", ".kt", ".go", ".rs",
            ".c", ".cpp", ".h", ".hpp", ".sql", ".sh", ".ps1", ".bat", ".gradle", ".properties", ".toml", ".csv"
        };

        private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "dist", "build", "target",
            ".gradle", "app", "publish", "out"
        };

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "are", "but", "not", "you", "all", "any", "can", "had", "was", "our",
            "out", "get", "has", "how", "new", "now", "see", "two", "way", "use", "this", "that", "with",
            "from", "have", "will", "your", "they", "what", "when", "which", "were", "been", "into",
            "then", "them", "than", "some", "public", "private", "static", "void", "using", "namespace"
        };

        private readonly object _gate = new();
        private KnowledgeIndex? _cache;

        public KnowledgeIndex Index(string? projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
                return new KnowledgeIndex();

            var files = EnumerateCandidates(projectRoot!).ToList();
            long sig = ComputeSignature(files);

            lock (_gate)
            {
                if (_cache != null && _cache.Root == projectRoot && _cache.Signature == sig)
                    return _cache;
            }

            var index = Build(projectRoot!, files, sig);
            lock (_gate) { _cache = index; }
            return index;
        }

        // Bounded, filtered file walk: skips tool/output directories, non-text extensions, and oversized
        // files. Order is stable and capped so indexing a large tree can never run away.
        private static IEnumerable<string> EnumerateCandidates(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            int yielded = 0;
            while (pending.Count > 0 && yielded < MaxFiles)
            {
                string dir = pending.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(dir); }
                catch { continue; }
                foreach (var path in entries)
                {
                    if (yielded >= MaxFiles) break;
                    try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue; }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    if (Directory.Exists(path))
                    {
                        var name = Path.GetFileName(path);
                        if (!SkipDirs.Contains(name)) pending.Push(path);
                        continue;
                    }
                    if (!TextExtensions.Contains(Path.GetExtension(path))) continue;
                    long len;
                    try { len = new FileInfo(path).Length; } catch { continue; }
                    if (len <= 0 || len > MaxFileBytes) continue;
                    yielded++;
                    yield return path;
                }
            }
        }

        private static long ComputeSignature(IEnumerable<string> files)
        {
            long sig = 17;
            int count = 0;
            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    sig = unchecked(sig * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(f));
                    sig = unchecked(sig * 31 + fi.LastWriteTimeUtc.Ticks + fi.Length);
                }
                catch { /* ignore unreadable */ }
                count++;
            }
            return unchecked(sig * 31 + count);
        }

        private static KnowledgeIndex Build(string root, IReadOnlyList<string> files, long signature)
        {
            var index = new KnowledgeIndex { Root = root, BuiltUtc = DateTime.UtcNow, Signature = signature };
            foreach (var file in files)
            {
                if (index.Chunks.Count >= MaxChunks) break;
                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(text)) continue;

                string rel = Relative(root, file);
                foreach (var chunkText in SplitChunks(text))
                {
                    if (index.Chunks.Count >= MaxChunks) break;
                    var tf = new Dictionary<string, int>();
                    int tokens = 0;
                    foreach (var term in Tokenize(chunkText))
                    {
                        tf[term] = tf.TryGetValue(term, out var c) ? c + 1 : 1;
                        tokens++;
                    }
                    if (tokens == 0) continue;
                    var chunk = new KnowledgeChunk { RelativePath = rel, Text = chunkText.Trim(), TermFrequency = tf, TokenCount = tokens };
                    index.Chunks.Add(chunk);
                    foreach (var term in tf.Keys)
                        index.DocumentFrequency[term] = index.DocumentFrequency.TryGetValue(term, out var d) ? d + 1 : 1;
                }
            }
            return index;
        }

        private static string Relative(string root, string file)
        {
            try { return Path.GetRelativePath(root, file).Replace('\\', '/'); }
            catch { return Path.GetFileName(file); }
        }

        // Split on line boundaries into ~ChunkChars fragments so a chunk never cuts mid-line.
        private static IEnumerable<string> SplitChunks(string text)
        {
            var sb = new StringBuilder();
            foreach (var line in text.Split('\n'))
            {
                if (sb.Length + line.Length + 1 > ChunkChars && sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                sb.Append(line).Append('\n');
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        // Lowercase alphanumeric tokens, dropping short tokens and stopwords. Identifier runs are also
        // split on camelCase / digit boundaries (ChargeCreditCard → charge, credit, card) so a natural-
        // language query matches code identifiers. Public+static for testing.
        public static IEnumerable<string> Tokenize(string? text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            var sb = new StringBuilder();
            foreach (char ch in text)
            {
                if (char.IsLetterOrDigit(ch)) { sb.Append(ch); continue; }
                if (sb.Length > 0) { foreach (var t in EmitRun(sb.ToString())) yield return t; sb.Clear(); }
            }
            if (sb.Length > 0) foreach (var t in EmitRun(sb.ToString())) yield return t;
        }

        // Split one alnum run on camelCase / letter-digit boundaries and emit each accepted sub-token.
        private static IEnumerable<string> EmitRun(string run)
        {
            var seg = new StringBuilder();
            for (int i = 0; i < run.Length; i++)
            {
                char c = run[i];
                if (i > 0 && seg.Length > 0)
                {
                    char prev = run[i - 1];
                    bool boundary = (char.IsUpper(c) && !char.IsUpper(prev))          // aB
                                 || (char.IsDigit(c) != char.IsDigit(prev));          // a1 / 1a
                    if (boundary)
                    {
                        foreach (var t in Accept(seg.ToString())) yield return t;
                        seg.Clear();
                    }
                }
                seg.Append(char.ToLowerInvariant(c));
            }
            if (seg.Length > 0) foreach (var t in Accept(seg.ToString())) yield return t;
        }

        private static IEnumerable<string> Accept(string tok)
        {
            if (tok.Length >= MinTokenLen && !StopWords.Contains(tok)) yield return tok;
        }

        public IReadOnlyList<KnowledgeSnippet> Retrieve(KnowledgeIndex index, string? query, int maxSnippets)
        {
            var empty = Array.Empty<KnowledgeSnippet>();
            if (index == null || index.IsEmpty || maxSnippets <= 0) return empty;

            var queryTerms = new HashSet<string>(Tokenize(query));
            if (queryTerms.Count == 0) return empty;

            int docCount = index.DocumentCount;
            var scored = new List<KnowledgeSnippet>();
            foreach (var chunk in index.Chunks)
            {
                double score = 0;
                foreach (var t in queryTerms)
                {
                    if (!chunk.TermFrequency.TryGetValue(t, out var tf) || tf == 0) continue;
                    int df = index.DocumentFrequency.TryGetValue(t, out var d) ? d : 1;
                    double idf = Math.Log(1.0 + (double)docCount / df);
                    double tfNorm = (double)tf / chunk.TokenCount;
                    score += tfNorm * idf;
                }
                if (score > 0)
                    scored.Add(new KnowledgeSnippet { RelativePath = chunk.RelativePath, Text = chunk.Text, Score = score });
            }

            // Highest score first; prefer file diversity so top results span the codebase, then backfill.
            var ordered = scored.OrderByDescending(s => s.Score).ToList();
            var picked = new List<KnowledgeSnippet>();
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in ordered)
            {
                if (picked.Count >= maxSnippets) break;
                if (seenFiles.Add(s.RelativePath)) picked.Add(s);
            }
            if (picked.Count < maxSnippets)
                foreach (var s in ordered)
                {
                    if (picked.Count >= maxSnippets) break;
                    if (!picked.Contains(s)) picked.Add(s);
                }
            return picked;
        }

        public string? BuildKnowledgeBlock(IReadOnlyList<KnowledgeSnippet> snippets, int maxChars)
        {
            if (snippets == null || snippets.Count == 0 || maxChars <= 0) return null;
            var sb = new StringBuilder();
            sb.AppendLine("[Knowledge — relevant excerpts from this project]");
            foreach (var s in snippets)
            {
                string header = $"\n// {s.RelativePath}\n";
                string body = s.Text;
                // Leave room for the fences/header; trim the body if a single snippet is huge.
                int room = maxChars - sb.Length - header.Length - 8;
                if (room <= 0) break;
                if (body.Length > room) body = body.Substring(0, room) + "\n…";
                sb.Append(header).Append("```\n").Append(body).Append("\n```\n");
                if (sb.Length >= maxChars) break;
            }
            return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
        }
    }
}
