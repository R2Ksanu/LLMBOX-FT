using System;
using System.IO;

namespace LlmodelBox.Common
{
    public static class AppConstants
    {
        public static readonly string PersistentUserRoot = ResolvePersistentUserRoot();
        public static readonly string DataDirectory = ResolveDataDirectory();
        public static readonly string ProjectsFilePath = Path.Combine(DataDirectory, "projects.json");
        public static readonly string ModelsFilePath = Path.Combine(DataDirectory, "models.json");
        public static readonly string SettingsFilePath = Path.Combine(DataDirectory, "settings.json");
        public static readonly string SecretVaultFilePath = ResolveSecretVaultPath();
        public static readonly string CacheDir = Path.Combine(DataDirectory, "cache", "logos");
        public static readonly string DefaultProjectsDir = Path.Combine(DataDirectory, "projects");
        public static readonly string ChatHistoryDir = Path.Combine(DataDirectory, "chat");

        private static string ResolvePersistentUserRoot()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = AppContext.BaseDirectory;
            }
            return Path.Combine(localAppData, "LlmodelBox");
        }

        private static string ResolveDataDirectory()
        {
            string? configured = Environment.GetEnvironmentVariable("LLMODELBOX_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

            // Source builds share the repository workspace root cleanly during development.
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "src", "LlmodelBox.csproj")))
                    return dir.FullName;
            }

            // Explicit opt-in portable folder beside the executable.
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.mode")))
            {
                return AppContext.BaseDirectory;
            }

            // Installed / standalone builds store settings, chat history, and projects in LocalAppData
            // so deleting or updating LlmodelBox.exe never wipes user configuration or API keys.
            MigrateLegacyInstallDataIfNeeded(AppContext.BaseDirectory, PersistentUserRoot);
            return PersistentUserRoot;
        }

        private static string ResolveSecretVaultPath()
        {
            string? configured = Environment.GetEnvironmentVariable("LLMODELBOX_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.Combine(Path.GetFullPath(configured), "secrets.dpapi");
            }
            return Path.Combine(PersistentUserRoot, "secrets.dpapi");
        }

        private static void MigrateLegacyInstallDataIfNeeded(string legacyBaseDir, string targetRoot)
        {
            try
            {
                string legacySettings = Path.Combine(legacyBaseDir, "settings.json");
                string targetSettings = Path.Combine(targetRoot, "settings.json");
                Directory.CreateDirectory(targetRoot);

                if (File.Exists(legacySettings) && !File.Exists(targetSettings))
                {
                    File.Copy(legacySettings, targetSettings, overwrite: false);
                }

                string legacyProjects = Path.Combine(legacyBaseDir, "projects.json");
                string targetProjects = Path.Combine(targetRoot, "projects.json");
                if (File.Exists(legacyProjects) && !File.Exists(targetProjects))
                {
                    File.Copy(legacyProjects, targetProjects, overwrite: false);
                }

                string legacyVault = Path.Combine(legacyBaseDir, "secrets.dpapi");
                string targetVault = Path.Combine(targetRoot, "secrets.dpapi");
                if (File.Exists(legacyVault) && !File.Exists(targetVault))
                {
                    File.Copy(legacyVault, targetVault, overwrite: false);
                }
            }
            catch { }
        }

        public static readonly string[] SuggestedProjectNames = new[]
        {
            "BlackBox-Orion",
            "Neural-Matrix-X",
            "CyberModel-Prime",
            "DeepCore-Engine",
            "VectorLab-Alpha",
            "QuantumBox-77",
            "Apex-Intelligence",
            "ZeroGravity-Lab",
            "Nexus-Model-01",
            "Synapse-Blackbox",
            "Titan-Inference",
            "OmniVision-Core"
        };
    }
}
