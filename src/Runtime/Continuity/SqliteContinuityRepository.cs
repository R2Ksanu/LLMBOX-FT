using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LlmodelBox.Common;
using LlmodelBox.Runtime.Memory;
using Microsoft.Data.Sqlite;

namespace LlmodelBox.Runtime.Continuity
{
    /// <summary>
    /// Transactional SQLite implementation of IConversationRepository:
    ///   - WAL journal mode + foreign keys + busy timeout + single serialized write lock
    ///   - Versioned schema with strict non-downgrade verification
    ///   - FTS5 cross-entity progressive search across messages, checkpoints, memories, and events
    ///   - Automatic corrupt DB quarantine & buffered retry tracking on disk/permission failures
    /// </summary>
    public sealed class SqliteContinuityRepository : IConversationRepository
    {
        public const int CurrentSchemaVersion = 1;
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

        private readonly string _dbPath;
        private readonly string _backupRootDir;
        private readonly object _writeGate = new();
        private readonly List<Action<SqliteConnection>> _pendingWriteQueue = new();
        private PersistenceDiagnosticReport _diagnostics = new();
        private bool _forceWriteErrorForTest;
        private bool _disposed;

        public event Action<PersistenceDiagnosticReport>? DiagnosticsChanged;

        public SqliteContinuityRepository()
            : this(Path.Combine(AppConstants.DataDirectory, "llmodelbox_continuity.db"),
                   Path.Combine(AppConstants.DataDirectory, "backups"))
        {
        }

        public SqliteContinuityRepository(string dbPath, string? backupRootDir = null)
        {
            _dbPath = string.IsNullOrWhiteSpace(dbPath)
                ? Path.Combine(AppConstants.DataDirectory, "llmodelbox_continuity.db")
                : Path.GetFullPath(dbPath);
            _backupRootDir = string.IsNullOrWhiteSpace(backupRootDir)
                ? Path.Combine(Path.GetDirectoryName(_dbPath) ?? AppConstants.DataDirectory, "backups")
                : Path.GetFullPath(backupRootDir);

            InitializeDatabaseWithRecovery();
        }

        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public void SetSimulatedWriteFailure(bool fail) => _forceWriteErrorForTest = fail;

        public PersistenceDiagnosticReport GetDiagnostics()
        {
            lock (_writeGate)
            {
                return new PersistenceDiagnosticReport
                {
                    State = _diagnostics.State,
                    LastSavedUtc = _diagnostics.LastSavedUtc,
                    PendingRetryCount = _pendingWriteQueue.Count,
                    LastWarning = _diagnostics.LastWarning,
                    LastError = _diagnostics.LastError,
                    QuarantineBackups = new List<string>(_diagnostics.QuarantineBackups),
                    MigrationNotes = new List<string>(_diagnostics.MigrationNotes),
                    RecoveredInterruptedTurns = _diagnostics.RecoveredInterruptedTurns
                };
            }
        }

        public bool RetryPendingWrites()
        {
            lock (_writeGate)
            {
                if (_pendingWriteQueue.Count == 0)
                {
                    _diagnostics.PendingRetryCount = 0;
                    if (_diagnostics.State == PersistenceState.PendingSave || _diagnostics.State == PersistenceState.DegradedError)
                    {
                        _diagnostics.State = PersistenceState.Healthy;
                        _diagnostics.LastError = string.Empty;
                        RaiseDiagnostics();
                    }
                    return true;
                }

                try
                {
                    if (_forceWriteErrorForTest)
                        throw new IOException("Simulated disk write failure.");

                    using var conn = OpenConnection();
                    using var tx = conn.BeginTransaction();
                    foreach (var op in _pendingWriteQueue)
                        op(conn);
                    tx.Commit();

                    _pendingWriteQueue.Clear();
                    _diagnostics.PendingRetryCount = 0;
                    _diagnostics.State = PersistenceState.Healthy;
                    _diagnostics.LastError = string.Empty;
                    _diagnostics.LastSavedUtc = DateTime.UtcNow;
                    RaiseDiagnostics();
                    return true;
                }
                catch (Exception ex)
                {
                    _diagnostics.State = PersistenceState.DegradedError;
                    _diagnostics.LastError = SecretRedactor.Redact(ex.Message);
                    _diagnostics.PendingRetryCount = _pendingWriteQueue.Count;
                    RaiseDiagnostics();
                    return false;
                }
            }
        }

        public string? CreateBackup(string? destinationDirectory = null)
        {
            lock (_writeGate)
            {
                try
                {
                    if (!File.Exists(_dbPath)) return null;
                    string targetDir = string.IsNullOrWhiteSpace(destinationDirectory)
                        ? Path.Combine(_backupRootDir, "snapshots")
                        : destinationDirectory!;
                    Directory.CreateDirectory(targetDir);
                    string dest = Path.Combine(targetDir, $"llmodelbox_continuity.{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.db");
                    using var src = OpenConnection();
                    using var dst = new SqliteConnection($"Data Source={dest};Pooling=False");
                    dst.Open();
                    src.BackupDatabase(dst);
                    return dest;
                }
                catch
                {
                    return null;
                }
            }
        }

        public ConversationRecord UpsertConversation(ConversationRecord conversation)
        {
            if (conversation == null) throw new ArgumentNullException(nameof(conversation));
            var rec = CloneConversation(conversation);
            rec.Title = SecretRedactor.Redact(string.IsNullOrWhiteSpace(rec.Title) ? "New Conversation" : rec.Title);
            if (!string.IsNullOrWhiteSpace(rec.LatestCheckpointSummary))
                rec.LatestCheckpointSummary = SecretRedactor.Redact(rec.LatestCheckpointSummary);

            ExecuteWrite(conn => UpsertConversationCore(conn, rec));
            return rec;
        }

        public ConversationRecord? GetConversation(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return null;
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id, title, project_key, project_name, status, created_at_utc, updated_at_utc,
                                       last_activity_utc, active_model_id, active_provider, parent_conversation_id,
                                       is_archived, is_deleted, deleted_at_utc, message_count, latest_checkpoint_summary
                                FROM conversations WHERE id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", conversationId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadConversation(reader) : null;
        }

        public IReadOnlyList<ConversationRecord> ListConversations(
            string? projectKey = null,
            bool includeArchived = false,
            bool includeDeleted = false,
            int offset = 0,
            int limit = int.MaxValue)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            var sb = new StringBuilder(@"
                SELECT id, title, project_key, project_name, status, created_at_utc, updated_at_utc,
                       last_activity_utc, active_model_id, active_provider, parent_conversation_id,
                       is_archived, is_deleted, deleted_at_utc, message_count, latest_checkpoint_summary
                FROM conversations WHERE 1=1 ");

            if (projectKey != null)
            {
                if (projectKey.Length == 0)
                    sb.Append(" AND project_key = '' ");
                else
                {
                    sb.Append(" AND LOWER(project_key) = LOWER($pk) ");
                    cmd.Parameters.AddWithValue("$pk", projectKey);
                }
            }
            if (!includeArchived) sb.Append(" AND is_archived = 0 ");
            if (!includeDeleted) sb.Append(" AND is_deleted = 0 ");
            sb.Append(" ORDER BY updated_at_utc DESC ");
            if (limit > 0 && limit < int.MaxValue)
            {
                sb.Append(" LIMIT $lim OFFSET $off ");
                cmd.Parameters.AddWithValue("$lim", limit);
                cmd.Parameters.AddWithValue("$off", Math.Max(0, offset));
            }
            cmd.CommandText = sb.ToString();

            var list = new List<ConversationRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadConversation(reader));
            return list;
        }

