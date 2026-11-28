using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlmodelBox.Models;
using LlmodelBox.Runtime.Memory;

namespace LlmodelBox.Runtime.Continuity
{
    public sealed class LegacyHomeThreadDto
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public List<ChatMessageItem> Messages { get; set; } = new();
    }

    /// <summary>
    /// Idempotently migrates legacy JSON storage into IConversationRepository:
    ///   1. home_conversations.json (full thread list — never capped at 100)
    ///   2. per-project chat/<hash>.json files
    ///   3. per-project chat/<hash>.mem.json files
    ///   4. global-preferences.mem.json
    /// Originals are backed up before migration; malformed files are quarantined without data wipe.
    /// </summary>
    public sealed class LegacyDataMigrator
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        public MigrationSummaryReport MigrateIfNeeded(
            IConversationRepository repo,
            string legacyChatDir,
            string backupRootDir,
            IEnumerable<ProjectItem>? knownProjects = null)
        {
            var report = new MigrationSummaryReport();
            if (repo == null || string.IsNullOrWhiteSpace(legacyChatDir) || !Directory.Exists(legacyChatDir))
                return report;

            var projectsByHash = new Dictionary<string, ProjectItem>(StringComparer.OrdinalIgnoreCase);
            if (knownProjects != null)
            {
                foreach (var p in knownProjects)
                {
                    if (!string.IsNullOrWhiteSpace(p.Path))
                        projectsByHash[HashKey(p.Path)] = p;
                }
            }

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string snapshotDir = Path.Combine(backupRootDir, $"pre-continuity-migration-{stamp}");
            bool createdBackupDir = false;

            void BackupOnce(string srcPath)
            {
                try
                {
                    if (!File.Exists(srcPath)) return;
                    if (!createdBackupDir)
                    {
                        Directory.CreateDirectory(snapshotDir);
                        report.BackupDirectory = snapshotDir;
                        createdBackupDir = true;
                    }
                    File.Copy(srcPath, Path.Combine(snapshotDir, Path.GetFileName(srcPath)), overwrite: true);
                }
                catch { }
            }

            // 1. Migrate ALL home_conversations.json entries (no Take(100) limit)
            string homeFile = Path.Combine(legacyChatDir, "home_conversations.json");
            const string homeLedgerKey = "legacy:home_conversations.json";
            if (File.Exists(homeFile) && !repo.IsMigrationRecorded(homeLedgerKey))
            {
                BackupOnce(homeFile);
                try
                {
                    string json = File.ReadAllText(homeFile);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var threads = JsonSerializer.Deserialize<List<LegacyHomeThreadDto>>(json, JsonOpts)
                                      ?? throw new InvalidDataException("Null home_conversations.json payload.");

                        foreach (var th in threads)
                        {
                            string cid = string.IsNullOrWhiteSpace(th.Id) ? Guid.NewGuid().ToString("N") : th.Id;
                            if (repo.GetConversation(cid) != null) continue;

                            var conv = new ConversationRecord
                            {
                                Id = cid,
                                Title = string.IsNullOrWhiteSpace(th.Title) ? "Imported Conversation" : th.Title,
                                ProjectKey = th.ProjectPath ?? string.Empty,
                                ProjectName = th.ProjectName ?? string.Empty,
                                Status = ConversationStatus.Active,
                                CreatedAtUtc = th.UpdatedAtUtc == default ? DateTime.UtcNow : th.UpdatedAtUtc.ToUniversalTime(),
                                UpdatedAtUtc = th.UpdatedAtUtc == default ? DateTime.UtcNow : th.UpdatedAtUtc.ToUniversalTime(),
                                LastActivityUtc = th.UpdatedAtUtc == default ? DateTime.UtcNow : th.UpdatedAtUtc.ToUniversalTime(),
                                MessageCount = th.Messages?.Count ?? 0
                            };
                            repo.UpsertConversation(conv);

                            if (th.Messages != null && th.Messages.Count > 0)
                            {
                                var msgRecs = th.Messages
                                    .Select((m, idx) => ConversationMessageRecord.FromChatMessageItem(m, cid, idx + 1))
                                    .ToList();
                                repo.ReplaceMessages(cid, msgRecs);
                            }
                            report.HomeThreadsImported++;
                        }
                    }
                    repo.RecordMigration(homeLedgerKey, "home_conversations");
                    report.Executed = true;
                }
                catch (Exception ex)
                {
                    report.MalformedFilesQuarantined++;
                    report.Warnings.Add($"home_conversations.json malformed ({SecretRedactor.Redact(ex.Message)}); quarantined backup preserved.");
                    CorruptFileQuarantine.PreserveFile(homeFile, backupRootDir);
                }
            }

            // 2. Migrate legacy global-preferences.mem.json
            string globalFile = Path.Combine(legacyChatDir, "global-preferences.mem.json");
            const string globalLedgerKey = "legacy:global-preferences.mem.json";
            if (File.Exists(globalFile) && !repo.IsMigrationRecorded(globalLedgerKey))
            {
                BackupOnce(globalFile);
                try
                {
                    string json = File.ReadAllText(globalFile);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var records = JsonSerializer.Deserialize<List<MemoryRecord>>(json, JsonOpts)
                                      ?? throw new InvalidDataException("Null global-preferences.mem.json payload.");
                        foreach (var r in records)
                        {
                            if (string.IsNullOrWhiteSpace(r.Content)) continue;
                            r.Scope = MemoryScope.UserGlobal;
                            r.Provenance = MemoryProvenance.UserExplicit;
                            repo.UpsertMemory(r);
                            report.GlobalMemoriesImported++;
                        }
                    }
                    repo.RecordMigration(globalLedgerKey, "global_memory");
                    report.Executed = true;
                }
                catch (Exception ex)
                {
                    report.MalformedFilesQuarantined++;
                    report.Warnings.Add($"global-preferences.mem.json malformed ({SecretRedactor.Redact(ex.Message)}); quarantined backup preserved.");
                    CorruptFileQuarantine.PreserveFile(globalFile, backupRootDir);
                }
            }

            // 3. Migrate per-project <hash>.mem.json files
            foreach (var memFile in Directory.EnumerateFiles(legacyChatDir, "*.mem.json"))
            {
                string fname = Path.GetFileName(memFile);
                if (fname.Equals("global-preferences.mem.json", StringComparison.OrdinalIgnoreCase)) continue;
                string ledgerKey = $"legacy:mem:{fname}";
                if (repo.IsMigrationRecorded(ledgerKey)) continue;

                BackupOnce(memFile);
                try
                {
                    string hash = fname.Replace(".mem.json", string.Empty, StringComparison.OrdinalIgnoreCase);
                    projectsByHash.TryGetValue(hash, out var proj);
                    string resolvedProjectKey = proj?.Path ?? $"legacy_hash:{hash}";

                    string json = File.ReadAllText(memFile);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var cm = JsonSerializer.Deserialize<ConversationMemory>(json, JsonOpts)
                                 ?? throw new InvalidDataException($"Null {fname} payload.");
                        cm.SynchronizeRecords(resolvedProjectKey);

                        foreach (var rec in cm.Records)
                        {
                            rec.ProjectKey = string.IsNullOrWhiteSpace(rec.ProjectKey) ? resolvedProjectKey : rec.ProjectKey;
                            rec.Scope = MemoryScope.CurrentProject;
                            repo.UpsertMemory(rec);
                            report.ProjectMemoriesImported++;
                        }

                        if (!string.IsNullOrWhiteSpace(cm.SessionGoal))
                        {
                            repo.UpsertMemory(new MemoryRecord
                            {
                                Id = ConversationMemory.DeterministicId("legacy_goal", hash + cm.SessionGoal),
                                Content = $"Legacy project goal: {cm.SessionGoal}",
                                Scope = MemoryScope.CurrentProject,
                                Category = MemoryCategory.Architecture,
                                Provenance = MemoryProvenance.UserExplicit,
                                Source = "legacy_session_goal",
                                ProjectKey = resolvedProjectKey,
                                Confidence = 0.70
                            });
                            report.ProjectMemoriesImported++;
                        }
                    }

                    repo.RecordMigration(ledgerKey, "project_memory");
                    report.Executed = true;
                }
                catch (Exception ex)
                {
                    report.MalformedFilesQuarantined++;
                    report.Warnings.Add($"{fname} malformed ({SecretRedactor.Redact(ex.Message)}); quarantined backup preserved.");
                    CorruptFileQuarantine.PreserveFile(memFile, backupRootDir);
                }
            }

            // 4. Migrate legacy per-project <hash>.json transcripts (skip home_conversations.json)
            foreach (var jsonFile in Directory.EnumerateFiles(legacyChatDir, "*.json"))
            {
                string fname = Path.GetFileName(jsonFile);
                if (fname.Equals("home_conversations.json", StringComparison.OrdinalIgnoreCase) ||
                    fname.EndsWith(".mem.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                string ledgerKey = $"legacy:chat:{fname}";
                if (repo.IsMigrationRecorded(ledgerKey)) continue;

                BackupOnce(jsonFile);
                try
                {
                    string hash = Path.GetFileNameWithoutExtension(fname);
                    projectsByHash.TryGetValue(hash, out var proj);
                    string resolvedProjectKey = proj?.Path ?? $"legacy_hash:{hash}";
                    string resolvedProjectName = proj?.Name ?? hash;

                    string json = File.ReadAllText(jsonFile);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var msgs = JsonSerializer.Deserialize<List<ChatMessageItem>>(json, JsonOpts)
                                   ?? throw new InvalidDataException($"Null {fname} payload.");

                        if (msgs.Count > 0)
                        {
                            string cid = $"legacy_proj_{hash}";
                            if (repo.GetConversation(cid) == null)
                            {
                                var firstUser = msgs.FirstOrDefault(m => m.IsUser && !string.IsNullOrWhiteSpace(m.Content));
                                string title = firstUser != null
                                    ? DeriveTitle(firstUser.Content)
                                    : $"{resolvedProjectName} Transcript";

                                var latestTime = msgs
                                    .Select(m => m.CreatedAtUtc != default ? m.CreatedAtUtc : DateTime.UtcNow)
                                    .DefaultIfEmpty(DateTime.UtcNow)
                                    .Max();

                                repo.UpsertConversation(new ConversationRecord
                                {
                                    Id = cid,
                                    Title = title,
                                    ProjectKey = resolvedProjectKey,
                                    ProjectName = resolvedProjectName,
                                    Status = ConversationStatus.Active,
                                    CreatedAtUtc = latestTime,
                                    UpdatedAtUtc = latestTime,
                                    LastActivityUtc = latestTime,
                                    MessageCount = msgs.Count
                                });

                                var converted = msgs
                                    .Select((m, idx) => ConversationMessageRecord.FromChatMessageItem(m, cid, idx + 1))
                                    .ToList();
                                repo.ReplaceMessages(cid, converted);
                                report.ProjectTranscriptsImported++;
                            }
                        }
                    }

                    repo.RecordMigration(ledgerKey, "project_transcript");
                    report.Executed = true;
                }
                catch (Exception ex)
                {
                    report.MalformedFilesQuarantined++;
                    report.Warnings.Add($"{fname} malformed ({SecretRedactor.Redact(ex.Message)}); quarantined backup preserved.");
                    CorruptFileQuarantine.PreserveFile(jsonFile, backupRootDir);
                }
            }

            return report;
        }

        private static string DeriveTitle(string prompt)
        {
            string clean = prompt.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length <= 48 ? clean : clean[..48].TrimEnd() + "...";
        }

        private static string HashKey(string value)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
