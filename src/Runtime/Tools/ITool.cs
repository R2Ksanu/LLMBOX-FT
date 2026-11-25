using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Runtime.Tools
{
    // How much trust a tool requires before it runs. The permission engine uses this to decide
    // whether a call can proceed automatically or must be approved by the user (Phase 5 / global
    // rule: dangerous tools need permission).
    public enum ToolRisk
    {
        Safe,       // read-only, no side effects, no sensitive data (e.g. list a directory)
        Sensitive,  // reads potentially private content (e.g. read an arbitrary file)
        Dangerous   // mutates state or the outside world (e.g. write/delete a file, run a command)
    }

    // An executable capability the agent loop can invoke on the model's behalf. Implementations must
    // be deterministic about their Risk and must never throw for expected failures — return an error
    // AiToolResult instead so the loop can feed it back to the model.
    public interface ITool
    {
        AiToolDefinition Definition { get; }
        ToolRisk Risk { get; }
        Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken);
    }
}
