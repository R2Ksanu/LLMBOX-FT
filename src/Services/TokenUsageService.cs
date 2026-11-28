using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    // Records per-call token usage to a local JSON file and produces aggregates
    // for the Token Usage dashboard. All access is locked because usage events
    // are raised from API-call continuations that may resume on a threadpool thread.
    public class TokenUsageService : ITokenUsageService
    {
        private const int MaxRecords = 5000;
        private readonly string _filePath;
        private readonly object _gate = new();
        private readonly List<TokenUsageRecord> _records = new();

        public TokenUsageService(string? filePath = null)
        {
            _filePath = filePath ?? Path.Combine(LlmodelBox.Common.AppConstants.DataDirectory, "token_usage.json");
            Load();
        }

        public event Action<TokenUsageRecord>? UsageRecorded;

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    var loaded = JsonSerializer.Deserialize<List<TokenUsageRecord>>(json);
                    if (loaded != null && loaded.Count > 0)
                    {
                        _records.Clear();
                        _records.AddRange(loaded);
                    }
                }
            }
            catch { /* corrupt/absent file starts empty */ }
        }

        private void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_records, new JsonSerializerOptions { WriteIndented = false });
                LlmodelBox.Common.AtomicFile.WriteAllText(_filePath, json);
            }
            catch { /* best-effort persistence */ }
        }

        public void Record(TokenUsageRecord record)
        {
            if (record == null || record.TotalTokens <= 0) return;
            lock (_gate)
            {
                _records.Add(record);
                if (_records.Count > MaxRecords)
                {
                    _records.RemoveRange(0, _records.Count - MaxRecords);
                }
                Save();
            }
            try
            {
                UsageRecorded?.Invoke(record);
            }
            catch { }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _records.Clear();
                Save();
            }
        }

        public TokenUsageSummary GetSummary()
        {
            lock (_gate)
            {
                var s = new TokenUsageSummary
                {
                    CallCount = _records.Count,
                    TotalTokens = _records.Sum(r => (long)r.TotalTokens),
                    PromptTokens = _records.Sum(r => (long)r.PromptTokens),
                    CompletionTokens = _records.Sum(r => (long)r.CompletionTokens)
                };

                s.ByProvider = _records
                    .GroupBy(r => string.IsNullOrWhiteSpace(r.Provider) ? "Unknown" : r.Provider)
                    .Select(g => new UsageBucket { Label = g.Key, Tokens = g.Sum(r => (long)r.TotalTokens), Calls = g.Count() })
                    .OrderByDescending(b => b.Tokens)
                    .ToList();

                s.TopModels = _records
                    .GroupBy(r => string.IsNullOrWhiteSpace(r.ModelName) ? r.ModelId : r.ModelName)
                    .Select(g => new UsageBucket { Label = g.Key, Tokens = g.Sum(r => (long)r.TotalTokens), Calls = g.Count() })
                    .OrderByDescending(b => b.Tokens)
                    .Take(6)
                    .ToList();

                var today = DateTime.UtcNow.Date;
                var byDay = _records
                    .GroupBy(r => r.TimestampUtc.Date)
                    .ToDictionary(g => g.Key, g => g.Sum(r => (long)r.TotalTokens));
                s.ByDay = Enumerable.Range(0, 14)
                    .Select(i => today.AddDays(-13 + i))
                    .Select(d => new DayBucket { Day = d, Tokens = byDay.TryGetValue(d, out var t) ? t : 0 })
                    .ToList();

                // Per-model timing. Only records that actually captured latency contribute to the
                // latency/throughput averages, so legacy (LatencyMs == 0) rows don't skew them down.
                s.ModelPerf = _records
                    .GroupBy(r => string.IsNullOrWhiteSpace(r.ModelName) ? r.ModelId : r.ModelName)
                    .Select(g =>
                    {
                        var timed = g.Where(r => r.LatencyMs > 0).ToList();
                        return new ModelPerfBucket
                        {
                            Label = g.Key,
                            Calls = g.Count(),
                            Tokens = g.Sum(r => (long)r.TotalTokens),
                            AvgLatencyMs = timed.Count > 0 ? timed.Average(r => r.LatencyMs) : 0.0,
                            AvgTokensPerSecond = timed.Count > 0 ? timed.Average(r => r.TokensPerSecond) : 0.0
                        };
                    })
                    .OrderByDescending(b => b.Calls)
                    .ThenByDescending(b => b.Tokens)
                    .Take(8)
                    .ToList();

                var allTimed = _records.Where(r => r.LatencyMs > 0).ToList();
                s.AvgLatencyMs = allTimed.Count > 0 ? allTimed.Average(r => r.LatencyMs) : 0.0;
                s.AvgTokensPerSecond = allTimed.Count > 0 ? allTimed.Average(r => r.TokensPerSecond) : 0.0;

                // ---- Advanced analytics ----
                s.AvgTokensPerCall = _records.Count > 0 ? (double)s.TotalTokens / _records.Count : 0.0;
                s.PromptShare = s.TotalTokens > 0 ? (double)s.PromptTokens / s.TotalTokens : 0.0;
                s.CompletionShare = s.TotalTokens > 0 ? (double)s.CompletionTokens / s.TotalTokens : 0.0;
                s.LiveCallCount = _records.Count(r => r.IsLiveApi);
                s.EstimatedCallCount = _records.Count(r => !r.IsLiveApi);
                s.TotalCachedTokens = _records.Sum(r => (long)r.CachedTokens);
                s.TotalReasoningTokens = _records.Sum(r => (long)r.ReasoningTokens);
                s.TotalInputCostUsd = _records.Sum(r => r.InputCostUsd ?? 0.0);
                s.TotalOutputCostUsd = _records.Sum(r => r.OutputCostUsd ?? 0.0);
                s.TotalEstimatedCostUsd = _records.Sum(r => r.EstimatedCostUsd ?? 0.0);
                s.PeakTokensPerSecond = allTimed.Count > 0 ? allTimed.Max(r => r.TokensPerSecond) : 0.0;

                var weekAgo = today.AddDays(-6);
                s.TokensToday = _records.Where(r => r.TimestampUtc.Date == today).Sum(r => (long)r.TotalTokens);
                s.Tokens7Days = _records.Where(r => r.TimestampUtc.Date >= weekAgo).Sum(r => (long)r.TotalTokens);

                if (s.ByDay.Count > 0)
                {
                    var peak = s.ByDay.OrderByDescending(d => d.Tokens).First();
                    s.PeakDayTokens = peak.Tokens;
                    s.PeakDay = peak.Tokens > 0 ? peak.Day : (DateTime?)null;
                }
                s.TopModelLabel = s.TopModels.Count > 0 ? s.TopModels[0].Label : string.Empty;

                var byHour = _records
                    .GroupBy(r => r.TimestampUtc.Hour)
                    .ToDictionary(g => g.Key, g => (Tokens: g.Sum(r => (long)r.TotalTokens), Calls: g.Count()));
                s.ByHour = Enumerable.Range(0, 24)
                    .Select(h => new HourBucket
                    {
                        Hour = h,
                        Tokens = byHour.TryGetValue(h, out var v) ? v.Tokens : 0,
                        Calls = byHour.TryGetValue(h, out var v2) ? v2.Calls : 0
                    })
                    .ToList();

                return s;
            }
        }
    }
}