        public void ArchiveConversation(string conversationId, bool archived)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return;
            ExecuteWrite(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE conversations
                                    SET is_archived = $arch,
                                        status = CASE WHEN $arch = 1 THEN $archStatus ELSE $actStatus END,
                                        updated_at_utc = $now
                                    WHERE id = $id;";
                cmd.Parameters.AddWithValue("$arch", archived ? 1 : 0);
                cmd.Parameters.AddWithValue("$archStatus", (int)ConversationStatus.Archived);
                cmd.Parameters.AddWithValue("$actStatus", (int)ConversationStatus.Active);
                cmd.Parameters.AddWithValue("$now", ToIso(DateTime.UtcNow));
                cmd.Parameters.AddWithValue("$id", conversationId);
                cmd.ExecuteNonQuery();
            });
        }

        public void SoftDeleteConversation(string conversationId, bool deleteDerivedMemories = false)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return;
            ExecuteWrite(conn =>
            {
                var now = ToIso(DateTime.UtcNow);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE conversations
                                        SET is_deleted = 1, deleted_at_utc = $now, status = $delStatus, updated_at_utc = $now
                                        WHERE id = $id;";
                    cmd.Parameters.AddWithValue("$now", now);
                    cmd.Parameters.AddWithValue("$delStatus", (int)ConversationStatus.Deleted);
                    cmd.Parameters.AddWithValue("$id", conversationId);
                    cmd.ExecuteNonQuery();
                }

                if (deleteDerivedMemories)
                {
                    DeleteMemoriesByConversationCore(conn, conversationId);
                }
            });
        }

        public void RestoreConversation(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return;
            ExecuteWrite(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE conversations
                                    SET is_deleted = 0, is_archived = 0, deleted_at_utc = NULL,
                                        status = $actStatus, updated_at_utc = $now
                                    WHERE id = $id;";
                cmd.Parameters.AddWithValue("$actStatus", (int)ConversationStatus.Active);
                cmd.Parameters.AddWithValue("$now", ToIso(DateTime.UtcNow));
                cmd.Parameters.AddWithValue("$id", conversationId);
                cmd.ExecuteNonQuery();
            });
        }

        public void DeleteConversationPermanently(string conversationId, bool deleteDerivedMemories = false)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return;
            ExecuteWrite(conn =>
            {
                if (deleteDerivedMemories)
                    DeleteMemoriesByConversationCore(conn, conversationId);

                using (var fts = conn.CreateCommand())
                {
                    fts.CommandText = "DELETE FROM continuity_fts WHERE conversation_id = $cid AND kind IN ('Message','Checkpoint','WorkEvent');";
                    fts.Parameters.AddWithValue("$cid", conversationId);
                    fts.ExecuteNonQuery();
                }

                using (var ev = conn.CreateCommand())
                {
                    ev.CommandText = "DELETE FROM work_events WHERE conversation_id = $cid;";
                    ev.Parameters.AddWithValue("$cid", conversationId);
                    ev.ExecuteNonQuery();
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM conversations WHERE id = $cid;";
                cmd.Parameters.AddWithValue("$cid", conversationId);
                cmd.ExecuteNonQuery();
            });
        }

        public void ReplaceMessages(string conversationId, IReadOnlyList<ConversationMessageRecord> messages)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return;
            var snapshot = messages.Select(CloneMessage).ToList();

            ExecuteWrite(conn =>
            {
                EnsureConversationRowCore(conn, conversationId);

                using (var delFts = conn.CreateCommand())
                {
                    delFts.CommandText = "DELETE FROM continuity_fts WHERE kind = 'Message' AND conversation_id = $cid;";
                    delFts.Parameters.AddWithValue("$cid", conversationId);
                    delFts.ExecuteNonQuery();
                }
                using (var del = conn.CreateCommand())
                {
                    del.CommandText = "DELETE FROM messages WHERE conversation_id = $cid;";
                    del.Parameters.AddWithValue("$cid", conversationId);
                    del.ExecuteNonQuery();
                }

                string projectKey = GetProjectKeyForConversationCore(conn, conversationId);
                for (int i = 0; i < snapshot.Count; i++)
                {
                    var m = snapshot[i];
                    m.ConversationId = conversationId;
                    m.SequenceNumber = i + 1;
                    UpsertMessageCore(conn, m, projectKey);
                }

                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE conversations SET message_count = $cnt, updated_at_utc = $now, last_activity_utc = $now WHERE id = $cid;";
                upd.Parameters.AddWithValue("$cnt", snapshot.Count);
                upd.Parameters.AddWithValue("$now", ToIso(DateTime.UtcNow));
                upd.Parameters.AddWithValue("$cid", conversationId);
                upd.ExecuteNonQuery();
            });
        }

        public void UpsertMessage(ConversationMessageRecord message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.ConversationId)) return;
            var copy = CloneMessage(message);

            ExecuteWrite(conn =>
            {
                EnsureConversationRowCore(conn, copy.ConversationId);
                string pk = GetProjectKeyForConversationCore(conn, copy.ConversationId);
                UpsertMessageCore(conn, copy, pk);

                using var upd = conn.CreateCommand();
                upd.CommandText = @"UPDATE conversations
                                    SET message_count = (SELECT COUNT(*) FROM messages WHERE conversation_id = $cid),
                                        updated_at_utc = $now,
                                        last_activity_utc = $now
                                    WHERE id = $cid;";
                upd.Parameters.AddWithValue("$cid", copy.ConversationId);
                upd.Parameters.AddWithValue("$now", ToIso(DateTime.UtcNow));
                upd.ExecuteNonQuery();
            });
        }

        public IReadOnlyList<ConversationMessageRecord> GetMessages(string conversationId, int offset = 0, int limit = int.MaxValue)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return Array.Empty<ConversationMessageRecord>();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            var sb = new StringBuilder(@"
                SELECT id, conversation_id, turn_id, sequence_number, sender_role, sender_name, content,
                       structured_blocks_json, attachments_json, tool_steps_json, model_name, provider,
                       turn_status, is_error, active_file_path, files_modified_count, added_lines_count,
                       removed_lines_count, elapsed_seconds, created_at_utc, updated_at_utc, display_timestamp
                FROM messages WHERE conversation_id = $cid
                ORDER BY sequence_number ASC, created_at_utc ASC ");
            cmd.Parameters.AddWithValue("$cid", conversationId);
            if (limit > 0 && limit < int.MaxValue)
            {
                sb.Append(" LIMIT $lim OFFSET $off ");
                cmd.Parameters.AddWithValue("$lim", limit);
                cmd.Parameters.AddWithValue("$off", Math.Max(0, offset));
            }
            cmd.CommandText = sb.ToString();

            var list = new List<ConversationMessageRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadMessage(reader));
            return list;
        }

        public IReadOnlyList<ConversationMessageRecord> GetMessagesAround(string messageId, int beforeCount = 4, int afterCount = 4)
        {
            if (string.IsNullOrWhiteSpace(messageId)) return Array.Empty<ConversationMessageRecord>();
            using var conn = OpenConnection();
            string? convId = null;
            int seq = 0;

            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT conversation_id, sequence_number FROM messages WHERE id = $id LIMIT 1;";
                probe.Parameters.AddWithValue("$id", messageId);
                using var pr = probe.ExecuteReader();
                if (!pr.Read()) return Array.Empty<ConversationMessageRecord>();
                convId = pr.GetString(0);
                seq = pr.GetInt32(1);
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, conversation_id, turn_id, sequence_number, sender_role, sender_name, content,
                       structured_blocks_json, attachments_json, tool_steps_json, model_name, provider,
                       turn_status, is_error, active_file_path, files_modified_count, added_lines_count,
                       removed_lines_count, elapsed_seconds, created_at_utc, updated_at_utc, display_timestamp
                FROM messages
                WHERE conversation_id = $cid AND sequence_number BETWEEN $minSeq AND $maxSeq
                ORDER BY sequence_number ASC;";
            cmd.Parameters.AddWithValue("$cid", convId);
            cmd.Parameters.AddWithValue("$minSeq", Math.Max(1, seq - Math.Max(0, beforeCount)));
            cmd.Parameters.AddWithValue("$maxSeq", seq + Math.Max(0, afterCount));

            var list = new List<ConversationMessageRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadMessage(reader));
            return list;
        }

        public void UpsertTurn(TurnRecord turn)
        {
            if (turn == null || string.IsNullOrWhiteSpace(turn.ConversationId)) return;
            var copy = CloneTurn(turn);
            copy.UserPrompt = SecretRedactor.Redact(copy.UserPrompt);
            copy.AssistantOutput = SecretRedactor.Redact(copy.AssistantOutput);
            copy.ErrorMessage = SecretRedactor.Redact(copy.ErrorMessage);

            ExecuteWrite(conn =>
            {
                EnsureConversationRowCore(conn, copy.ConversationId);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO turns (id, conversation_id, project_key, user_message_id, assistant_message_id, user_prompt,
                                       assistant_output, status, started_at_utc, ended_at_utc, checkpoint_sequence,
                                       prompt_tokens_estimate, completion_tokens_estimate, error_message)
                    VALUES ($id, $cid, $pk, $umid, $amid, $up, $ao, $st, $sutc, $eutc, $cseq, $ptok, $ctok, $err)
                    ON CONFLICT(id) DO UPDATE SET
                        assistant_message_id = excluded.assistant_message_id,
                        assistant_output = excluded.assistant_output,
                        status = excluded.status,
                        ended_at_utc = excluded.ended_at_utc,
                        checkpoint_sequence = excluded.checkpoint_sequence,
                        prompt_tokens_estimate = excluded.prompt_tokens_estimate,
                        completion_tokens_estimate = excluded.completion_tokens_estimate,
                        error_message = excluded.error_message;";
                cmd.Parameters.AddWithValue("$id", copy.Id);
                cmd.Parameters.AddWithValue("$cid", copy.ConversationId);
                cmd.Parameters.AddWithValue("$pk", copy.ProjectKey ?? string.Empty);
                cmd.Parameters.AddWithValue("$umid", copy.UserMessageId ?? string.Empty);
                cmd.Parameters.AddWithValue("$amid", copy.AssistantMessageId ?? string.Empty);
                cmd.Parameters.AddWithValue("$up", copy.UserPrompt);
                cmd.Parameters.AddWithValue("$ao", copy.AssistantOutput);
                cmd.Parameters.AddWithValue("$st", (int)copy.Status);
                cmd.Parameters.AddWithValue("$sutc", ToIso(copy.StartedAtUtc));
                cmd.Parameters.AddWithValue("$eutc", copy.EndedAtUtc.HasValue ? ToIso(copy.EndedAtUtc.Value) : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$cseq", copy.CheckpointSequence);
                cmd.Parameters.AddWithValue("$ptok", copy.PromptTokensEstimate);
                cmd.Parameters.AddWithValue("$ctok", copy.CompletionTokensEstimate);
                cmd.Parameters.AddWithValue("$err", copy.ErrorMessage);
                cmd.ExecuteNonQuery();
            });
        }

        public IReadOnlyList<TurnRecord> GetTurns(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return Array.Empty<TurnRecord>();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id, conversation_id, project_key, user_message_id, assistant_message_id, user_prompt,
                                       assistant_output, status, started_at_utc, ended_at_utc, checkpoint_sequence,
                                       prompt_tokens_estimate, completion_tokens_estimate, error_message
                                FROM turns WHERE conversation_id = $cid ORDER BY started_at_utc ASC;";
            cmd.Parameters.AddWithValue("$cid", conversationId);

            var list = new List<TurnRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new TurnRecord
                {
                    Id = reader.GetString(0),
                    ConversationId = reader.GetString(1),
                    ProjectKey = reader.GetString(2),
                    UserMessageId = reader.GetString(3),
                    AssistantMessageId = reader.GetString(4),
                    UserPrompt = reader.GetString(5),
                    AssistantOutput = reader.GetString(6),
                    Status = (TurnLifecycleStatus)reader.GetInt32(7),
                    StartedAtUtc = FromIso(reader.GetString(8)),
                    EndedAtUtc = reader.IsDBNull(9) ? null : FromIso(reader.GetString(9)),
                    CheckpointSequence = reader.GetInt32(10),
                    PromptTokensEstimate = reader.GetInt32(11),
                    CompletionTokensEstimate = reader.GetInt32(12),
                    ErrorMessage = reader.GetString(13)
                });
            }
            return list;
        }

        public int RecoverInterruptedTurns()
        {
            int recovered = 0;
            ExecuteWrite(conn =>
            {
                var now = ToIso(DateTime.UtcNow);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE turns
                                        SET status = $intStatus,
                                            ended_at_utc = COALESCE(ended_at_utc, $now),
                                            error_message = CASE WHEN error_message = '' THEN 'Process terminated before turn completed.' ELSE error_message END
                                        WHERE status IN ($p, $s);";
                    cmd.Parameters.AddWithValue("$intStatus", (int)TurnLifecycleStatus.Interrupted);
                    cmd.Parameters.AddWithValue("$now", now);
                    cmd.Parameters.AddWithValue("$p", (int)TurnLifecycleStatus.Pending);
                    cmd.Parameters.AddWithValue("$s", (int)TurnLifecycleStatus.Streaming);
                    recovered += cmd.ExecuteNonQuery();
                }

                using (var mCmd = conn.CreateCommand())
                {
                    mCmd.CommandText = @"UPDATE messages
                                         SET turn_status = $intStatus,
                                             is_error = 1,
                                             content = CASE WHEN content = '' THEN '[Interrupted before completion]' ELSE content END,
                                             updated_at_utc = $now
                                         WHERE turn_status IN ($p, $s);";
                    mCmd.Parameters.AddWithValue("$intStatus", (int)TurnLifecycleStatus.Interrupted);
                    mCmd.Parameters.AddWithValue("$now", now);
                    mCmd.Parameters.AddWithValue("$p", (int)TurnLifecycleStatus.Pending);
                    mCmd.Parameters.AddWithValue("$s", (int)TurnLifecycleStatus.Streaming);
                    mCmd.ExecuteNonQuery();
                }

                using (var cCmd = conn.CreateCommand())
                {
                    cCmd.CommandText = @"UPDATE conversations
                                         SET status = $cIntStatus
                                         WHERE id IN (SELECT DISTINCT conversation_id FROM turns WHERE status = $tIntStatus AND ended_at_utc = $now);";
                    cCmd.Parameters.AddWithValue("$cIntStatus", (int)ConversationStatus.Interrupted);
                    cCmd.Parameters.AddWithValue("$tIntStatus", (int)TurnLifecycleStatus.Interrupted);
                    cCmd.Parameters.AddWithValue("$now", now);
                    cCmd.ExecuteNonQuery();
                }

                _diagnostics.RecoveredInterruptedTurns += recovered;
            });
            return recovered;
        }

        public bool RecordWorkEvent(WorkEventRecord workEvent)
        {
            if (workEvent == null || string.IsNullOrWhiteSpace(workEvent.ConversationId)) return false;
            var copy = CloneWorkEvent(workEvent);
            copy.Summary = SecretRedactor.Redact(copy.Summary);
            copy.Detail = SecretRedactor.Redact(copy.Detail);
            if (copy.Detail.Length > 2000) copy.Detail = copy.Detail[..2000] + "…";

            bool inserted = false;
            ExecuteWrite(conn =>
            {
                EnsureConversationRowCore(conn, copy.ConversationId);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO work_events
                        (id, correlation_id, conversation_id, turn_id, project_key, event_type, tool_name, summary, detail, file_path, status, created_at_utc)
                    VALUES
                        ($id, $corr, $cid, $tid, $pk, $et, $tn, $sum, $det, $fp, $st, $utc);";
                cmd.Parameters.AddWithValue("$id", copy.Id);
                cmd.Parameters.AddWithValue("$corr", string.IsNullOrWhiteSpace(copy.CorrelationId) ? copy.Id : copy.CorrelationId);
                cmd.Parameters.AddWithValue("$cid", copy.ConversationId);
                cmd.Parameters.AddWithValue("$tid", copy.TurnId ?? string.Empty);
                cmd.Parameters.AddWithValue("$pk", copy.ProjectKey ?? string.Empty);
                cmd.Parameters.AddWithValue("$et", copy.EventType ?? string.Empty);
                cmd.Parameters.AddWithValue("$tn", copy.ToolName ?? string.Empty);
                cmd.Parameters.AddWithValue("$sum", copy.Summary);
                cmd.Parameters.AddWithValue("$det", copy.Detail);
                cmd.Parameters.AddWithValue("$fp", copy.FilePath ?? string.Empty);
                cmd.Parameters.AddWithValue("$st", copy.Status ?? string.Empty);
                cmd.Parameters.AddWithValue("$utc", ToIso(copy.CreatedAtUtc));
                inserted = cmd.ExecuteNonQuery() > 0;

                if (inserted && !string.IsNullOrWhiteSpace(copy.Summary))
                {
                    UpsertFtsCore(conn, "WorkEvent", copy.Id, copy.ConversationId, copy.ProjectKey ?? string.Empty,
                        $"{copy.EventType} {copy.ToolName} {copy.FilePath}".Trim(), copy.Summary);
                }
            });
            return inserted;
        }

        public IReadOnlyList<WorkEventRecord> GetWorkEvents(string conversationId, int limit = 100)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return Array.Empty<WorkEventRecord>();
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id, correlation_id, conversation_id, turn_id, project_key, event_type, tool_name, summary, detail, file_path, status, created_at_utc
                                FROM work_events WHERE conversation_id = $cid ORDER BY created_at_utc DESC LIMIT $lim;";
            cmd.Parameters.AddWithValue("$cid", conversationId);
            cmd.Parameters.AddWithValue("$lim", Math.Max(1, limit));

            var list = new List<WorkEventRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new WorkEventRecord
                {
                    Id = reader.GetString(0),
                    CorrelationId = reader.GetString(1),
                    ConversationId = reader.GetString(2),
                    TurnId = reader.GetString(3),
                    ProjectKey = reader.GetString(4),
                    EventType = reader.GetString(5),
                    ToolName = reader.GetString(6),
                    Summary = reader.GetString(7),
                    Detail = reader.GetString(8),
                    FilePath = reader.GetString(9),
                    Status = reader.GetString(10),
                    CreatedAtUtc = FromIso(reader.GetString(11))
                });
            }
            return list;
        }

        public void SaveCheckpoint(HandoffCheckpoint checkpoint)
        {
            if (checkpoint == null || string.IsNullOrWhiteSpace(checkpoint.ConversationId)) return;
            var copy = JsonSerializer.Deserialize<HandoffCheckpoint>(JsonSerializer.Serialize(checkpoint, JsonOpts), JsonOpts) ?? checkpoint;
            copy.CurrentGoal = SecretRedactor.Redact(copy.CurrentGoal);
            copy.NextStep = SecretRedactor.Redact(copy.NextStep);
            copy.SummaryText = SecretRedactor.Redact(copy.SummaryText);
            string payloadJson = SecretRedactor.Redact(JsonSerializer.Serialize(copy, JsonOpts));

            ExecuteWrite(conn =>
            {
                EnsureConversationRowCore(conn, copy.ConversationId);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO checkpoints
                        (id, conversation_id, project_key, checkpoint_version, updated_at_utc, source_turn_id,
                         status, current_goal, next_step, summary_text, payload_json)
                    VALUES
                        ($id, $cid, $pk, $ver, $utc, $tid, $st, $goal, $next, $sum, $json)
                    ON CONFLICT(id) DO UPDATE SET
                        checkpoint_version = excluded.checkpoint_version,
                        updated_at_utc = excluded.updated_at_utc,
                        source_turn_id = excluded.source_turn_id,
                        status = excluded.status,
                        current_goal = excluded.current_goal,
                        next_step = excluded.next_step,
                        summary_text = excluded.summary_text,
                        payload_json = excluded.payload_json;";
                cmd.Parameters.AddWithValue("$id", copy.Id);
                cmd.Parameters.AddWithValue("$cid", copy.ConversationId);
                cmd.Parameters.AddWithValue("$pk", copy.ProjectKey ?? string.Empty);
                cmd.Parameters.AddWithValue("$ver", copy.CheckpointVersion);
                cmd.Parameters.AddWithValue("$utc", ToIso(copy.UpdatedAtUtc));
                cmd.Parameters.AddWithValue("$tid", copy.SourceTurnId ?? string.Empty);
                cmd.Parameters.AddWithValue("$st", (int)copy.Status);
                cmd.Parameters.AddWithValue("$goal", copy.CurrentGoal);
                cmd.Parameters.AddWithValue("$next", copy.NextStep);
                cmd.Parameters.AddWithValue("$sum", copy.SummaryText);
                cmd.Parameters.AddWithValue("$json", payloadJson);
                cmd.ExecuteNonQuery();

                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE conversations SET latest_checkpoint_summary = $sum, status = $st, updated_at_utc = $utc WHERE id = $cid;";
                upd.Parameters.AddWithValue("$sum", copy.SummaryText);
                upd.Parameters.AddWithValue("$st", (int)copy.Status);
                upd.Parameters.AddWithValue("$utc", ToIso(copy.UpdatedAtUtc));
                upd.Parameters.AddWithValue("$cid", copy.ConversationId);
                upd.ExecuteNonQuery();

                UpsertFtsCore(conn, "Checkpoint", copy.Id, copy.ConversationId, copy.ProjectKey ?? string.Empty,
                    copy.CurrentGoal, $"{copy.SummaryText}\nNext: {copy.NextStep}");
            });
        }

        public HandoffCheckpoint? GetLatestCheckpoint(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return null;
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT payload_json FROM checkpoints
                                WHERE conversation_id = $cid
                                ORDER BY checkpoint_version DESC, updated_at_utc DESC LIMIT 1;";
            cmd.Parameters.AddWithValue("$cid", conversationId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            try { return JsonSerializer.Deserialize<HandoffCheckpoint>(reader.GetString(0), JsonOpts); }
            catch { return null; }
        }

        public ProjectPreviousWorkPointer? GetLatestProjectWorkPointer(string projectKey, string? excludeConversationId = null)
        {
            if (string.IsNullOrWhiteSpace(projectKey)) return null;
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT cp.conversation_id, c.title, cp.project_key, c.status, cp.current_goal, cp.summary_text, cp.next_step, cp.updated_at_utc, cp.payload_json
                FROM checkpoints cp
                INNER JOIN conversations c ON c.id = cp.conversation_id
                WHERE LOWER(cp.project_key) = LOWER($pk)
                  AND c.is_deleted = 0
                  AND ($ex IS NULL OR cp.conversation_id <> $ex)
                ORDER BY cp.updated_at_utc DESC
                LIMIT 1;";
            cmd.Parameters.AddWithValue("$pk", projectKey);
            cmd.Parameters.AddWithValue("$ex", string.IsNullOrWhiteSpace(excludeConversationId) ? (object)DBNull.Value : excludeConversationId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            string blocker = string.Empty;
            string completed = string.Empty;
            try
            {
                var cp = JsonSerializer.Deserialize<HandoffCheckpoint>(reader.GetString(8), JsonOpts);
                if (cp != null)
                {
                    blocker = cp.Blockers.FirstOrDefault() ?? string.Empty;
                    completed = cp.Completed.Count > 0 ? string.Join("; ", cp.Completed.TakeLast(2)) : string.Empty;
                }
            }
            catch { }

            return new ProjectPreviousWorkPointer
            {
                ConversationId = reader.GetString(0),
                ConversationTitle = reader.GetString(1),
                ProjectKey = reader.GetString(2),
                Status = (ConversationStatus)reader.GetInt32(3),
                CurrentGoal = reader.GetString(4),
                CompletedSummary = string.IsNullOrWhiteSpace(completed) ? reader.GetString(5) : completed,
                Blocker = blocker,
                NextStep = reader.GetString(6),
                UpdatedAtUtc = FromIso(reader.GetString(7))
            };
        }

        public MemoryRecord UpsertMemory(MemoryRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            var copy = record.Clone();
            copy.Content = SecretRedactor.Redact(copy.Content);

            ExecuteWrite(conn =>
            {
                // Auto-supersede conflicting active records sharing the same canonical key in same scope/project
                if (!string.IsNullOrWhiteSpace(copy.CanonicalKey) && copy.Status == MemoryLifecycleStatus.Active)
                {
                    using var sup = conn.CreateCommand();
                    sup.CommandText = @"UPDATE memories
                                        SET status = $supStatus, superseded_by_id = $newId, updated_at_utc = $now
                                        WHERE id <> $newId
                                          AND status = $actStatus
                                          AND scope = $sc
                                          AND LOWER(project_key) = LOWER($pk)
                                          AND LOWER(canonical_key) = LOWER($ck);";
                    sup.Parameters.AddWithValue("$supStatus", (int)MemoryLifecycleStatus.Superseded);
                    sup.Parameters.AddWithValue("$actStatus", (int)MemoryLifecycleStatus.Active);
                    sup.Parameters.AddWithValue("$newId", copy.Id);
                    sup.Parameters.AddWithValue("$now", ToIso(DateTime.UtcNow));
                    sup.Parameters.AddWithValue("$sc", (int)copy.Scope);
                    sup.Parameters.AddWithValue("$pk", copy.ProjectKey ?? string.Empty);
                    sup.Parameters.AddWithValue("$ck", copy.CanonicalKey);
                    sup.ExecuteNonQuery();
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO memories
                        (id, scope, project_key, conversation_id, turn_id, category, status, canonical_key, superseded_by_id,
                         provenance, source, source_message_id, source_event_id, source_file_path, confidence, is_pinned,
                         is_user_editable, content, created_at_utc, updated_at_utc, last_confirmed_utc, expires_utc)
                    VALUES
                        ($id, $sc, $pk, $cid, $tid, $cat, $st, $ck, $sby, $prov, $src, $smid, $seid, $sfp,
                         $conf, $pin, $edit, $cnt, $cutc, $uutc, $lutc, $exp)
                    ON CONFLICT(id) DO UPDATE SET
                        scope = excluded.scope,
                        project_key = excluded.project_key,
                        conversation_id = excluded.conversation_id,
                        category = excluded.category,
                        status = excluded.status,
                        canonical_key = excluded.canonical_key,
                        superseded_by_id = excluded.superseded_by_id,
                        provenance = excluded.provenance,
                        source = excluded.source,
                        confidence = excluded.confidence,
                        is_pinned = excluded.is_pinned,
                        is_user_editable = excluded.is_user_editable,
                        content = excluded.content,
                        updated_at_utc = excluded.updated_at_utc,
                        last_confirmed_utc = excluded.last_confirmed_utc,
                        expires_utc = excluded.expires_utc;";
                cmd.Parameters.AddWithValue("$id", copy.Id);
                cmd.Parameters.AddWithValue("$sc", (int)copy.Scope);
                cmd.Parameters.AddWithValue("$pk", copy.ProjectKey ?? string.Empty);
                cmd.Parameters.AddWithValue("$cid", copy.ConversationId ?? string.Empty);
                cmd.Parameters.AddWithValue("$tid", copy.TurnId ?? string.Empty);
                cmd.Parameters.AddWithValue("$cat", (int)copy.Category);
                cmd.Parameters.AddWithValue("$st", (int)copy.Status);
                cmd.Parameters.AddWithValue("$ck", copy.CanonicalKey ?? string.Empty);
                cmd.Parameters.AddWithValue("$sby", (object?)copy.SupersededById ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$prov", (int)copy.Provenance);
                cmd.Parameters.AddWithValue("$src", copy.Source ?? string.Empty);
                cmd.Parameters.AddWithValue("$smid", (object?)copy.SourceMessageId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$seid", (object?)copy.SourceEventId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sfp", (object?)copy.SourceFilePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$conf", copy.Confidence);
                cmd.Parameters.AddWithValue("$pin", copy.IsPinned ? 1 : 0);
                cmd.Parameters.AddWithValue("$edit", copy.IsUserEditable ? 1 : 0);
                cmd.Parameters.AddWithValue("$cnt", copy.Content);
                cmd.Parameters.AddWithValue("$cutc", ToIso(copy.TimestampUtc));
                cmd.Parameters.AddWithValue("$uutc", ToIso(copy.UpdatedUtc));
                cmd.Parameters.AddWithValue("$lutc", ToIso(copy.LastConfirmedUtc));
                cmd.Parameters.AddWithValue("$exp", copy.ExpiresUtc.HasValue ? ToIso(copy.ExpiresUtc.Value) : (object)DBNull.Value);
                cmd.ExecuteNonQuery();

                if (copy.Status == MemoryLifecycleStatus.Active)
                {
                    UpsertFtsCore(conn, "Memory", copy.Id, copy.ConversationId ?? string.Empty, copy.ProjectKey ?? string.Empty,
                        $"{copy.Scope} {copy.Category}", copy.Content);
                }
                else
                {
                    DeleteFtsCore(conn, "Memory", copy.Id);
                }
            });

            return copy;
        }

        public MemoryRecord? GetMemory(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id, scope, project_key, conversation_id, turn_id, category, status, canonical_key,
                                       superseded_by_id, provenance, source, source_message_id, source_event_id,
                                       source_file_path, confidence, is_pinned, is_user_editable, content,
                                       created_at_utc, updated_at_utc, last_confirmed_utc, expires_utc
                                FROM memories WHERE id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadMemory(reader) : null;
        }

        public IReadOnlyList<MemoryRecord> QueryMemories(
            MemoryScope? scope = null,
            string? projectKey = null,
            string? conversationId = null,
            string? turnId = null,
            bool includeInactive = false)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            var sb = new StringBuilder(@"
                SELECT id, scope, project_key, conversation_id, turn_id, category, status, canonical_key,
                       superseded_by_id, provenance, source, source_message_id, source_event_id,
                       source_file_path, confidence, is_pinned, is_user_editable, content,
                       created_at_utc, updated_at_utc, last_confirmed_utc, expires_utc
                FROM memories WHERE 1=1 ");

            if (scope.HasValue)
            {
                sb.Append(" AND scope = $sc ");
                cmd.Parameters.AddWithValue("$sc", (int)scope.Value);
            }
            if (!string.IsNullOrWhiteSpace(projectKey))
            {
                sb.Append(" AND (LOWER(project_key) = LOWER($pk) OR scope = $gScope OR scope = $sScope) ");
                cmd.Parameters.AddWithValue("$pk", projectKey);
                cmd.Parameters.AddWithValue("$gScope", (int)MemoryScope.UserGlobal);
                cmd.Parameters.AddWithValue("$sScope", (int)MemoryScope.CurrentSession);
            }
            if (!string.IsNullOrWhiteSpace(conversationId))
            {
                sb.Append(" AND (conversation_id = '' OR conversation_id = $cid OR scope IN ($pScope, $sScope2, $gScope2)) ");
                cmd.Parameters.AddWithValue("$cid", conversationId);
                cmd.Parameters.AddWithValue("$pScope", (int)MemoryScope.CurrentProject);
                cmd.Parameters.AddWithValue("$sScope2", (int)MemoryScope.CurrentSession);
                cmd.Parameters.AddWithValue("$gScope2", (int)MemoryScope.UserGlobal);
            }
            if (!string.IsNullOrWhiteSpace(turnId))
            {
                sb.Append(" AND (turn_id = '' OR turn_id = $tid) ");
                cmd.Parameters.AddWithValue("$tid", turnId);
            }
            if (!includeInactive)
            {
                sb.Append(" AND status = $act ");
                cmd.Parameters.AddWithValue("$act", (int)MemoryLifecycleStatus.Active);
            }
            sb.Append(" ORDER BY is_pinned DESC, scope DESC, confidence DESC, updated_at_utc DESC;");
            cmd.CommandText = sb.ToString();

            var now = DateTime.UtcNow;
            var list = new List<MemoryRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var rec = ReadMemory(reader);
                if (!includeInactive && rec.IsExpired(now)) continue;
                list.Add(rec);
            }
            return list;
        }

        public bool DeleteMemory(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            bool deleted = false;
            ExecuteWrite(conn =>
            {
                DeleteFtsCore(conn, "Memory", id);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM memories WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                deleted = cmd.ExecuteNonQuery() > 0;
            });
            return deleted;
        }

        public int ForgetProjectMemory(string projectKey)
        {
            if (string.IsNullOrWhiteSpace(projectKey)) return 0;
            int removed = 0;
            ExecuteWrite(conn =>
            {
                using (var fts = conn.CreateCommand())
                {
                    fts.CommandText = "DELETE FROM continuity_fts WHERE kind = 'Memory' AND LOWER(project_key) = LOWER($pk);";
                    fts.Parameters.AddWithValue("$pk", projectKey);
                    fts.ExecuteNonQuery();
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM memories WHERE LOWER(project_key) = LOWER($pk) AND scope <> $global;";
                cmd.Parameters.AddWithValue("$pk", projectKey);
                cmd.Parameters.AddWithValue("$global", (int)MemoryScope.UserGlobal);
                removed = cmd.ExecuteNonQuery();
            });
            return removed;
        }

        public int DeleteAllLocalContinuityData()
        {
            int count = 0;
            ExecuteWrite(conn =>
            {
                foreach (var table in new[] { "continuity_fts", "work_events", "checkpoints", "turns", "messages", "memories", "conversations" })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"DELETE FROM {table};";
                    count += cmd.ExecuteNonQuery();
                }
            });
            return count;
        }

        public IReadOnlyList<ConversationSearchHit> Search(
            string query,
            string? projectKey = null,
            bool crossProject = false,
            int limit = 20)
        {
            if (string.IsNullOrWhiteSpace(query) || limit <= 0) return Array.Empty<ConversationSearchHit>();
            string ftsExpr = BuildSafeFtsQuery(query);
            if (string.IsNullOrWhiteSpace(ftsExpr)) return Array.Empty<ConversationSearchHit>();

            using var conn = OpenConnection();
            var hits = new List<ConversationSearchHit>();
            try
            {
                using var cmd = conn.CreateCommand();
                var sb = new StringBuilder(@"
                    SELECT f.kind, f.entity_id, f.conversation_id, f.project_key, f.title,
                           substr(f.body, 1, 240) AS snip,
                           -bm25(continuity_fts) AS rank_score,
                           COALESCE(c.updated_at_utc, '') AS ts
                    FROM continuity_fts f
                    LEFT JOIN conversations c ON c.id = f.conversation_id
                    WHERE continuity_fts MATCH $q
                      AND (c.id IS NULL OR c.is_deleted = 0) ");
                cmd.Parameters.AddWithValue("$q", ftsExpr);
                if (!crossProject && !string.IsNullOrWhiteSpace(projectKey))
                {
                    sb.Append(" AND (LOWER(f.project_key) = LOWER($pk) OR f.project_key = '') ");
                    cmd.Parameters.AddWithValue("$pk", projectKey);
                }
                sb.Append(" ORDER BY rank_score DESC LIMIT $lim;");
                cmd.Parameters.AddWithValue("$lim", limit);
                cmd.CommandText = sb.ToString();

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    hits.Add(new ConversationSearchHit
                    {
                        Kind = reader.GetString(0),
                        Id = reader.GetString(1),
                        ConversationId = reader.GetString(2),
                        ProjectKey = reader.GetString(3),
                        Title = reader.GetString(4),
                        Snippet = reader.GetString(5),
                        Score = Math.Max(0.01, reader.IsDBNull(6) ? 1.0 : reader.GetDouble(6)),
                        TimestampUtc = reader.IsDBNull(7) || string.IsNullOrWhiteSpace(reader.GetString(7))
                            ? DateTime.UtcNow
                            : FromIso(reader.GetString(7))
                    });
                }
            }
            catch
            {
                // Fallback LIKE scan if FTS syntax cannot parse unusual symbols.
            }
            return hits;
        }

        public bool IsMigrationRecorded(string sourceKey)
        {
            if (string.IsNullOrWhiteSpace(sourceKey)) return false;
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM migration_ledger WHERE source_key = $k LIMIT 1;";
            cmd.Parameters.AddWithValue("$k", sourceKey);
            return cmd.ExecuteScalar() != null;
        }

        public void RecordMigration(string sourceKey, string sourceKind)
        {
            if (string.IsNullOrWhiteSpace(sourceKey)) return;
            ExecuteWrite(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT OR IGNORE INTO migration_ledger (source_key, source_kind, migrated_at_utc)
                                    VALUES ($k, $kind, $utc);";
                cmd.Parameters.AddWithValue("$k", sourceKey);
                cmd.Parameters.AddWithValue("$kind", sourceKind);
                cmd.Parameters.AddWithValue("$utc", ToIso(DateTime.UtcNow));
                cmd.ExecuteNonQuery();
            });
        }

        private void InitializeDatabaseWithRecovery()
        {
            string? dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            try
            {
                EnsureSchema();
            }
            catch (IncompatibleSchemaException)
            {
                throw;
            }
            catch (Exception ex) when (File.Exists(_dbPath))
            {
                SqliteConnection.ClearAllPools();
                var q1 = CorruptFileQuarantine.PreserveFile(_dbPath, _backupRootDir);
                CorruptFileQuarantine.PreserveFile(_dbPath + "-wal", _backupRootDir);
                CorruptFileQuarantine.PreserveFile(_dbPath + "-shm", _backupRootDir);
                try { File.Delete(_dbPath); } catch { }
                try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch { }
                try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch { }

                _diagnostics.State = PersistenceState.RecoveredFromBackup;
                _diagnostics.LastWarning = $"Damaged continuity database quarantined before recovery: {SecretRedactor.Redact(ex.Message)}";
                if (!string.IsNullOrEmpty(q1)) _diagnostics.QuarantineBackups.Add(q1);

                EnsureSchema();
            }
        }

        private void EnsureSchema()
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            using (var meta = conn.CreateCommand())
            {
                meta.CommandText = @"CREATE TABLE IF NOT EXISTS schema_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
                meta.ExecuteNonQuery();
            }

            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT value FROM schema_meta WHERE key = 'schema_version' LIMIT 1;";
                var val = check.ExecuteScalar()?.ToString();
                if (int.TryParse(val, out int existingVer) && existingVer > CurrentSchemaVersion)
                    throw new IncompatibleSchemaException($"Database schema version {existingVer} is newer than supported version {CurrentSchemaVersion}.");
            }

            string ddl = @"
                CREATE TABLE IF NOT EXISTS migration_ledger (
                    source_key TEXT PRIMARY KEY,
                    source_kind TEXT NOT NULL,
                    migrated_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS conversations (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    project_key TEXT NOT NULL,
                    project_name TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    last_activity_utc TEXT NOT NULL,
                    active_model_id TEXT NOT NULL,
                    active_provider TEXT NOT NULL,
                    parent_conversation_id TEXT,
                    is_archived INTEGER NOT NULL DEFAULT 0,
                    is_deleted INTEGER NOT NULL DEFAULT 0,
                    deleted_at_utc TEXT,
                    message_count INTEGER NOT NULL DEFAULT 0,
                    latest_checkpoint_summary TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_conv_proj_upd ON conversations(project_key, is_deleted, is_archived, updated_at_utc DESC);

                CREATE TABLE IF NOT EXISTS messages (
                    id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                    turn_id TEXT NOT NULL,
                    sequence_number INTEGER NOT NULL,
                    sender_role TEXT NOT NULL,
                    sender_name TEXT NOT NULL,
                    content TEXT NOT NULL,
                    structured_blocks_json TEXT NOT NULL,
                    attachments_json TEXT NOT NULL,
                    tool_steps_json TEXT NOT NULL,
                    model_name TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    turn_status INTEGER NOT NULL,
                    is_error INTEGER NOT NULL,
                    active_file_path TEXT NOT NULL,
                    files_modified_count INTEGER NOT NULL,
                    added_lines_count INTEGER NOT NULL,
                    removed_lines_count INTEGER NOT NULL,
                    elapsed_seconds REAL NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    display_timestamp TEXT NOT NULL,
                    UNIQUE(conversation_id, sequence_number)
                );
                CREATE INDEX IF NOT EXISTS ix_msg_conv_seq ON messages(conversation_id, sequence_number ASC);

                CREATE TABLE IF NOT EXISTS turns (
                    id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                    project_key TEXT NOT NULL,
                    user_message_id TEXT NOT NULL,
                    assistant_message_id TEXT NOT NULL,
                    user_prompt TEXT NOT NULL,
                    assistant_output TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    started_at_utc TEXT NOT NULL,
                    ended_at_utc TEXT,
                    checkpoint_sequence INTEGER NOT NULL,
                    prompt_tokens_estimate INTEGER NOT NULL,
                    completion_tokens_estimate INTEGER NOT NULL,
                    error_message TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_turns_status ON turns(status, conversation_id);

                CREATE TABLE IF NOT EXISTS work_events (
                    id TEXT PRIMARY KEY,
                    correlation_id TEXT NOT NULL UNIQUE,
                    conversation_id TEXT NOT NULL,
                    turn_id TEXT NOT NULL,
                    project_key TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    tool_name TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    detail TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_events_conv ON work_events(conversation_id, created_at_utc DESC);

                CREATE TABLE IF NOT EXISTS checkpoints (
                    id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                    project_key TEXT NOT NULL,
                    checkpoint_version INTEGER NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    source_turn_id TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    current_goal TEXT NOT NULL,
                    next_step TEXT NOT NULL,
                    summary_text TEXT NOT NULL,
                    payload_json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_cp_proj ON checkpoints(project_key, updated_at_utc DESC);

                CREATE TABLE IF NOT EXISTS memories (
                    id TEXT PRIMARY KEY,
                    scope INTEGER NOT NULL,
                    project_key TEXT NOT NULL,
                    conversation_id TEXT NOT NULL,
                    turn_id TEXT NOT NULL,
                    category INTEGER NOT NULL,
                    status INTEGER NOT NULL,
                    canonical_key TEXT NOT NULL,
                    superseded_by_id TEXT,
                    provenance INTEGER NOT NULL,
                    source TEXT NOT NULL,
                    source_message_id TEXT,
                    source_event_id TEXT,
                    source_file_path TEXT,
                    confidence REAL NOT NULL,
                    is_pinned INTEGER NOT NULL,
                    is_user_editable INTEGER NOT NULL,
                    content TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    last_confirmed_utc TEXT NOT NULL,
                    expires_utc TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_mem_scope_proj ON memories(scope, project_key, status, updated_at_utc DESC);

                CREATE VIRTUAL TABLE IF NOT EXISTS continuity_fts USING fts5(
                    kind UNINDEXED,
                    entity_id UNINDEXED,
                    conversation_id UNINDEXED,
                    project_key UNINDEXED,
                    title,
                    body,
                    tokenize='unicode61'
                );";

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = ddl;
                cmd.ExecuteNonQuery();
            }

            using (var setVer = conn.CreateCommand())
            {
                setVer.CommandText = "INSERT OR REPLACE INTO schema_meta (key, value) VALUES ('schema_version', $ver);";
                setVer.Parameters.AddWithValue("$ver", CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
                setVer.ExecuteNonQuery();
            }

            tx.Commit();
        }

        private SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared;Default Timeout=5");
            try
            {
                conn.Open();
                using var pragma = conn.CreateCommand();
                pragma.CommandText = @"
                    PRAGMA journal_mode = WAL;
                    PRAGMA foreign_keys = ON;
                    PRAGMA busy_timeout = 5000;
                    PRAGMA synchronous = NORMAL;";
                pragma.ExecuteNonQuery();
                return conn;
            }
            catch
            {
                try { conn.Close(); } catch { }
                try { conn.Dispose(); } catch { }
                SqliteConnection.ClearAllPools();
                throw;
            }
        }

        private void ExecuteWrite(Action<SqliteConnection> writeAction)
        {
            lock (_writeGate)
            {
                try
                {
                    if (_forceWriteErrorForTest)
                        throw new IOException("Simulated write failure.");

                    using var conn = OpenConnection();
                    using var tx = conn.BeginTransaction();
                    if (_pendingWriteQueue.Count > 0)
                    {
                        foreach (var queued in _pendingWriteQueue) queued(conn);
                        _pendingWriteQueue.Clear();
                    }
                    writeAction(conn);
                    tx.Commit();

                    _diagnostics.State = _diagnostics.QuarantineBackups.Count > 0 && !_diagnostics.LastSavedUtc.HasValue
                        ? PersistenceState.RecoveredFromBackup
                        : PersistenceState.Healthy;
                    _diagnostics.PendingRetryCount = 0;
                    _diagnostics.LastError = string.Empty;
                    _diagnostics.LastSavedUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _pendingWriteQueue.Add(writeAction);
                    _diagnostics.State = PersistenceState.DegradedError;
                    _diagnostics.PendingRetryCount = _pendingWriteQueue.Count;
                    _diagnostics.LastError = SecretRedactor.Redact(ex.Message);
                }
            }
            RaiseDiagnostics();
        }

        private void RaiseDiagnostics()
        {
            try { DiagnosticsChanged?.Invoke(GetDiagnostics()); } catch { }
        }

        private static void EnsureConversationRowCore(SqliteConnection conn, string conversationId)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO conversations
                    (id, title, project_key, project_name, status, created_at_utc, updated_at_utc, last_activity_utc,
                     active_model_id, active_provider, is_archived, is_deleted, message_count)
                VALUES
                    ($id, 'New Conversation', '', '', 0, $now, $now, $now, '', '', 0, 0, 0);";
            cmd.Parameters.AddWithValue("$id", conversationId);
            cmd.Parameters.AddWithValue("$now", ToIso(DateTime.UtcNow));
            cmd.ExecuteNonQuery();
        }

        private static string GetProjectKeyForConversationCore(SqliteConnection conn, string conversationId)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT project_key FROM conversations WHERE id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", conversationId);
            return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
        }

        private static void UpsertConversationCore(SqliteConnection conn, ConversationRecord rec)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO conversations
                    (id, title, project_key, project_name, status, created_at_utc, updated_at_utc, last_activity_utc,
                     active_model_id, active_provider, parent_conversation_id, is_archived, is_deleted, deleted_at_utc,
                     message_count, latest_checkpoint_summary)
                VALUES
                    ($id, $title, $pk, $pn, $st, $cutc, $uutc, $lutc, $mid, $prov, $par, $arch, $del, $dutc, $mcnt, $lcs)
                ON CONFLICT(id) DO UPDATE SET
                    title = excluded.title,
                    project_key = excluded.project_key,
                    project_name = excluded.project_name,
                    status = excluded.status,
                    updated_at_utc = excluded.updated_at_utc,
                    last_activity_utc = excluded.last_activity_utc,
                    active_model_id = excluded.active_model_id,
                    active_provider = excluded.active_provider,
                    parent_conversation_id = excluded.parent_conversation_id,
                    is_archived = excluded.is_archived,
                    is_deleted = excluded.is_deleted,
                    deleted_at_utc = excluded.deleted_at_utc,
                    message_count = excluded.message_count,
                    latest_checkpoint_summary = COALESCE(excluded.latest_checkpoint_summary, conversations.latest_checkpoint_summary);";
            cmd.Parameters.AddWithValue("$id", rec.Id);
            cmd.Parameters.AddWithValue("$title", rec.Title ?? "New Conversation");
            cmd.Parameters.AddWithValue("$pk", rec.ProjectKey ?? string.Empty);
            cmd.Parameters.AddWithValue("$pn", rec.ProjectName ?? string.Empty);
            cmd.Parameters.AddWithValue("$st", (int)rec.Status);
            cmd.Parameters.AddWithValue("$cutc", ToIso(rec.CreatedAtUtc));
            cmd.Parameters.AddWithValue("$uutc", ToIso(rec.UpdatedAtUtc));
            cmd.Parameters.AddWithValue("$lutc", ToIso(rec.LastActivityUtc));
            cmd.Parameters.AddWithValue("$mid", rec.ActiveModelId ?? string.Empty);
            cmd.Parameters.AddWithValue("$prov", rec.ActiveProvider ?? string.Empty);
            cmd.Parameters.AddWithValue("$par", (object?)rec.ParentConversationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$arch", rec.IsArchived ? 1 : 0);
            cmd.Parameters.AddWithValue("$del", rec.IsDeleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$dutc", rec.DeletedAtUtc.HasValue ? ToIso(rec.DeletedAtUtc.Value) : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$mcnt", rec.MessageCount);
            cmd.Parameters.AddWithValue("$lcs", (object?)rec.LatestCheckpointSummary ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private static void UpsertMessageCore(SqliteConnection conn, ConversationMessageRecord m, string projectKey)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO messages
                    (id, conversation_id, turn_id, sequence_number, sender_role, sender_name, content,
                     structured_blocks_json, attachments_json, tool_steps_json, model_name, provider,
                     turn_status, is_error, active_file_path, files_modified_count, added_lines_count,
                     removed_lines_count, elapsed_seconds, created_at_utc, updated_at_utc, display_timestamp)
                VALUES
                    ($id, $cid, $tid, $seq, $role, $sname, $cnt, $blk, $att, $stp, $mn, $pr,
                     $tst, $err, $afp, $fmc, $alc, $rlc, $el, $cutc, $uutc, $dts)
                ON CONFLICT(id) DO UPDATE SET
                    sequence_number = excluded.sequence_number,
                    content = excluded.content,
                    structured_blocks_json = excluded.structured_blocks_json,
                    attachments_json = excluded.attachments_json,
                    tool_steps_json = excluded.tool_steps_json,
                    model_name = excluded.model_name,
                    provider = excluded.provider,
                    turn_status = excluded.turn_status,
                    is_error = excluded.is_error,
                    active_file_path = excluded.active_file_path,
                    files_modified_count = excluded.files_modified_count,
                    added_lines_count = excluded.added_lines_count,
                    removed_lines_count = excluded.removed_lines_count,
                    elapsed_seconds = excluded.elapsed_seconds,
                    updated_at_utc = excluded.updated_at_utc,
                    display_timestamp = excluded.display_timestamp;";
            cmd.Parameters.AddWithValue("$id", m.Id);
            cmd.Parameters.AddWithValue("$cid", m.ConversationId);
            cmd.Parameters.AddWithValue("$tid", m.TurnId ?? string.Empty);
            cmd.Parameters.AddWithValue("$seq", m.SequenceNumber);
            cmd.Parameters.AddWithValue("$role", m.SenderRole ?? "User");
            cmd.Parameters.AddWithValue("$sname", m.SenderName ?? "Operator");
            cmd.Parameters.AddWithValue("$cnt", m.Content ?? string.Empty);
            cmd.Parameters.AddWithValue("$blk", m.StructuredBlocksJson ?? "[]");
            cmd.Parameters.AddWithValue("$att", m.AttachmentsJson ?? "[]");
            cmd.Parameters.AddWithValue("$stp", m.ToolStepsJson ?? "[]");
            cmd.Parameters.AddWithValue("$mn", m.ModelName ?? string.Empty);
            cmd.Parameters.AddWithValue("$pr", m.Provider ?? string.Empty);
            cmd.Parameters.AddWithValue("$tst", (int)m.TurnStatus);
            cmd.Parameters.AddWithValue("$err", m.IsError ? 1 : 0);
            cmd.Parameters.AddWithValue("$afp", m.ActiveFilePath ?? string.Empty);
            cmd.Parameters.AddWithValue("$fmc", m.FilesModifiedCount);
            cmd.Parameters.AddWithValue("$alc", m.AddedLinesCount);
            cmd.Parameters.AddWithValue("$rlc", m.RemovedLinesCount);
            cmd.Parameters.AddWithValue("$el", m.ElapsedSeconds);
            cmd.Parameters.AddWithValue("$cutc", ToIso(m.CreatedAtUtc));
            cmd.Parameters.AddWithValue("$uutc", ToIso(m.UpdatedAtUtc));
            cmd.Parameters.AddWithValue("$dts", m.DisplayTimestamp ?? string.Empty);
            cmd.ExecuteNonQuery();

            if (!string.IsNullOrWhiteSpace(m.Content))
            {
                UpsertFtsCore(conn, "Message", m.Id, m.ConversationId, projectKey, $"{m.SenderRole} {m.ModelName}".Trim(),
                    SecretRedactor.Redact(m.Content));
            }
        }

        private static void DeleteMemoriesByConversationCore(SqliteConnection conn, string conversationId)
        {
            using (var fts = conn.CreateCommand())
            {
                fts.CommandText = "DELETE FROM continuity_fts WHERE kind = 'Memory' AND conversation_id = $cid;";
                fts.Parameters.AddWithValue("$cid", conversationId);
                fts.ExecuteNonQuery();
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM memories WHERE conversation_id = $cid AND scope <> $global;";
            cmd.Parameters.AddWithValue("$cid", conversationId);
            cmd.Parameters.AddWithValue("$global", (int)MemoryScope.UserGlobal);
            cmd.ExecuteNonQuery();
        }

        private static void UpsertFtsCore(SqliteConnection conn, string kind, string entityId, string convId, string projectKey, string title, string body)
        {
            DeleteFtsCore(conn, kind, entityId);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO continuity_fts (kind, entity_id, conversation_id, project_key, title, body)
                                VALUES ($k, $eid, $cid, $pk, $t, $b);";
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$eid", entityId);
            cmd.Parameters.AddWithValue("$cid", convId ?? string.Empty);
            cmd.Parameters.AddWithValue("$pk", projectKey ?? string.Empty);
            cmd.Parameters.AddWithValue("$t", title ?? string.Empty);
            cmd.Parameters.AddWithValue("$b", body ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        private static void DeleteFtsCore(SqliteConnection conn, string kind, string entityId)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM continuity_fts WHERE kind = $k AND entity_id = $eid;";
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$eid", entityId);
            cmd.ExecuteNonQuery();
        }

        private static string BuildSafeFtsQuery(string raw)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in raw)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else if (sb.Length > 0)
                {
                    if (sb.Length >= 2) tokens.Add($"\"{sb}\"");
                    sb.Clear();
                }
            }
            if (sb.Length >= 2) tokens.Add($"\"{sb}\"");
            return string.Join(" OR ", tokens.Distinct(StringComparer.OrdinalIgnoreCase).Take(8));
        }

        private static ConversationRecord ReadConversation(SqliteDataReader r) => new()
        {
            Id = r.GetString(0),
            Title = r.GetString(1),
            ProjectKey = r.GetString(2),
            ProjectName = r.GetString(3),
            Status = (ConversationStatus)r.GetInt32(4),
            CreatedAtUtc = FromIso(r.GetString(5)),
            UpdatedAtUtc = FromIso(r.GetString(6)),
            LastActivityUtc = FromIso(r.GetString(7)),
            ActiveModelId = r.GetString(8),
            ActiveProvider = r.GetString(9),
            ParentConversationId = r.IsDBNull(10) ? null : r.GetString(10),
            IsArchived = r.GetInt32(11) == 1,
            IsDeleted = r.GetInt32(12) == 1,
            DeletedAtUtc = r.IsDBNull(13) ? null : FromIso(r.GetString(13)),
            MessageCount = r.GetInt32(14),
            LatestCheckpointSummary = r.IsDBNull(15) ? null : r.GetString(15)
        };

        private static ConversationMessageRecord ReadMessage(SqliteDataReader r) => new()
        {
            Id = r.GetString(0),
            ConversationId = r.GetString(1),
            TurnId = r.GetString(2),
            SequenceNumber = r.GetInt32(3),
            SenderRole = r.GetString(4),
            SenderName = r.GetString(5),
            Content = r.GetString(6),
            StructuredBlocksJson = r.GetString(7),
            AttachmentsJson = r.GetString(8),
            ToolStepsJson = r.GetString(9),
            ModelName = r.GetString(10),
            Provider = r.GetString(11),
            TurnStatus = (TurnLifecycleStatus)r.GetInt32(12),
            IsError = r.GetInt32(13) == 1,
            ActiveFilePath = r.GetString(14),
            FilesModifiedCount = r.GetInt32(15),
            AddedLinesCount = r.GetInt32(16),
            RemovedLinesCount = r.GetInt32(17),
            ElapsedSeconds = r.GetDouble(18),
            CreatedAtUtc = FromIso(r.GetString(19)),
            UpdatedAtUtc = FromIso(r.GetString(20)),
            DisplayTimestamp = r.GetString(21)
        };

        private static MemoryRecord ReadMemory(SqliteDataReader r) => new()
        {
            Id = r.GetString(0),
            Scope = (MemoryScope)r.GetInt32(1),
            ProjectKey = string.IsNullOrEmpty(r.GetString(2)) ? null : r.GetString(2),
            ConversationId = string.IsNullOrEmpty(r.GetString(3)) ? null : r.GetString(3),
            TurnId = string.IsNullOrEmpty(r.GetString(4)) ? null : r.GetString(4),
            Category = (MemoryCategory)r.GetInt32(5),
            Status = (MemoryLifecycleStatus)r.GetInt32(6),
            CanonicalKey = r.GetString(7),
            SupersededById = r.IsDBNull(8) ? null : r.GetString(8),
            Provenance = (MemoryProvenance)r.GetInt32(9),
            Source = r.GetString(10),
            SourceMessageId = r.IsDBNull(11) ? null : r.GetString(11),
            SourceEventId = r.IsDBNull(12) ? null : r.GetString(12),
            SourceFilePath = r.IsDBNull(13) ? null : r.GetString(13),
            Confidence = r.GetDouble(14),
            IsPinned = r.GetInt32(15) == 1,
            IsUserEditable = r.GetInt32(16) == 1,
            Content = r.GetString(17),
            TimestampUtc = FromIso(r.GetString(18)),
            UpdatedUtc = FromIso(r.GetString(19)),
            LastConfirmedUtc = FromIso(r.GetString(20)),
            ExpiresUtc = r.IsDBNull(21) ? null : FromIso(r.GetString(21))
        };

        private static ConversationRecord CloneConversation(ConversationRecord c) => new()
        {
            Id = c.Id, Title = c.Title, ProjectKey = c.ProjectKey, ProjectName = c.ProjectName,
            Status = c.Status, CreatedAtUtc = c.CreatedAtUtc, UpdatedAtUtc = c.UpdatedAtUtc,
            LastActivityUtc = c.LastActivityUtc, ActiveModelId = c.ActiveModelId, ActiveProvider = c.ActiveProvider,
            ParentConversationId = c.ParentConversationId, IsArchived = c.IsArchived, IsDeleted = c.IsDeleted,
            DeletedAtUtc = c.DeletedAtUtc, MessageCount = c.MessageCount, LatestCheckpointSummary = c.LatestCheckpointSummary
        };

        private static ConversationMessageRecord CloneMessage(ConversationMessageRecord m) => new()
        {
            Id = m.Id, ConversationId = m.ConversationId, TurnId = m.TurnId, SequenceNumber = m.SequenceNumber,
            SenderRole = m.SenderRole, SenderName = m.SenderName, Content = m.Content,
            StructuredBlocksJson = m.StructuredBlocksJson, AttachmentsJson = m.AttachmentsJson,
            ToolStepsJson = m.ToolStepsJson, ModelName = m.ModelName, Provider = m.Provider,
            TurnStatus = m.TurnStatus, IsError = m.IsError, ActiveFilePath = m.ActiveFilePath,
            FilesModifiedCount = m.FilesModifiedCount, AddedLinesCount = m.AddedLinesCount,
            RemovedLinesCount = m.RemovedLinesCount, ElapsedSeconds = m.ElapsedSeconds,
            CreatedAtUtc = m.CreatedAtUtc, UpdatedAtUtc = m.UpdatedAtUtc, DisplayTimestamp = m.DisplayTimestamp
        };

        private static TurnRecord CloneTurn(TurnRecord t) => new()
        {
            Id = t.Id, ConversationId = t.ConversationId, ProjectKey = t.ProjectKey,
            UserMessageId = t.UserMessageId, AssistantMessageId = t.AssistantMessageId,
            UserPrompt = t.UserPrompt, AssistantOutput = t.AssistantOutput, Status = t.Status,
            StartedAtUtc = t.StartedAtUtc, EndedAtUtc = t.EndedAtUtc, CheckpointSequence = t.CheckpointSequence,
            PromptTokensEstimate = t.PromptTokensEstimate, CompletionTokensEstimate = t.CompletionTokensEstimate,
            ErrorMessage = t.ErrorMessage
        };

        private static WorkEventRecord CloneWorkEvent(WorkEventRecord w) => new()
        {
            Id = w.Id, CorrelationId = w.CorrelationId, ConversationId = w.ConversationId, TurnId = w.TurnId,
            ProjectKey = w.ProjectKey, EventType = w.EventType, ToolName = w.ToolName, Summary = w.Summary,
            Detail = w.Detail, FilePath = w.FilePath, Status = w.Status, CreatedAtUtc = w.CreatedAtUtc
        };

        private static string ToIso(DateTime dt)
            => (dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture);

        private static DateTime FromIso(string s)
            => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToUniversalTime()
                : DateTime.UtcNow;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SqliteConnection.ClearAllPools();
        }
    }
}
