using System;
using System.Collections.Generic;
using System.Linq;
using LlmodelBox.Common;
using LlmodelBox.Models;
using LlmodelBox.Runtime.Tools;

namespace LlmodelBox.Infrastructure.RightPanel
{
    public enum OmniboxResultKind
    {
        Url,
        RecentFile,
        File,
        RecentCommand,
        Command,
        Tool,
        PluginCommand
    }

    public sealed class OmniboxSearchItem
    {
        public OmniboxResultKind Kind { get; init; }
        public string Category => Kind switch
        {
            OmniboxResultKind.Url => "URL",
            OmniboxResultKind.RecentFile => "RECENT FILE",
            OmniboxResultKind.File => "FILE",
            OmniboxResultKind.RecentCommand => "RECENT CMD",
            OmniboxResultKind.Command => "COMMAND",
            OmniboxResultKind.Tool => "AI TOOL",
            OmniboxResultKind.PluginCommand => "PLUGIN",
            _ => "ITEM"
        };
        public string Title { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
        public string TargetKey { get; init; } = string.Empty;
        public int Score { get; init; }
    }

    public sealed class OmniboxService
    {
        private readonly ToolRegistry? _toolRegistry;
        private readonly PluginManagerService? _plugins;

        public OmniboxService(ToolRegistry? toolRegistry = null, PluginManagerService? plugins = null)
        {
            _toolRegistry = toolRegistry;
            _plugins = plugins;
        }

        public List<OmniboxSearchItem> Search(
            string query,
            IEnumerable<string> workspaceFiles,
            IEnumerable<string> recentFiles,
            IEnumerable<(string Id, string Title, string Subtitle)> ideCommands,
            IEnumerable<string> recentCommands)
        {
            string q = (query ?? string.Empty).Trim();
            var results = new List<OmniboxSearchItem>();

            // 1. Direct URL or search navigation
            if (!string.IsNullOrWhiteSpace(q))
            {
                string candidate = q.Contains("://", StringComparison.Ordinal)
                    ? q
                    : (q.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || (q.Contains('.') && !q.Contains(' ')))
                        ? "https://" + q
                        : string.Empty;

                if (!string.IsNullOrEmpty(candidate) && WebLink.TryParse(candidate, out var safeUri) && safeUri != null)
                {
                    results.Add(new OmniboxSearchItem
                    {
                        Kind = OmniboxResultKind.Url,
                        Title = $"Open URL in Browser: {safeUri.Host}",
                        Subtitle = safeUri.AbsoluteUri,
                        TargetKey = safeUri.AbsoluteUri,
                        Score = 1000
                    });
                }
            }

            // 2. Recent files
            if (recentFiles != null)
            {
                foreach (var rf in recentFiles.Take(6))
                {
                    int score = ComputeFuzzyScore(q, rf);
                    if (score > 0 || string.IsNullOrEmpty(q))
                    {
                        results.Add(new OmniboxSearchItem
                        {
                            Kind = OmniboxResultKind.RecentFile,
                            Title = System.IO.Path.GetFileName(rf),
                            Subtitle = rf,
                            TargetKey = rf,
                            Score = score + 220
                        });
                    }
                }
            }

            // 3. Recent commands
            if (recentCommands != null)
            {
                foreach (var rc in recentCommands.Take(5))
                {
                    int score = ComputeFuzzyScore(q, rc);
                    if (score > 0 || string.IsNullOrEmpty(q))
                    {
                        results.Add(new OmniboxSearchItem
                        {
                            Kind = OmniboxResultKind.RecentCommand,
                            Title = rc,
                            Subtitle = "Re-run in Terminal",
                            TargetKey = rc,
                            Score = score + 180
                        });
                    }
                }
            }

            // 4. Built-in IDE Commands
            if (ideCommands != null)
            {
                foreach (var cmd in ideCommands)
                {
                    int score = Math.Max(ComputeFuzzyScore(q, cmd.Title), ComputeFuzzyScore(q, cmd.Subtitle));
                    if (score > 0 || string.IsNullOrEmpty(q))
                    {
                        results.Add(new OmniboxSearchItem
                        {
                            Kind = OmniboxResultKind.Command,
                            Title = cmd.Title,
                            Subtitle = cmd.Subtitle,
                            TargetKey = cmd.Id,
                            Score = score + 150
                        });
                    }
                }
            }

            // 5. Workspace Files (fuzzy filename + relative path)
            if (workspaceFiles != null)
            {
                var matchedFiles = new List<OmniboxSearchItem>();
                foreach (var wf in workspaceFiles)
                {
                    string fileName = System.IO.Path.GetFileName(wf);
                    int s = Math.Max(ComputeFuzzyScore(q, fileName) + 40, ComputeFuzzyScore(q, wf));
                    if (s > 0 || string.IsNullOrEmpty(q))
                    {
                        matchedFiles.Add(new OmniboxSearchItem
                        {
                            Kind = OmniboxResultKind.File,
                            Title = string.IsNullOrEmpty(fileName) ? wf : fileName,
                            Subtitle = wf,
                            TargetKey = wf,
                            Score = s + 120
                        });
                    }
                }
                results.AddRange(matchedFiles.OrderByDescending(x => x.Score).Take(14));
            }

            // 6. Registered AI Tools
            if (_toolRegistry != null)
            {
                foreach (var def in _toolRegistry.Definitions())
                {
                    int score = Math.Max(ComputeFuzzyScore(q, def.Name), ComputeFuzzyScore(q, def.Description));
                    if (score > 0 || string.IsNullOrEmpty(q))
                    {
                        results.Add(new OmniboxSearchItem
                        {
                            Kind = OmniboxResultKind.Tool,
                            Title = $"Tool: {def.Name}",
                            Subtitle = def.Description,
                            TargetKey = def.Name,
                            Score = score + 80
                        });
                    }
                }
            }

            // 7. Active Plugin Commands
            if (_plugins != null)
            {
                foreach (var pcmd in _plugins.GetActiveCommands())
                {
                    int score = Math.Max(ComputeFuzzyScore(q, pcmd.Title), ComputeFuzzyScore(q, pcmd.Description));
                    if (score > 0 || string.IsNullOrEmpty(q))
                    {
                        results.Add(new OmniboxSearchItem
                        {
                            Kind = OmniboxResultKind.PluginCommand,
                            Title = pcmd.Title,
                            Subtitle = pcmd.Description,
                            TargetKey = pcmd.Id,
                            Score = score + 110
                        });
                    }
                }
            }

            return results
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToList();
        }

        public static int ComputeFuzzyScore(string query, string target)
        {
            if (string.IsNullOrWhiteSpace(query)) return 10;
            if (string.IsNullOrWhiteSpace(target)) return 0;

            if (target.Equals(query, StringComparison.OrdinalIgnoreCase)) return 400;
            if (target.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 300;
            if (target.Contains(query, StringComparison.OrdinalIgnoreCase)) return 220;

            int qi = 0;
            int streak = 0;
            int bonus = 0;
            for (int ti = 0; ti < target.Length && qi < query.Length; ti++)
            {
                if (char.ToLowerInvariant(target[ti]) == char.ToLowerInvariant(query[qi]))
                {
                    qi++;
                    streak++;
                    bonus += streak * 8;
                }
                else
                {
                    streak = 0;
                }
            }
            return qi == query.Length ? Math.Max(20, 120 + bonus - (target.Length / 4)) : 0;
        }
    }
}
