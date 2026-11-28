using System;
using System.Collections.Generic;
using System.IO;

namespace LlmodelBox.Runtime.Continuity
{
    public enum PersistenceState
    {
        Healthy = 0,
        PendingSave = 1,
        RecoveredFromBackup = 2,
        DegradedError = 3
    }

    public sealed class PersistenceDiagnosticReport
    {
        public PersistenceState State { get; set; } = PersistenceState.Healthy;
        public DateTime? LastSavedUtc { get; set; }
        public int PendingRetryCount { get; set; }
        public string LastWarning { get; set; } = string.Empty;
        public string LastError { get; set; } = string.Empty;
        public List<string> QuarantineBackups { get; set; } = new();
        public List<string> MigrationNotes { get; set; } = new();
        public int RecoveredInterruptedTurns { get; set; }
    }

    public sealed class MigrationSummaryReport
    {
        public bool Executed { get; set; }
        public int HomeThreadsImported { get; set; }
        public int ProjectTranscriptsImported { get; set; }
        public int ProjectMemoriesImported { get; set; }
        public int GlobalMemoriesImported { get; set; }
        public int MalformedFilesQuarantined { get; set; }
        public string BackupDirectory { get; set; } = string.Empty;
        public List<string> Warnings { get; set; } = new();
    }

    public sealed class IncompatibleSchemaException : Exception
    {
        public IncompatibleSchemaException(string message) : base(message) { }
    }

    public static class CorruptFileQuarantine
    {
        public static string? PreserveFile(string sourcePath, string backupRootDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    return null;

                string quarantineDir = Path.Combine(backupRootDir, "corrupt-quarantine");
                Directory.CreateDirectory(quarantineDir);
                string fileName = Path.GetFileName(sourcePath);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                string dest = Path.Combine(quarantineDir, $"{fileName}.{stamp}.bak");
                File.Copy(sourcePath, dest, overwrite: true);
                return dest;
            }
            catch
            {
                return null;
            }
        }
    }
}
