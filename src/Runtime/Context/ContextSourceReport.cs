using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LlmodelBox.Runtime.Knowledge;

namespace LlmodelBox.Runtime.Context
{
    public enum ContextPriorityTier
    {
        CriticalPromptAndSystem = 0,
        PinnedMemoryAndSummary = 1,
        ScoredFiles = 2,
        KnowledgeRetrieval = 3,
        RecentHistory = 4,
        OutputReserve = 5
    }

    public sealed class CandidateFileContext
    {
        public string Path { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public bool IsActiveOpenFile { get; set; }
        public bool IsDirty { get; set; }
        public int OpenRecencyRank { get; set; } // 0 = most recent
    }

    public sealed class ScoredFileResult
    {
        public string Path { get; init; } = string.Empty;
        public double Score { get; init; }
        public string Reason { get; init; } = string.Empty;
        public bool Included { get; init; }
    }

    public sealed class ContextSourceEntry
    {
        public string Category { get; init; } = string.Empty;
        public string SourceName { get; init; } = string.Empty;
        public ContextPriorityTier Tier { get; init; }
        public int TokensAllocated { get; init; }
        public double RelevanceScore { get; init; }
        public bool Included { get; init; }
        public bool Truncated { get; init; }
        public bool Deduplicated { get; init; }
        public string Reason { get; init; } = string.Empty;
    }

    public static class FileRelevanceScorer
    {
        public static List<(CandidateFileContext File, double Score, string Reason)> RankCandidates(
            IEnumerable<CandidateFileContext>? candidates,
            string? userPrompt)
        {
            if (candidates == null) return new List<(CandidateFileContext, double, string)>();

            var promptTerms = new HashSet<string>(KnowledgeEngine.Tokenize(userPrompt), StringComparer.OrdinalIgnoreCase);
            string promptLower = (userPrompt ?? string.Empty).ToLowerInvariant();
            var scored = new List<(CandidateFileContext File, double Score, string Reason)>();

            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c.Path) || string.IsNullOrWhiteSpace(c.Content)) continue;

                double score = 0;
                var reasons = new List<string>();
                string fileName = Path.GetFileName(c.Path);
                string fileNameLower = fileName.ToLowerInvariant();

                if (c.IsActiveOpenFile)
                {
                    score += 10.0;
                    reasons.Add("active_editor");
                }
                if (c.IsDirty)
                {
                    score += 2.5;
                    reasons.Add("unsaved_changes");
                }
                if (!string.IsNullOrEmpty(fileNameLower) && promptLower.Contains(fileNameLower))
                {
                    score += 8.0;
                    reasons.Add("mentioned_in_prompt");
                }

                int pathOverlap = KnowledgeEngine.Tokenize(c.Path).Count(t => promptTerms.Contains(t));
                if (pathOverlap > 0)
                {
                    score += Math.Min(4.0, pathOverlap * 1.5);
                    reasons.Add("path_overlap");
                }

                if (promptTerms.Count > 0)
                {
                    string sample = c.Content.Length > 4000 ? c.Content[..4000] : c.Content;
                    int contentOverlap = KnowledgeEngine.Tokenize(sample).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(t => promptTerms.Contains(t));
                    if (contentOverlap > 0)
                    {
                        score += Math.Min(5.0, contentOverlap * 0.75);
                        reasons.Add("symbol_overlap");
                    }
                }

                score += Math.Max(0, 1.5 - c.OpenRecencyRank * 0.25);
                if (reasons.Count == 0) reasons.Add("open_workspace_tab");

                scored.Add((c, score, string.Join(", ", reasons)));
            }

            return scored.OrderByDescending(x => x.Score).ToList();
        }
    }

    public static class ContextDeduplicator
    {
        public static (string? DeduplicatedBlock, int RemovedCount) DeduplicateKnowledgeAgainstOpenFile(
            string? knowledgeBlock,
            string? openFilePath,
            string? openFileText,
            string? memoryPreamble)
        {
            if (string.IsNullOrWhiteSpace(knowledgeBlock))
                return (null, 0);

            string openFileName = string.IsNullOrWhiteSpace(openFilePath) ? string.Empty : Path.GetFileName(openFilePath);
            var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(memoryPreamble))
                AddLineWindowHashes(memoryPreamble!, seenHashes);

            var sections = knowledgeBlock!.Split(new[] { "\n// " }, StringSplitOptions.None);
            if (sections.Length <= 1)
            {
                // Single or non-delimited block: check whole content hash against open file
                if (!string.IsNullOrWhiteSpace(openFileText) && openFileText!.Length < 200000 &&
                    !string.IsNullOrWhiteSpace(openFileName) &&
                    knowledgeBlock.Contains(openFileName, StringComparison.OrdinalIgnoreCase) &&
                    SharesSubstantialBody(knowledgeBlock, openFileText))
                {
                    return (null, 1);
                }
                return (knowledgeBlock, 0);
            }

            var kept = new StringBuilder();
            kept.Append(sections[0].TrimEnd());
            int removed = 0;

            for (int i = 1; i < sections.Length; i++)
            {
                string sec = sections[i];
                int nl = sec.IndexOf('\n');
                string headerPath = nl >= 0 ? sec[..nl].Trim() : sec.Trim();
                string body = nl >= 0 ? sec[(nl + 1)..] : string.Empty;
                string strippedBody = StripCodeFences(body);

                bool isSameAsOpenFile = !string.IsNullOrWhiteSpace(openFileName) &&
                                        headerPath.Contains(openFileName, StringComparison.OrdinalIgnoreCase) &&
                                        !string.IsNullOrWhiteSpace(openFileText) &&
                                        openFileText!.Contains(strippedBody.Trim(), StringComparison.Ordinal);

                string blockHash = HashNormalized(strippedBody);
                if (isSameAsOpenFile || !seenHashes.Add(blockHash))
                {
                    removed++;
                    continue;
                }

                kept.Append("\n// ").Append(sec.TrimEnd());
            }

            string finalText = kept.ToString().Trim();
            if (removed == sections.Length - 1) return (null, removed);
            return (finalText, removed);
        }

        public static List<string> DeduplicateLines(IEnumerable<string>? items)
        {
            var result = new List<string>();
            if (items == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in items)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string key = HashNormalized(raw);
                if (seen.Add(key)) result.Add(raw);
            }
            return result;
        }

        private static bool SharesSubstantialBody(string candidateBlock, string referenceText)
        {
            string clean = StripCodeFences(candidateBlock);
            var lines = clean.Split('\n').Select(l => l.Trim()).Where(l => l.Length >= 12).Take(4).ToList();
            if (lines.Count == 0) return false;
            return lines.All(l => referenceText.Contains(l, StringComparison.Ordinal));
        }

        private static string StripCodeFences(string text)
        {
            var lines = text.Split('\n').Where(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal));
            return string.Join('\n', lines);
        }

        private static void AddLineWindowHashes(string text, HashSet<string> hashes)
        {
            foreach (var line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length >= 20) hashes.Add(HashNormalized(trimmed));
            }
        }

        private static string HashNormalized(string s)
        {
            string norm = s.Trim().Replace("\r\n", "\n").ToLowerInvariant();
            byte[] b = SHA256.HashData(Encoding.UTF8.GetBytes(norm));
            return Convert.ToHexString(b)[..16];
        }
    }
}
