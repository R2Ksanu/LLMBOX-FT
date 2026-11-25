using System.Collections.Generic;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Runtime.Tools
{
    // The set of tools available to the agent loop, addressable by name. Names are unique and
    // case-insensitive; registering the same name twice replaces the earlier tool.
    public interface IToolRegistry
    {
        void Register(ITool tool);
        bool TryGet(string name, out ITool tool);
        IReadOnlyCollection<ITool> All { get; }

        // The definitions to advertise to the model this turn.
        IReadOnlyList<AiToolDefinition> Definitions();
    }
}
