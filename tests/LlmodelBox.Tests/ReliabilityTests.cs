using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Common;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Knowledge;
using LlmodelBox.Runtime.Tools.Builtin;
using LlmodelBox.Services;
using Xunit;

namespace LlmodelBox.Tests;

public sealed class ReliabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmbox_regression_" + Guid.NewGuid().ToString("N"));
    public ReliabilityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private static AiToolCall Call(string json) => new() { Name = "write_file", ArgumentsJson = json };

    [Theory]
    [InlineData("{\"path\":\"keep.txt\"}")]
    [InlineData("{\"path\":\"keep.txt\",\"content\":null}")]
    [InlineData("{\"path\":\"keep.txt\",\"content\":7}")]
    public async Task MalformedWritePreservesExistingFile(string json)
    {
        string file = Path.Combine(_root, "keep.txt");
        File.WriteAllText(file, "keep me");
        Assert.True((await new WriteFileTool(_root).ExecuteAsync(Call(json), CancellationToken.None)).IsError);
        Assert.Equal("keep me", File.ReadAllText(file));
    }

    [Fact]
    public async Task ExplicitEmptyContentCanClearFile()
    {
        File.WriteAllText(Path.Combine(_root, "keep.txt"), "before");
        var result = await new WriteFileTool(_root).ExecuteAsync(Call("{\"path\":\"keep.txt\",\"content\":\"\"}"), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Empty(File.ReadAllText(Path.Combine(_root, "keep.txt")));
    }

    [Fact]
    public async Task CancelledWriteDoesNotCreateDirectories()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WriteFileTool(_root).ExecuteAsync(
            Call("{\"path\":\"new/file.txt\",\"content\":\"data\"}"), new CancellationToken(true)));
        Assert.False(Directory.Exists(Path.Combine(_root, "new")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("file.txt:stream")]
    public async Task UnsafePathsAreRejected(string path)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new WriteFileTool(_root).ExecuteAsync(
            Call(JsonSerializer.Serialize(new { path, content = "data" })), CancellationToken.None));
    }

    [Fact]
    public async Task Exactly500EntriesAreNotMarkedTruncated()
    {
        for (int i = 0; i < 500; i++) File.WriteAllText(Path.Combine(_root, $"file{i}.txt"), "");
        var tool = new ListDirectoryTool(_root);
        Assert.DoesNotContain("truncated", (await tool.ExecuteAsync(Call("{}"), CancellationToken.None)).Output);
        File.WriteAllText(Path.Combine(_root, "extra.txt"), "");
        Assert.Contains("truncated", (await tool.ExecuteAsync(Call("{}"), CancellationToken.None)).Output);
    }

    [Fact]
    public async Task OversizedReadIsRejected()
    {
        File.WriteAllBytes(Path.Combine(_root, "large.txt"), new byte[256 * 1024 + 1]);
        Assert.True((await new ReadFileTool(_root).ExecuteAsync(Call("{\"path\":\"large.txt\"}"), CancellationToken.None)).IsError);
    }

    [Fact]
    public void SettingsSaveCreatesParentAndLeavesNoTemporaryFiles()
    {
        string path = Path.Combine(_root, "nested", "settings.json");
        var service = new SettingsService(path);
        Assert.True(service.SaveSettings(new UserSettings()));
        Assert.True(service.SaveSettings(new UserSettings()));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void RenamedFileInvalidatesKnowledgeCache()
    {
        File.WriteAllText(Path.Combine(_root, "first.cs"), "unique searchable content");
        var engine = new KnowledgeEngine();
        var first = engine.Index(_root);
        File.Move(Path.Combine(_root, "first.cs"), Path.Combine(_root, "second.cs"));
        var second = engine.Index(_root);
        Assert.NotSame(first, second);
        Assert.Contains(engine.Retrieve(second, "searchable", 1), s => s.RelativePath == "second.cs");
    }

    [Fact]
    public void WorkspaceCreationRejectsTraversal()
    {
        var service = new WorkspaceService();
        Assert.False(service.CreateNewFile(_root, "../outside.txt"));
        Assert.False(service.CreateNewFolder(_root, "../outside"));
        Assert.True(service.CreateNewFile(_root, "nested/file.txt"));
    }

    [Fact]
    public void LargeFilePreviewIsBounded()
    {
        string path = Path.Combine(_root, "large.txt");
        File.WriteAllText(path, new string('x', 5 * 1024 * 1024));
        string preview = new WorkspaceService().ReadFileContent(path);
        Assert.StartsWith("[NOTICE:", preview);
        Assert.InRange(preview.Length, 100000, 100100);
    }

    [Fact]
    public async Task JunctionCannotEscapeToolsOrKnowledgeIndex()
    {
        string outside = Path.Combine(_root, "outside");
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "private marker");
        string junction = Path.Combine(workspace, "linked");
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{junction.Replace("'", "''")}' -Target '{outside.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ReadFileTool(workspace).ExecuteAsync(Call("{\"path\":\"linked/secret.txt\"}"), CancellationToken.None));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new WriteFileTool(workspace).ExecuteAsync(Call("{\"path\":\"linked/new.txt\",\"content\":\"blocked\"}"), CancellationToken.None));
            Assert.True(new KnowledgeEngine().Index(workspace).IsEmpty);
            Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
        }
        finally { Directory.Delete(junction); }
    }

    [Theory]
    [InlineData("https://github.com", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("relative.exe", false)]
    public void MarkdownLinksOnlyOpenWebUrls(string input, bool allowed)
        => Assert.Equal(allowed, WebLink.TryParse(input, out _));
}
