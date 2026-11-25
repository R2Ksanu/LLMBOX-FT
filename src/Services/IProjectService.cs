using System.Collections.Generic;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    public interface IProjectService
    {
        List<ProjectItem> LoadProjects();
        bool SaveProjects(List<ProjectItem> projects);
        ProjectItem CreateProject(string name, string folderPath, string description);
        string GetRandomSuggestedName(IEnumerable<ProjectItem>? existingProjects = null);
        void EnsureDefaultWorkspaceDirectory(string defaultDir);
    }
}
