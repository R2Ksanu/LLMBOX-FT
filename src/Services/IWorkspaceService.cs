using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    public interface IWorkspaceService
    {
        List<ProjectFileItem> GetProjectFileTree(string projectRoot);
        string ReadFileContent(string filePath);
        bool SaveFileContent(string filePath, string content);
        bool CreateNewFile(string projectRoot, string relativePath);
        bool CreateNewFolder(string projectRoot, string relativePath);
        bool DeletePath(string projectRoot, string fullPath);
        bool RenamePath(string projectRoot, string fullPath, string newName, out string newFullPath);
        bool MovePath(string projectRoot, string fullPath, string destRelativeDir, out string newFullPath);
        string SaveUploadedImage(string projectRoot, string sourceFilePath);
        List<ArtifactItem> GetProjectArtifacts(string projectRoot);
        List<DiffLineItem> GenerateDiff(string originalContent, string newContent);
        Task RunCliCommandAsync(string command, string workingDir, Action<string> outputCallback, CancellationToken cancellationToken);
    }
}
