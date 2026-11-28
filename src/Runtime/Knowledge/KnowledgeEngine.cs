using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LlmodelBox.Runtime.Knowledge
{
    // Part 5 Knowledge Engine:
    // - Incremental indexing driven by per-file SHA-256 content hashes + cached index persistence.
    // - Configurable exclusions + .gitignore / .llmodelboxignore glob filtering.
    // - Overlapping, line-range-tracked code chunks to preserve context across chunk boundaries.
    // - Deterministic BM25 retrieval + optional embedding-based hybrid fusion (Reciprocal Rank Fusion).
    public sealed class KnowledgeEngine : IKnowledgeEngine
    {
        private const int MinTokenLen = 3;

        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".xaml", ".txt", ".md", ".json", ".xml", ".yml", ".yaml", ".ini", ".cfg", ".config",
            ".js", ".ts", ".jsx", ".tsx", ".css", ".html", ".htm", ".py", ".java", ".kt", ".go", ".rs",
            ".c", ".cpp", ".h", ".hpp", ".sql", ".sh", ".ps1", ".bat", ".gradle", ".properties", ".toml", ".csv"
        };

        private static readonly HashSet<string> DefaultSkipDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "dist", "build", "target",
            ".gradle", "app", "publish", "out", ".next", "__pycache__"
        };

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "are", "but", "not", "you", "all", "any", "can", "had", "was", "our",
            "out", "get", "has", "how", "new", "now", "see", "two", "way", "use", "this", "that", "with",
            "from", "have", "will", "your", "they", "what", "when", "which", "were", "been", "into",
            "then", "them", "than", "some", "public", "private", "static", "void", "using", "namespace"
        };

        private readonly IKnowledgeIndexStore? _indexStore;
        private readonly IKnowledgeEmbeddingProvider? _defaultEmbeddingProvider;
        private readonly KnowledgeIndexOptions _defaultOptions;
        private readonly object _gate = new();
        private KnowledgeIndex? _cache;

        public KnowledgeEngine()
            : this(new JsonKnowledgeIndexStore(), null, null)
        {
        }

        public KnowledgeEngine(
            IKnowledgeIndexStore? indexStore,
            IKnowledgeEmbeddingProvider? defaultEmbeddingProvider = null,
            KnowledgeIndexOptions? defaultOptions = null)
        {
            _indexStore = indexStore;
            _defaultEmbeddingProvider = defaultEmbeddingProvider;
            _defaultOptions = defaultOptions ?? new KnowledgeIndexOptions();
        }

        public KnowledgeIndex Index(string? projectRoot) => Index(projectRoot, null);

        public KnowledgeIndex Index(string? projectRoot, KnowledgeIndexOptions? options)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
                return new KnowledgeIndex();

            var opts = options ?? _defaultOptions;
            var sw = Stopwatch.StartNew();
            var exclusions = LoadExclusionMatchers(projectRoot!, opts);
            var files = EnumerateCandidates(projectRoot!, opts, exclusions).ToList();
            long sig = ComputeSignature(files);
            string optHash = opts.ComputeOptionsHash();

            lock (_gate)
            {
                if (_cache != null &&
                    string.Equals(_cache.Root, projectRoot, StringComparison.OrdinalIgnoreCase) &&
                    _cache.Signature == sig &&
                    _cache.OptionsHash == optHash)
                {
                    _cache.LastStats = new KnowledgeIndexStats
                    {
                        TotalFiles = _cache.FileManifest.Count,
                        ReusedFiles = _cache.FileManifest.Count,
                        CacheHitExact = true,
                        DurationMs = sw.ElapsedMilliseconds
                    };
                    return _cache;
                }
            }

            KnowledgeIndex? previous = _cache != null && string.Equals(_cache.Root, projectRoot, StringComparison.OrdinalIgnoreCase)
                ? _cache
                : (opts.PersistToDisk ? _indexStore?.Load(projectRoot!) : null);

            if (previous != null && previous.OptionsHash != optHash)
                previous = null;

            var built = BuildIncremental(projectRoot!, files, sig, optHash, opts, previous, sw);

            lock (_gate) { _cache = built; }
            if (opts.PersistToDisk && _indexStore != null)
                _indexStore.Save(built);

            return built;
        }

        private static KnowledgeIndex BuildIncremental(
            string root,
            IReadOnlyList<string> files,
            long signature,
            string optionsHash,
            KnowledgeIndexOptions opts,
            KnowledgeIndex? previous,
            Stopwatch sw)
        {
            var next = new KnowledgeIndex
            {
                Root = root,
                BuiltUtc = DateTime.UtcNow,
                Signature = signature,
                OptionsHash = optionsHash
            };

            // Group previous chunks by relative path so unchanged content hashes keep existing chunks & embeddings.
            var prevChunksByFile = previous?.Chunks
                .GroupBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, List<KnowledgeChunk>>(StringComparer.OrdinalIgnoreCase);

            int reused = 0, reindexed = 0;

            foreach (var file in files)
            {
                if (next.Chunks.Count >= opts.MaxChunks) break;
                string rel = Relative(root, file);
                KnowledgeFileManifestEntry? prevEntry = null;
                previous?.FileManifest.TryGetValue(rel, out prevEntry);

                FileInfo fi;
                try { fi = new FileInfo(file); } catch { continue; }

                string? content = null;
                string contentHash;

                // Fast check if timestamps match prior disk entry, otherwise compute real SHA-256 content hash.
                if (prevEntry != null &&
                    prevEntry.LastWriteUtcTicks == fi.LastWriteTimeUtc.Ticks &&
                    prevEntry.Length == fi.Length &&
                    prevChunksByFile.TryGetValue(rel, out var existingFast))
                {
                    next.FileManifest[rel] = prevEntry;
                    AppendChunks(next, existingFast, opts.MaxChunks);
                    reused++;
                    continue;
                }

                try { content = File.ReadAllText(file); } catch { continue; }
                if (string.IsNullOrWhiteSpace(content)) continue;
                contentHash = ComputeSha256Hex(content);

                if (prevEntry != null &&
                    string.Equals(prevEntry.ContentSha256, contentHash, StringComparison.OrdinalIgnoreCase) &&
                    prevChunksByFile.TryGetValue(rel, out var unchangedChunks))
                {
                    // File mtime touched without content change: reuse existing chunks + cached embeddings!
                    next.FileManifest[rel] = new KnowledgeFileManifestEntry
                    {
                        RelativePath = rel,
                        ContentSha256 = contentHash,
                        LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                        Length = fi.Length
                    };
                    AppendChunks(next, unchangedChunks, opts.MaxChunks);
                    reused++;
                    continue;
                }

                reindexed++;
                next.FileManifest[rel] = new KnowledgeFileManifestEntry
                {
                    RelativePath = rel,
                    ContentSha256 = contentHash,
                    LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                    Length = fi.Length
                };

                // Carry forward chunk-level cached embeddings when identical chunk hash existed previously.
                var prevEmbeddingMap = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
                if (prevChunksByFile.TryGetValue(rel, out var oldList))
                {
                    foreach (var oldC in oldList)
                        if (oldC.Embedding != null && !string.IsNullOrEmpty(oldC.ChunkSha256))
                            prevEmbeddingMap[oldC.ChunkSha256] = oldC.Embedding;
                }

                foreach (var slice in SplitOverlappingChunks(content, opts.ChunkMaxChars, opts.ChunkOverlapLines))
                {
                    if (next.Chunks.Count >= opts.MaxChunks) break;
                    var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    int tokens = 0;
                    foreach (var term in Tokenize(slice.Text))
                    {
                        tf[term] = tf.TryGetValue(term, out var c) ? c + 1 : 1;
                        tokens++;
                    }
                    if (tokens == 0) continue;

                    string chunkHash = ComputeSha256Hex(slice.Text);
                    prevEmbeddingMap.TryGetValue(chunkHash, out var cachedEmb);

                    var chunk = new KnowledgeChunk
                    {
                        RelativePath = rel,
                        Text = slice.Text.Trim(),
                        StartLine = slice.StartLine,
                        EndLine = slice.EndLine,
                        ChunkSha256 = chunkHash,
                        FileSha256 = contentHash,
                        TermFrequency = tf,
                        TokenCount = tokens,
                        Embedding = cachedEmb
                    };
                    next.Chunks.Add(chunk);
                    foreach (var term in tf.Keys)
                        next.DocumentFrequency[term] = next.DocumentFrequency.TryGetValue(term, out var d) ? d + 1 : 1;
                }
            }

            int removed = previous == null
                ? 0
                : previous.FileManifest.Keys.Count(k => !next.FileManifest.ContainsKey(k));

            sw.Stop();
            next.LastStats = new KnowledgeIndexStats
            {
                TotalFiles = next.FileManifest.Count,
                ReusedFiles = reused,
                ReindexedFiles = reindexed,
                RemovedFiles = removed,
                DurationMs = sw.ElapsedMilliseconds,
                CacheHitExact = false
            };
            return next;
        }

        private static void AppendChunks(KnowledgeIndex target, List<KnowledgeChunk> chunks, int maxChunks)
        {
            foreach (var c in chunks)
            {
                if (target.Chunks.Count >= maxChunks) break;
                target.Chunks.Add(c);
                foreach (var term in c.TermFrequency.Keys)
                    target.DocumentFrequency[term] = target.DocumentFrequency.TryGetValue(term, out var d) ? d + 1 : 1;
            }
        }

        private static IEnumerable<string> EnumerateCandidates(
            string root,
            KnowledgeIndexOptions opts,
            IReadOnlyList<Regex> excludeMatchers)
        {
            var skipDirs = new HashSet<string>(DefaultSkipDirs, StringComparer.OrdinalIgnoreCase);
            foreach (var d in opts.ExtraExcludedDirectories)
                if (!string.IsNullOrWhiteSpace(d)) skipDirs.Add(d.Trim());

            var pending = new Stack<string>();
            pending.Push(root);
            int yielded = 0;

            while (pending.Count > 0 && yielded < opts.MaxFiles)
            {
                string dir = pending.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(dir); }
                catch { continue; }
                Array.Sort(entries, StringComparer.OrdinalIgnoreCase);

                foreach (var path in entries)
                {
                    if (yielded >= opts.MaxFiles) break;
                    try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue; }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }

                    string rel = Relative(root, path);

                    if (Directory.Exists(path))
                    {
                        var name = Path.GetFileName(path);
                        if (skipDirs.Contains(name) || IsExcluded(rel + "/", excludeMatchers))
                            continue;
                        pending.Push(path);
                        continue;
                    }

                    if (!TextExtensions.Contains(Path.GetExtension(path))) continue;
                    if (IsExcluded(rel, excludeMatchers)) continue;

                    long len;
                    try { len = new FileInfo(path).Length; } catch { continue; }
                    if (len <= 0 || len > opts.MaxFileBytes) continue;

                    yielded++;
                    yield return path;
                }
            }
        }

        private static List<Regex> LoadExclusionMatchers(string root, KnowledgeIndexOptions opts)
        {
            var patterns = new List<string>(opts.ExcludedGlobs);
            if (opts.RespectIgnoreFiles)
            {
                foreach (var ignoreName in new[] { ".llmodelboxignore", ".gitignore" })
                {
                    string path = Path.Combine(root, ignoreName);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        foreach (var raw in File.ReadAllLines(path))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;
                            patterns.Add(line);
                        }
                    }
                    catch { }
                }
            }

            var matchers = new List<Regex>();
            foreach (var pat in patterns)
            {
                var rx = GlobToRegex(pat);
                if (rx != null) matchers.Add(rx);
            }
            return matchers;
        }

        private static Regex? GlobToRegex(string rawPattern)
        {
            string p = rawPattern.Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(p)) return null;

            bool rooted = p.StartsWith('/');
            p = p.TrimStart('/');
            bool dirOnly = p.EndsWith('/');
            if (dirOnly) p = p.TrimEnd('/');

            string escaped = Regex.Escape(p)
                .Replace(@"\*\*/", "(.*/)?")
                .Replace(@"\*\*", ".*")
                .Replace(@"\*", "[^/]*")
                .Replace(@"\?", "[^/]");

            string expr = rooted
                ? (dirOnly ? $"^{escaped}(/.*)?$" : $"^{escaped}$")
                : (dirOnly ? $"(^|.*/){escaped}(/.*)?$" : $"(^|.*/){escaped}(/.*)?$");

            try { return new Regex(expr, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
            catch { return null; }
        }

        private static bool IsExcluded(string relUnixPath, IReadOnlyList<Regex> matchers)
        {
            foreach (var rx in matchers)
                if (rx.IsMatch(relUnixPath)) return true;
            return false;
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
                catch { }
                count++;
            }
            return unchecked(sig * 31 + count);
        }

        internal readonly record struct ChunkSlice(string Text, int StartLine, int EndLine);

        internal static IEnumerable<ChunkSlice> SplitOverlappingChunks(string text, int maxChars, int overlapLines)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0) yield break;

            int idx = 0;
            while (idx < lines.Length)
            {
                var sb = new StringBuilder();
                int start = idx;
                int end = idx;

                while (end < lines.Length)
                {
                    int addLen = lines[end].Length + 1;
                    if (sb.Length > 0 && sb.Length + addLen > maxChars) break;
                    sb.Append(lines[end]).Append('\n');
                    end++;
                }

                string chunkText = sb.ToString();
                if (!string.IsNullOrWhiteSpace(chunkText))
                    yield return new ChunkSlice(chunkText, start + 1, Math.Max(start + 1, end));

                if (end >= lines.Length) break;

                int stepBack = Math.Min(Math.Max(0, overlapLines), Math.Max(0, (end - start) - 1));
                int nextIdx = end - stepBack;
                idx = nextIdx <= start ? end : nextIdx;
            }
        }

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

        private static IEnumerable<string> EmitRun(string run)
        {
            var seg = new StringBuilder();
            for (int i = 0; i < run.Length; i++)
            {
                char c = run[i];
                if (i > 0 && seg.Length > 0)
                {
                    char prev = run[i - 1];
                    bool boundary = (char.IsUpper(c) && !char.IsUpper(prev))
                                 || (char.IsDigit(c) != char.IsDigit(prev));
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
            => RetrieveDeterministic(index, query, maxSnippets, KnowledgeRetrievalMode.Deterministic);

        public async Task<IReadOnlyList<KnowledgeSnippet>> RetrieveAsync(
            KnowledgeIndex index,
            string? query,
            int maxSnippets,
            IKnowledgeEmbeddingProvider? embeddingProvider = null,
            CancellationToken cancellationToken = default)
        {
            var empty = Array.Empty<KnowledgeSnippet>();
            if (index == null || index.IsEmpty || maxSnippets <= 0 || string.IsNullOrWhiteSpace(query))
                return empty;

            var bm25Hits = ScoreChunksBm25(index, query!);
            var activeProvider = embeddingProvider ?? _defaultEmbeddingProvider;

            if (activeProvider == null || !activeProvider.IsConfigured)
                return SelectDiverseTop(bm25Hits, maxSnippets, KnowledgeRetrievalMode.DeterministicFallback);

            try
            {
                // Populate missing chunk embeddings in small batches (cached cleanly on index chunks).
                var missing = index.Chunks.Where(c => c.Embedding == null).Take(96).ToList();
                if (missing.Count > 0)
                {
                    var inputs = missing.Select(c => $"{c.RelativePath}\n{c.Text}").ToList();
                    var vectors = await activeProvider.EmbedBatchAsync(inputs, cancellationToken).ConfigureAwait(false);
                    for (int i = 0; i < Math.Min(missing.Count, vectors.Count); i++)
                    {
                        if (vectors[i] != null && vectors[i]!.Length > 0)
                            missing[i].Embedding = vectors[i];
                    }
                }

                var qResult = await activeProvider.EmbedBatchAsync(new[] { query! }, cancellationToken).ConfigureAwait(false);
                var qVec = qResult.Count > 0 ? qResult[0] : null;
                if (qVec == null || qVec.Length == 0)
                    return SelectDiverseTop(bm25Hits, maxSnippets, KnowledgeRetrievalMode.DeterministicFallback);

                var denseScored = new List<(KnowledgeChunk Chunk, double Score)>();
                foreach (var c in index.Chunks)
                {
                    if (c.Embedding == null || c.Embedding.Length != qVec.Length) continue;
                    double cos = CosineSimilarity(qVec, c.Embedding);
                    if (cos > 0.05) denseScored.Add((c, cos));
                }

                if (denseScored.Count == 0)
                    return SelectDiverseTop(bm25Hits, maxSnippets, KnowledgeRetrievalMode.DeterministicFallback);

                // Reciprocal Rank Fusion (RRF) combining semantic dense vectors + exact BM25 lexical hits.
                var rrf = new Dictionary<KnowledgeChunk, double>();
                const double k = 60.0;
                int rank = 0;
                foreach (var (chunk, _) in bm25Hits.OrderByDescending(x => x.Score))
                    rrf[chunk] = rrf.TryGetValue(chunk, out var prev) ? prev + 1.0 / (k + ++rank) : 1.0 / (k + ++rank);

                rank = 0;
                foreach (var (chunk, _) in denseScored.OrderByDescending(x => x.Score))
                    rrf[chunk] = rrf.TryGetValue(chunk, out var prev) ? prev + 1.0 / (k + ++rank) : 1.0 / (k + ++rank);

                var fused = rrf.Select(kv => (kv.Key, kv.Value)).ToList();
                return SelectDiverseTop(fused, maxSnippets, KnowledgeRetrievalMode.Hybrid);
            }
            catch
            {
                return SelectDiverseTop(bm25Hits, maxSnippets, KnowledgeRetrievalMode.DeterministicFallback);
            }
        }

        private static IReadOnlyList<KnowledgeSnippet> RetrieveDeterministic(
            KnowledgeIndex index,
            string? query,
            int maxSnippets,
            KnowledgeRetrievalMode mode)
        {
            if (index == null || index.IsEmpty || maxSnippets <= 0 || string.IsNullOrWhiteSpace(query))
                return Array.Empty<KnowledgeSnippet>();

            var bm25Hits = ScoreChunksBm25(index, query!);
            return SelectDiverseTop(bm25Hits, maxSnippets, mode);
        }

        private static List<(KnowledgeChunk Chunk, double Score)> ScoreChunksBm25(KnowledgeIndex index, string query)
        {
            var queryTerms = new HashSet<string>(Tokenize(query), StringComparer.OrdinalIgnoreCase);
            var scored = new List<(KnowledgeChunk, double)>();
            if (queryTerms.Count == 0) return scored;

            int docCount = Math.Max(1, index.DocumentCount);
            double avgLen = index.AverageChunkTokens;
            const double k1 = 1.5;
            const double b = 0.75;

            foreach (var chunk in index.Chunks)
            {
                double score = 0;
                foreach (var t in queryTerms)
                {
                    if (!chunk.TermFrequency.TryGetValue(t, out var tf) || tf == 0) continue;
                    int df = index.DocumentFrequency.TryGetValue(t, out var d) ? d : 1;
                    double idf = Math.Log(1.0 + (docCount - df + 0.5) / (df + 0.5));
                    if (idf <= 0) idf = Math.Log(1.0 + (double)docCount / df);
                    double normTf = (tf * (k1 + 1.0)) / (tf + k1 * (1.0 - b + b * (chunk.TokenCount / avgLen)));
                    score += normTf * idf;
                }

                // Bonus when filename itself matches query terms.
                foreach (var pathTok in Tokenize(chunk.RelativePath))
                    if (queryTerms.Contains(pathTok)) score += 0.45;

                if (score > 0) scored.Add((chunk, score));
            }
            return scored;
        }

        private static IReadOnlyList<KnowledgeSnippet> SelectDiverseTop(
            List<(KnowledgeChunk Chunk, double Score)> scored,
            int maxSnippets,
            KnowledgeRetrievalMode mode)
        {
            var ordered = scored
                .OrderByDescending(x => x.Score)
                .Select(x => new KnowledgeSnippet
                {
                    RelativePath = x.Chunk.RelativePath,
                    Text = x.Chunk.Text,
                    Score = x.Score,
                    StartLine = x.Chunk.StartLine,
                    EndLine = x.Chunk.EndLine,
                    ChunkSha256 = x.Chunk.ChunkSha256,
                    RetrievalMode = mode
                })
                .ToList();

            var picked = new List<KnowledgeSnippet>();
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in ordered)
            {
                if (picked.Count >= maxSnippets) break;
                if (seenFiles.Add(s.RelativePath)) picked.Add(s);
            }
            if (picked.Count < maxSnippets)
            {
                foreach (var s in ordered)
                {
                    if (picked.Count >= maxSnippets) break;
                    if (!picked.Contains(s)) picked.Add(s);
                }
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
                string span = s.EndLine > s.StartLine ? $":{s.StartLine}-{s.EndLine}" : $":{s.StartLine}";
                string header = $"\n// {s.RelativePath}{span}\n";
                string body = s.Text;
                int room = maxChars - sb.Length - header.Length - 8;
                if (room <= 0) break;
                if (body.Length > room) body = body.Substring(0, room) + "\n…";
                sb.Append(header).Append("```\n").Append(body).Append("\n```\n");
                if (sb.Length >= maxChars) break;
            }
            return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
        }

        private static string Relative(string root, string file)
        {
            try { return Path.GetRelativePath(root, file).Replace('\\', '/'); }
            catch { return Path.GetFileName(file); }
        }

        private static string ComputeSha256Hex(string content)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static double CosineSimilarity(float[] a, float[] b)
        {
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            double denom = Math.Sqrt(na) * Math.Sqrt(nb);
            return denom > 0 ? dot / denom : 0.0;
        }
    }
}
