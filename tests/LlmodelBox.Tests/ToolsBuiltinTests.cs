using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Tools.Builtin;
using Xunit;

namespace LlmodelBox.Tests
{
    public class ToolsBuiltinTests
    {
        private static string NewWorkspace()
        {
            string dir = Path.Combine(Path.GetTempPath(), "llmbox_tools_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public async Task ReadFile_ReturnsContents()
        {
            string ws = NewWorkspace();
            File.WriteAllText(Path.Combine(ws, "a.txt"), "hello");
            var tool = new ReadFileTool(ws);

            var res = await tool.ExecuteAsync(new AiToolCall { Name = "read_file", ArgumentsJson = "{\"path\":\"a.txt\"}" }, CancellationToken.None);

            Assert.False(res.IsError);
            Assert.Equal("hello", res.Output);
        }

        [Fact]
        public async Task ReadFile_MissingFile_IsError()
        {
            var tool = new ReadFileTool(NewWorkspace());
            var res = await tool.ExecuteAsync(new AiToolCall { Name = "read_file", ArgumentsJson = "{\"path\":\"nope.txt\"}" }, CancellationToken.None);
            Assert.True(res.IsError);
        }

        [Fact]
        public async Task PathTraversal_IsRejected()
        {
            var tool = new ReadFileTool(NewWorkspace());
            await Assert.ThrowsAnyAsync<System.Exception>(() =>
                tool.ExecuteAsync(new AiToolCall { Name = "read_file", ArgumentsJson = "{\"path\":\"..\\\\..\\\\windows\\\\system32\\\\drivers\\\\etc\\\\hosts\"}" }, CancellationToken.None));
        }

        [Fact]
        public async Task WriteThenListAndRead_RoundTrips()
        {
            string ws = NewWorkspace();
            var write = new WriteFileTool(ws);
            var list = new ListDirectoryTool(ws);
            var read = new ReadFileTool(ws);

            var w = await write.ExecuteAsync(new AiToolCall { Name = "write_file", ArgumentsJson = "{\"path\":\"sub\\\\note.txt\",\"content\":\"data123\"}" }, CancellationToken.None);
            Assert.False(w.IsError);

            var l = await list.ExecuteAsync(new AiToolCall { Name = "list_directory", ArgumentsJson = "{\"path\":\".\"}" }, CancellationToken.None);
            Assert.Contains("sub", l.Output);

            var r = await read.ExecuteAsync(new AiToolCall { Name = "read_file", ArgumentsJson = "{\"path\":\"sub\\\\note.txt\"}" }, CancellationToken.None);
            Assert.Equal("data123", r.Output);
        }
    }
}
