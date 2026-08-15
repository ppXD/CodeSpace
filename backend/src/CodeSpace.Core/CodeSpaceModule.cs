using System.Reflection;
using Autofac;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Mediation;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Providers.Modules;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Plugins;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Core.Settings;
using CodeSpace.Core.Settings.Database;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace CodeSpace.Core;

public class CodeSpaceModule : Autofac.Module
{
    private readonly ILogger _logger;
    private readonly IConfiguration _configuration;
    private readonly Assembly[] _assemblies;

    /// <summary>
    /// <paramref name="assemblies"/> are the assemblies the mediator scans for handlers. It defaults to this one when
    /// none is passed, which is every caller today; it is a parameter so a host that composes an EXTRA assembly of
    /// handlers (a plugin pack, a test double set) states that at the call site rather than editing this constructor.
    /// </summary>
    public CodeSpaceModule(ILogger logger, IConfiguration configuration, params Assembly[] assemblies)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _assemblies = assemblies is { Length: > 0 } ? assemblies : new[] { typeof(CodeSpaceModule).Assembly };
    }

    protected override void Load(ContainerBuilder builder)
    {
        // Idempotent, and deliberately also done in Program.Main: Main covers the pre-host path (DbUp), this covers
        // every host that skips Main — notably WebApplicationFactory's in-memory test host.
        RuntimeSettings.Bind(_configuration);

        RegisterSettings(builder);
        RegisterMediator(builder);
        RegisterPersistence(builder);
        RegisterProviderModules(builder);
        RegisterStorageProviderModules(builder);
        RegisterPluginModules(builder);
        RegisterLLMProviderModules(builder);
        RegisterFirstPartyAgentTools(builder);
        RegisterDependency(builder);
        RegisterDecorators(builder);
        RegisterCurrentUser(builder);
        RegisterAmbient(builder);
        RegisterVariableEncryption(builder);
    }

    /// <summary>
    /// Singleton AES-GCM encryption for the unified <c>variable</c> subsystem.
    /// Master key from <c>Variables:MasterKey</c>, still honouring the legacy flat
    /// <c>CODESPACE_VARIABLE_MASTER_KEY</c> / <c>CODESPACE_TEAM_SECRET_MASTER_KEY</c> names every deployed pod sets.
    /// Dev fallback + WARN in Development; fail-fast everywhere else.
    /// </summary>
    private void RegisterVariableEncryption(ContainerBuilder builder)
    {
        // Both come through IConfiguration now, so a k8s Secret, a ConfigMap and the legacy flat environment name
        // are all the same thing to this code. RuntimeSettings.Bind ran at the top of Load, so the value is present.
        var configured = RuntimeSettings.Current.VariableMasterKey;

        var aspNetCoreEnv = _configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";
        var isDevelopment = string.Equals(aspNetCoreEnv, "Development", StringComparison.OrdinalIgnoreCase);

        byte[] masterKey;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            masterKey = Convert.FromBase64String(configured);
        }
        else if (isDevelopment)
        {
            Serilog.Log.Warning(
                "VariableEncryption: {Key} is not configured; using a DEVELOPMENT-ONLY fallback key. " +
                "Set it in any non-Development deployment.", "Variables:MasterKey");
            masterKey = new byte[32];
            for (int i = 0; i < 32; i++) masterKey[i] = (byte)i;
        }
        else
        {
            throw new InvalidOperationException(
                "Variables:MasterKey must be set to a base64-encoded 32-byte AES-256 key in non-Development " +
                "environments (the legacy CODESPACE_VARIABLE_MASTER_KEY name is still read). " +
                "Generate one with `openssl rand -base64 32`.");
        }

        var encryption = new Services.Variables.AesGcmVariableEncryption(masterKey);
        builder.RegisterInstance<Services.Variables.IVariableValueEncryption>(encryption).SingleInstance();
    }

    private static void RegisterAmbient(ContainerBuilder builder)
    {
        // TimeProvider — the OAuth state store + token refresh use this to compute
        // expiry; test fixtures swap it for a deterministic clock to make TTL tests reliable.
        builder.RegisterInstance(TimeProvider.System).As<TimeProvider>().SingleInstance();
    }

    private void RegisterSettings(ContainerBuilder builder)
    {
        var settingTypes = typeof(CodeSpaceModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IConfigurationSetting).IsAssignableFrom(t))
            .ToArray();

        builder.RegisterTypes(settingTypes).AsSelf().SingleInstance();
    }

    private void RegisterMediator(ContainerBuilder builder)
    {
        builder.RegisterModule(new MediatorModule(_assemblies));
    }

    private void RegisterPersistence(ContainerBuilder builder)
    {
        var connectionString = new CodeSpaceConnectionString(_configuration).Value;

        builder.Register(_ =>
            {
                var optionsBuilder = new DbContextOptionsBuilder<CodeSpaceDbContext>();

                optionsBuilder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();

                // The global User query filter (exclude bots) sits across a required TeamMembership→User
                // relationship; that's intentional (a bot membership's principal is filtered out of
                // human rosters), so silence EF's defensive warning about the interaction.
                optionsBuilder.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

                return optionsBuilder.Options;
            })
            .As<DbContextOptions<CodeSpaceDbContext>>()
            .SingleInstance();

        builder.Register(c =>
            {
                var options = c.Resolve<DbContextOptions<CodeSpaceDbContext>>();
                var currentUser = c.ResolveOptional<ICurrentUser>();
                var botVisibility = c.ResolveOptional<IBotVisibility>();
                return new CodeSpaceDbContext(options, currentUser, botVisibility);
            })
            .AsSelf()
            .As<DbContext>()
            .AsImplementedInterfaces()
            .InstancePerLifetimeScope();
    }

    private void RegisterProviderModules(ContainerBuilder builder)
    {
        var modules = typeof(CodeSpaceModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IProviderModule).IsAssignableFrom(t))
            .Select(t => (IProviderModule)Activator.CreateInstance(t)!)
            .ToList();

        foreach (var module in modules) RegisterProviderModule(builder, module);

        builder.Register(c => new ProviderModuleCatalog(modules)).As<IProviderModuleCatalog>().SingleInstance();
    }

    private static void RegisterProviderModule(ContainerBuilder builder, IProviderModule module)
    {
        foreach (var type in module.Capabilities) builder.RegisterType(type).AsSelf().AsImplementedInterfaces().InstancePerLifetimeScope();
        foreach (var type in module.AuthStrategies) builder.RegisterType(type).AsSelf().AsImplementedInterfaces().InstancePerLifetimeScope();
        foreach (var type in module.EventSubscriptions) builder.RegisterType(type).AsSelf().AsImplementedInterfaces().InstancePerLifetimeScope();
        foreach (var type in module.AuxiliaryServices) builder.RegisterType(type).AsSelf().SingleInstance();
    }

    /// <summary>
    /// Discovers immutable storage provider descriptors and their inert factory entry points. Catalog construction only
    /// validates descriptor/factory parity: it never creates a driver, resolves a profile/credential, or changes the
    /// existing singleton <c>IArtifactBlobBackend</c> path.
    /// </summary>
    private static void RegisterStorageProviderModules(ContainerBuilder builder)
    {
        var moduleTypes = typeof(CodeSpaceModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IStorageProviderModule).IsAssignableFrom(t))
            .ToArray();

        foreach (var type in moduleTypes) builder.RegisterType(type).As<IStorageProviderModule>().SingleInstance();
        builder.RegisterType<LocalRwxArtifactStorageDriverFactory>().As<IArtifactStorageDriverFactory>().SingleInstance();

        // Resolve both unions at container build time, not captured Core-assembly lists: an external Azure/OSS package
        // can contribute its module and factory from its own Autofac module without editing this central switch.
        builder.Register(c => new StorageProviderModuleCatalog(c.Resolve<IEnumerable<IStorageProviderModule>>())).As<IStorageProviderModuleCatalog>().SingleInstance().AutoActivate();
        builder.Register(c => new ArtifactStorageDriverFactoryCatalog(c.Resolve<IEnumerable<IArtifactStorageDriverFactory>>(), c.Resolve<IStorageProviderModuleCatalog>())).As<IArtifactStorageDriverFactoryCatalog>().SingleInstance().AutoActivate();
    }

    private void RegisterPluginModules(ContainerBuilder builder)
    {
        var modules = typeof(CodeSpaceModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IPluginModule).IsAssignableFrom(t))
            .Select(t => (IPluginModule)Activator.CreateInstance(t)!)
            .ToList();

        foreach (var module in modules) RegisterPluginModule(builder, module);

        builder.Register(c => new PluginModuleCatalog(modules)).As<IPluginModuleCatalog>().SingleInstance();

        // Registries that consume the union of every plugin's contributions.
        builder.RegisterType<NodeRegistry>().As<INodeRegistry>().SingleInstance();
        builder.RegisterType<RunSourceMatcherRegistry>().As<IRunSourceMatcherRegistry>().SingleInstance();
    }

    private static void RegisterPluginModule(ContainerBuilder builder, IPluginModule module)
    {
        // Nodes + matchers are singletons — stateless, manifest cached. Auxiliary services
        // pick their lifetime via IDependency markers (RegisterDependency below).
        foreach (var type in module.Nodes) builder.RegisterType(type).AsSelf().AsImplementedInterfaces().SingleInstance();
        foreach (var type in module.RunSourceMatchers) builder.RegisterType(type).AsSelf().AsImplementedInterfaces().SingleInstance();
        foreach (var type in module.AuxiliaryServices) builder.RegisterType(type).AsSelf().SingleInstance();
    }

    private void RegisterLLMProviderModules(ContainerBuilder builder)
    {
        var modules = typeof(CodeSpaceModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ILLMProviderModule).IsAssignableFrom(t))
            .Select(t => (ILLMProviderModule)Activator.CreateInstance(t)!)
            .ToList();

        foreach (var module in modules)
        {
            builder.RegisterType(module.Client).AsSelf().AsImplementedInterfaces().SingleInstance();
            foreach (var aux in module.AuxiliaryServices) builder.RegisterType(aux).AsSelf().SingleInstance();
        }

        builder.RegisterType<LLMClientRegistry>().As<ILLMClientRegistry>().SingleInstance();
    }

    /// <summary>
    /// Registers the FIRST-PARTY agent tools that are not workflow nodes (Rule 18.3 — an MCP-native capability, not a
    /// graph step).
    /// <list type="bullet">
    ///   <item><see cref="Services.Agents.Tools.GetContextTool"/> — a READ-ONLY context-retrieval tool. Registered
    ///   UNCONDITIONALLY: it touches neither the ledger nor the approval surface, so it has no governance dependency and
    ///   is useful in any run whose MCP endpoint is open (the endpoint gate is separate from governance). A run with no
    ///   session simply gets a clean "nothing to retrieve".</item>
    ///   <item><see cref="Services.Agents.Tools.DecisionRequestTool"/> (Decision substrate D2) — needs the ledger +
    ///   approval surface, which exist whenever governance does. That used to be an environment flag read here, so the
    ///   tool could be absent from the DI <c>IEnumerable&lt;IAgentTool&gt;</c> and missing from <c>tools/list</c>
    ///   depending on a deployment variable; governance is now a committed constant
    ///   (<see cref="Services.Agents.Mcp.McpRequestHandler.GovernanceEnabled"/>), so the catalog is the same
    ///   everywhere.</item>
    /// </list>
    /// </summary>
    private void RegisterFirstPartyAgentTools(ContainerBuilder builder)
    {
        builder.RegisterType<Services.Agents.Tools.GetContextTool>().As<Services.Agents.Tools.IAgentTool>().SingleInstance();
        builder.RegisterType<Services.Agents.Tools.DecisionRequestTool>().As<Services.Agents.Tools.IAgentTool>().SingleInstance();
    }

    /// <summary>
    /// Decorators wrap a convention-registered service AFTER <see cref="RegisterDependency"/> has registered the
    /// implementation. The critic planner decorator wraps <c>IWorkflowPlanner</c> with the generic adversarial-review
    /// primitive — inert by default (per-request <c>ReviewMode.None</c>), so an
    /// unconfigured plan is byte-identical to the bare planner.
    /// </summary>
    private static void RegisterDecorators(ContainerBuilder builder)
    {
        builder.RegisterDecorator<Services.Workflows.Planning.Planners.CriticPlannerDecorator, Services.Workflows.Planning.IWorkflowPlanner>();

        // Retry sits INSIDE the critic (registered first ⇒ innermost), so a transient brain-call blip self-heals before
        // the critic ever reviews a decision and the retry budget covers exactly the brain call (not the critic's review).
        builder.RegisterInstance(Services.Supervisor.Deciders.SupervisorDecisionRetryOptions.FromEnvironment());
        builder.RegisterDecorator<Services.Supervisor.Deciders.RetryingSupervisorDeciderDecorator, Services.Supervisor.ISupervisorDecider>();
        builder.RegisterDecorator<Services.Supervisor.Deciders.CriticSupervisorDeciderDecorator, Services.Supervisor.ISupervisorDecider>();

        // Side-channel: record every in-process model call (prompt/completion/usage) onto the run ledger as
        // interaction.* — captures the supervisor brain's reasoning that was discarded, generically at the client seam.
        // Over ILLMClient (the interface the registry holds + the faces callers cast to). THREE decorators on
        // MUTUALLY-EXCLUSIVE conditions so the wrapped type MIRRORS the inner's faces: a structured+streaming client gets
        // the full-face decorator (stays IStructuredLLMClient AND IStreamingLLMClient — so BOTH the decider's
        // OfType<IStructuredLLMClient>() AND a streaming caller's OfType<IStreamingLLMClient>() land on the recorder, never
        // bypassing capture); a structured-only client gets the structured decorator; a plain-text-only client gets the
        // narrow one (stays non-structured — the merge synthesis's `is not IStructuredLLMClient` text-provider pick still
        // finds it). One decorator implementing faces unconditionally lied about the narrower clients. (A hypothetical
        // streaming-only, non-structured client would fall to the narrow branch and lose the streaming face — no such
        // client exists; adding one is a deliberate act that would add a fourth branch + its test.)
        builder.RegisterDecorator<Services.Workflows.Llm.RecordingStreamingStructuredLLMClientDecorator, Services.Workflows.Llm.ILLMClient>(context => context.CurrentInstance is Services.Workflows.Llm.IStructuredLLMClient && context.CurrentInstance is Services.Workflows.Llm.IStreamingLLMClient);
        builder.RegisterDecorator<Services.Workflows.Llm.RecordingStructuredLLMClientDecorator, Services.Workflows.Llm.ILLMClient>(context => context.CurrentInstance is Services.Workflows.Llm.IStructuredLLMClient && context.CurrentInstance is not Services.Workflows.Llm.IStreamingLLMClient);
        builder.RegisterDecorator<Services.Workflows.Llm.RecordingLLMClientDecorator, Services.Workflows.Llm.ILLMClient>(context => context.CurrentInstance is not Services.Workflows.Llm.IStructuredLLMClient);
    }

    private void RegisterDependency(ContainerBuilder builder)
    {
        foreach (var type in typeof(IDependency).Assembly.GetTypes()
                     .Where(t => t.IsClass && !t.IsAbstract && typeof(IDependency).IsAssignableFrom(t)))
        {
            if (typeof(IScopedDependency).IsAssignableFrom(type))
                builder.RegisterType(type).AsSelf().AsImplementedInterfaces().InstancePerLifetimeScope();
            else if (typeof(ISingletonDependency).IsAssignableFrom(type))
                builder.RegisterType(type).AsSelf().AsImplementedInterfaces().SingleInstance();
            else if (typeof(ITransientDependency).IsAssignableFrom(type))
                builder.RegisterType(type).AsSelf().AsImplementedInterfaces().InstancePerDependency();
            else
                builder.RegisterType(type).AsSelf().AsImplementedInterfaces();
        }
    }

    private void RegisterCurrentUser(ContainerBuilder builder)
    {
        builder.Register<ICurrentUser>(c =>
        {
            var accessor = c.ResolveOptional<IHttpContextAccessor>();

            if (accessor?.HttpContext != null) return c.Resolve<ApiUser>();

            // No HTTP context → background work (Hangfire workers, scheduled jobs, DbUp).
            // BackgroundSeederUser holds the Admin role so tenancy bypass works the same way
            // as the seeded system user does for human admins.
            return new BackgroundSeederUser();
        }).InstancePerLifetimeScope();
    }
}
