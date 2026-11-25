using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LlmodelBox.Common;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    public class ProjectService : IProjectService
    {
        private readonly string _projectsFilePath;
        private readonly Random _random = new Random();

        public ProjectService(string? projectsFilePath = null)
        {
            _projectsFilePath = projectsFilePath ?? AppConstants.ProjectsFilePath;
        }

        public List<ProjectItem> LoadProjects()
        {
            var list = new List<ProjectItem>();
            try
            {
                if (File.Exists(_projectsFilePath))
                {
                    string json = File.ReadAllText(_projectsFilePath);
                    var loaded = JsonSerializer.Deserialize<List<ProjectItem>>(json);
                    if (loaded != null) list = loaded;
                }
            }
            catch { }

            return list;
        }

        public bool SaveProjects(List<ProjectItem> projects)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(projects, options);
                LlmodelBox.Common.AtomicFile.WriteAllText(_projectsFilePath, json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public ProjectItem CreateProject(string name, string folderPath, string description)
        {
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            var project = new ProjectItem
            {
                Name = name,
                Path = folderPath,
                Description = description,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                LastOpened = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            // Write initial project manifest file
            try
            {
                string metaPath = Path.Combine(folderPath, "project.json");
                var meta = new
                {
                    name = project.Name,
                    description = project.Description,
                    created = project.CreatedAt,
                    engine = "LlmodelBox BlackBox Engine",
                    pipeline = "Default Multi-Model Workspace"
                };
                LlmodelBox.Common.AtomicFile.WriteAllText(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }

            return project;
        }

        public string GetRandomSuggestedName(IEnumerable<ProjectItem>? existingProjects = null)
        {
            var existingNames = new HashSet<string>(
                (existingProjects ?? Enumerable.Empty<ProjectItem>()).Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase
            );

            var candidates = AppConstants.SuggestedProjectNames
                .Where(n => !existingNames.Contains(n))
                .ToList();

            if (candidates.Count > 0)
            {
                return candidates[_random.Next(candidates.Count)];
            }

            return $"BlackBox-Lab-{_random.Next(100, 999)}";
        }

        public void EnsureDefaultWorkspaceDirectory(string defaultDir)
        {
            if (!string.IsNullOrWhiteSpace(defaultDir) && !Directory.Exists(defaultDir))
            {
                try
                {
                    Directory.CreateDirectory(defaultDir);
                }
                catch { }
            }
        }
    }
}
