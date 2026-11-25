using System;
using System.IO;

namespace LlmodelBox.Common
{
    public static class AppConstants
    {
        public static readonly string DataDirectory = ResolveDataDirectory();
        public static readonly string ProjectsFilePath = Path.Combine(DataDirectory, "projects.json");
        public static readonly string ModelsFilePath = Path.Combine(DataDirectory, "models.json");
        public static readonly string SettingsFilePath = Path.Combine(DataDirectory, "settings.json");
        public static readonly string CacheDir = Path.Combine(DataDirectory, "cache", "logos");
        public static readonly string DefaultProjectsDir = Path.Combine(DataDirectory, "projects");
        public static readonly string ChatHistoryDir = Path.Combine(DataDirectory, "chat");

        private static string ResolveDataDirectory()
        {
            string? configured = Environment.GetEnvironmentVariable("LLMODELBOX_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
            // Source builds and the app/ distribution share the installation's existing data.
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "src", "LlmodelBox.csproj")))
                    return dir.FullName;
            return AppContext.BaseDirectory;
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
