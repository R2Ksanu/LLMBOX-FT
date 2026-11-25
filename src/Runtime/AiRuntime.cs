using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models.Ai;
using LlmodelBox.Providers;
using LlmodelBox.Runtime.Context;
using LlmodelBox.Runtime.Intent;
using LlmodelBox.Runtime.Knowledge;
using LlmodelBox.Runtime.Memory;
using LlmodelBox.Runtime.Tools;
using LlmodelBox.Services;

namespace LlmodelBox.Runtime
{
    // The runtime entry point. Today it delegates to the ModelRouter (which reaches the provider
    // API — the heart). Later phases insert context building, memory, tool loops, planning and
    // verification here, WITHOUT the UI or providers needing to change: the UI always calls this,
    // and providers always expose the same normalized contract.
    //
    // Provider-agnostic observability: the runtime mirrors the lifecycle of every request onto the
    // AgentActivityBus (dispatch → reasoning → error), regardless of which provider serves it, so
    // the Neural Net view reflects runtime-level activity even for call sites that do not emit their
    // own signals.
    public sealed class AiRuntime : IAiRuntime
    {
        // Reasoning can be arbitrarily long; the bus detail line is a short human-readable summary.
        private const int ReasoningSnippetMax = 160;

        private readonly IModelRouter _router;
        private readonly IContextEngine _contextEngine;
        private readonly IMemoryEngine _memory;
        private readonly IToolRegistry _tools;
        private readonly IAgentLoop _agentLoop;
        private readonly IKnowledgeEngine? _knowledge;
        private readonly IIntentEngine? _intent;
        private readonly TaskManager? _taskManager;

        // Max chars of retrieved project knowledge to fold into a single turn, and how many snippets.
        private const int KnowledgeBlockChars = 6000;
        private const int KnowledgeSnippets = 4;

        // Backward-compatible: existing call sites (Phase 1/2) construct with just the router and get
        // the default Context + Memory engines.
        public AiRuntime(IModelRouter router) : this(router, new ContextEngine(), new MemoryEngine()) { }

        public AiRuntime(IModelRouter router, IContextEngine contextEngine)
            : this(router, contextEngine, new MemoryEngine()) { }

        public AiRuntime(IModelRouter router, IContextEngine contextEngine, IMemoryEngine memory)
            : this(router, contextEngine, memory, new ToolRegistry(), null) { }

        // Phase 5: an optional tool registry + agent loop. With an empty registry (or a model that
        // can't call tools) this behaves exactly as before — a single routed turn.
        public AiRuntime(IModelRouter router, IContextEngine contextEngine, IMemoryEngine memory,
            IToolRegistry tools, IAgentLoop? agentLoop)
            : this(router, contextEngine, memory, tools, agentLoop, null) { }

        // Phase 6: an optional Knowledge Engine. When supplied (and a project root is known), the
        // runtime retrieves relevant project excerpts and folds them into the Context Engine's budget.
        // A null engine disables retrieval — every earlier phase behaves identically.
        public AiRuntime(IModelRouter router, IContextEngine contextEngine, IMemoryEngine memory,
            IToolRegistry tools, IAgentLoop? agentLoop, IKnowledgeEngine? knowledge)
            : this(router, contextEngine, memory, tools, agentLoop, knowledge, null, null) { }

        // Phase 7: an optional Intent + Task Engine. When supplied, the runtime classifies each turn to
        // gate tools/knowledge (a "hi" no longer indexes the repo) and records a task lifecycle on the
        // AgentActivityBus. A null intent engine keeps the permissive Phase-6 behaviour unchanged.
        public AiRuntime(IModelRouter router, IContextEngine contextEngine, IMemoryEngine memory,
            IToolRegistry tools, IAgentLoop? agentLoop, IKnowledgeEngine? knowledge,
            IIntentEngine? intent, TaskManager? taskManager)
        {
            _router = router;
            _contextEngine = contextEngine;
            _memory = memory;
            _tools = tools ?? new ToolRegistry();
            _agentLoop = agentLoop ?? new Tools.AgentLoop(new ToolExecutor(_tools, new ToolPermissionEngine()));
            _knowledge = knowledge;
            _intent = intent;
            _taskManager = taskManager;
        }

        public async Task<AiResponse> RunAsync(AiRequest request, CancellationToken cancellationToken)
        {
            AgentActivityBus.Emit(AgentNode.Model, "active", "active");

            var response = await _router.RouteAsync(request, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(response.Reasoning))
                AgentActivityBus.Emit(AgentNode.Reasoning, Truncate(response.Reasoning!, ReasoningSnippetMax));

            if (response.IsError)
                AgentActivityBus.Emit(AgentNode.Response, response.ErrorMessage ?? response.Text, "error");

            return response;
        }

