using System;
using System.Net.Http;
using LlmodelBox.Infrastructure;
using LlmodelBox.Providers;
using LlmodelBox.Runtime;
using LlmodelBox.Runtime.Context;
using LlmodelBox.Runtime.Intent;
using LlmodelBox.Runtime.Knowledge;
using LlmodelBox.Runtime.Memory;
using LlmodelBox.Runtime.Middleware;
using LlmodelBox.Runtime.Tools;
using LlmodelBox.Services;

namespace LlmodelBox.Presentation
{
    /// <summary>
    /// Unified 4-Layer Application Composition Root:
    ///   Layer 1 -> ProviderGateway (adapters, router, capabilities, normalized errors)
    ///   Layer 2 -> MiddlewareContainer (15 independent AI middleware engines + AiRuntime)
    ///   Layer 3 -> Application & Infrastructure services (projects, workspaces, git, snapshots, runner, logging)
    ///   Layer 4 -> Presentation windows, controls & ViewModels consume these abstractions cleanly.
    /// </summary>
    public sealed class AppServiceHost
    {
        // Layer 1: Provider Gateway
        public IProviderGateway ProviderGateway { get; }

        // Layer 2: AI Intelligence Middleware
        public MiddlewareContainer Middleware { get; }
        public IAiRuntime Runtime => Middleware.Orchestrator;
        public ToolRegistry ToolRegistry => Middleware.ToolRegistry;

        // Layer 3: Application & Infrastructure
        public ICloudApiService CloudApi { get; }
        public ILogoService Logos { get; }
        public ISettingsService Settings { get; }
        public IProjectService Projects { get; }
        public IWorkspaceService Workspace { get; }
        public IModelLibraryService ModelLibrary { get; }
        public ITokenUsageService TokenUsage { get; }
        public IChatHistoryStore ChatHistoryStore { get; }
        public IMemoryStore MemoryStore { get; }
        public IFileSnapshotService FileSnapshots { get; }
        public IGitService Git { get; }
        public IBuildTestRunner BuildRunner { get; }
        public IAppLogger Logger { get; }

        public AppServiceHost(
            HttpClient httpClient,
            Func<ToolPermissionRequest, bool> toolApprover)
        {
            if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            if (toolApprover == null) throw new ArgumentNullException(nameof(toolApprover));

            // Layer 3 Infrastructure
            CloudApi = new CloudApiService(httpClient);
            Logos = new LogoService(httpClient);
            Settings = new SettingsService();
            Projects = new ProjectService();
            Workspace = new WorkspaceService();
            ModelLibrary = new ModelLibraryService();
            TokenUsage = new TokenUsageService();
            ChatHistoryStore = new JsonChatHistoryStore();
            MemoryStore = new JsonMemoryStore();
            FileSnapshots = new FileSnapshotService();
            Git = new GitService();
            BuildRunner = new BuildTestRunner();
            Logger = new AppLogger();

            // Layer 1 Provider Gateway
            var primaryAdapter = new CloudApiProvider(CloudApi);
            var gateway = new ProviderGateway(new IModelProvider[] { primaryAdapter }, ModelLibrary);
            ProviderGateway = gateway;

            // Layer 2 AI Intelligence Middleware
            var toolRegistry = new ToolRegistry();
            var permissions = new ToolPermissionEngine(toolApprover);
            var toolExecutor = new ToolExecutor(toolRegistry, permissions);
            var agentLoop = new AgentLoop(toolExecutor);
            var contextEngine = new ContextEngine();
            var memoryEngine = new MemoryEngine();
            var knowledgeEngine = new KnowledgeEngine();
            var intentEngine = new IntentEngine();
            var taskManager = new TaskManager();

            var runtime = new AiRuntime(
                gateway.Router,
                contextEngine,
                memoryEngine,
                toolRegistry,
                agentLoop,
                knowledgeEngine,
                intentEngine,
                taskManager);

            Middleware = new MiddlewareContainer
            {
                Orchestrator = runtime,
                Conversation = new ConversationEngine(),
                Intent = intentEngine,
                TaskPlanner = new TaskPlanningEngine(),
                Memory = memoryEngine,
                Context = contextEngine,
                TokenIntelligence = new TokenIntelligenceEngine(),
                ContextCompression = new ContextCompressionEngine(),
                Knowledge = knowledgeEngine,
                ToolRegistry = toolRegistry,
                ToolExecutor = toolExecutor,
                Permissions = permissions,
                ResponseNormalization = new ResponseNormalizationEngine(),
                Verification = new VerificationEngine(),
                Checkpoints = new CheckpointEngine(),
                RealtimeEvents = new RealtimeEventEngine()
            };
        }
    }
}