        // Phase 3 entry point: resolve the target model's capabilities, let the Context Engine assemble
        // a budgeted request, then route it. The input-token estimate rides back on the response so the
        // UI can do token accounting without re-assembling or re-counting the prompt.
        public async Task<AiResponse> RunAsync(ContextRequest request, CancellationToken cancellationToken)
        {
            // Phase 7: classify the turn up front. The verdict gates knowledge/tools below and drives a
            // real task lifecycle on the bus. With no intent engine, both gates stay open (Phase-6 path).
            IntentResult? intent = null;
            RuntimeTask? task = null;
            bool wantsKnowledge = true, wantsTools = true;
            if (_intent != null)
            {
                intent = _intent.Classify(request.UserPrompt);
                wantsKnowledge = intent.WantsKnowledge;
                wantsTools = intent.WantsTools;

                AgentActivityBus.Emit(AgentNode.Prompt,
                    $"Intent: {intent.Kind} ({intent.Confidence:P0}) · {intent.Tasks.Count} task(s)");

                if (_taskManager != null && intent.Tasks.Count > 0)
                {
                    task = _taskManager.Create(intent.Tasks[0]);
                    _taskManager.UpdateState(task.Id, AiTaskState.Planning);
                    for (int i = 0; i < intent.Tasks.Count; i++)
                        AgentActivityBus.Emit(AgentNode.Prompt, $"Task {i + 1}/{intent.Tasks.Count}: {intent.Tasks[i]}");
                    _taskManager.UpdateState(task.Id, AiTaskState.Executing);
                }
            }

            // Recall durable memory and let the Context Engine fold it into the live turn prefix. We
            // only supply a preamble when the caller has not already set one.
            if (string.IsNullOrWhiteSpace(request.MemoryPreamble))
            {
                var snapshot = _memory.Recall(request.ProjectKey);
                request.MemoryPreamble = _memory.BuildPreamble(snapshot);
            }

            // Phase 6: retrieval-augmented context. When a Knowledge Engine is present and the project
            // key names a real directory, index it and fold the most relevant excerpts into the turn.
            // Best-effort and deterministic: indexing/retrieval failures never break the request. Phase 7
            // suppresses retrieval for turns the Intent Engine judged not to need it (e.g. small talk).
            if (_knowledge != null && wantsKnowledge && string.IsNullOrWhiteSpace(request.KnowledgeBlock)
                && !string.IsNullOrWhiteSpace(request.UserPrompt))
            {
                try
                {
                    var index = _knowledge.Index(request.ProjectKey);
                    if (!index.IsEmpty)
                    {
                        var snippets = _knowledge.Retrieve(index, request.UserPrompt, KnowledgeSnippets);
                        var block = _knowledge.BuildKnowledgeBlock(snippets, KnowledgeBlockChars);
                        if (!string.IsNullOrWhiteSpace(block))
                        {
                            request.KnowledgeBlock = block;
                            AgentActivityBus.Emit(AgentNode.Context,
                                $"Knowledge: {snippets.Count} excerpt(s) from {index.DocumentCount} indexed chunk(s)");
                        }
                    }
                }
                catch { /* retrieval is best-effort; never fail the turn on it */ }
            }

            var probe = new AiRequest { Model = request.Model, Settings = request.Settings };
            probe.Messages.Add(AiMessage.User(request.UserPrompt ?? string.Empty));

            var provider = _router.Select(probe);
            var caps = SafeCapabilities(provider, request);

            var built = _contextEngine.Build(request, caps);

            // Phase 5: advertise tools and drive the agent loop only when the model can call them and
            // at least one tool is registered. Phase 7 additionally suppresses tools for turns the
            // Intent Engine judged tool-free (e.g. small talk / pure questions).
            var toolDefs = _tools.Definitions();
            bool useTools = caps.ToolCalling && toolDefs.Count > 0 && wantsTools;

            AiResponse response;
            if (useTools)
            {
                built.Request.Tools = new List<AiToolDefinition>(toolDefs);
                response = await _agentLoop.RunAsync(built.Request, RunAsync, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                response = await RunAsync(built.Request, cancellationToken).ConfigureAwait(false);
            }

            response.PromptTokensEstimate = built.InputTokenEstimate;

            // Distil this exchange into durable memory — cheap, deterministic, best-effort. Never let a
            // memory write failure surface into the turn.
            if (!response.IsError)
            {
                try { _memory.Observe(request.ProjectKey, request.UserPrompt ?? string.Empty, response.Text); }
                catch { /* best-effort */ }
            }

            // Phase 7: close out the task lifecycle so the tracked state reflects the real outcome.
            if (task != null && _taskManager != null)
                _taskManager.UpdateState(task.Id, response.IsError ? AiTaskState.Failed : AiTaskState.Completed);

            return response;
        }

        private static ModelCapabilities SafeCapabilities(IModelProvider provider, ContextRequest request)
        {
            try { return provider.GetCapabilities(request.Model); }
            catch { return ModelCapabilities.Default(); }
        }

        public IAsyncEnumerable<AiEvent> StreamAsync(AiRequest request, CancellationToken cancellationToken)
            => _router.RouteStreamAsync(request, cancellationToken);

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
