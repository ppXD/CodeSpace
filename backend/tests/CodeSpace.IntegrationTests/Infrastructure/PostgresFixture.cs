using Autofac;
using Autofac.Extensions.DependencyInjection;
using CodeSpace.Core;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Settings;
using CodeSpace.IntegrationTests.Settings;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Serilog;
using Serilog.Extensions.Logging;

namespace CodeSpace.IntegrationTests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly IConfiguration _testConfig;
    private readonly string _adminConnectionString;
    private readonly string _testDatabaseName;

    private Autofac.IContainer? _ioc;

    public PostgresFixture()
    {
        _testConfig = BuildTestConfiguration();

        _adminConnectionString = new TestPostgresAdminConnectionString(_testConfig).Value;

        var prefix = new TestDatabaseNamePrefix(_testConfig).Value;
        _testDatabaseName = $"{prefix}{Guid.NewGuid():N}";
    }

    public string ConnectionString { get; private set; } = default!;

    public ILifetimeScope BeginScope() => _ioc!.BeginLifetimeScope();

    /// <summary>
    /// Begins a child scope with a per-test override callback — typically used to swap
    /// <see cref="CodeSpace.Core.Services.Identity.ICurrentUser"/> for tenancy tests so the
    /// caller can simulate "user in team" vs "user not in team" cases.
    /// </summary>
    public ILifetimeScope BeginScope(Action<ContainerBuilder> configure) => _ioc!.BeginLifetimeScope(configure);

    /// <summary>
    /// Convenience: opens a scope with the given user + team + roles injected as
    /// ICurrentUser and ICurrentTeam. Pass null for either to omit (anonymous or no team).
    /// Pass Roles.Admin to bypass tenancy without seeding TeamMembership rows.
    /// </summary>
    public ILifetimeScope BeginScopeAs(Guid? userId, Guid? teamId, params string[] roles)
    {
        return BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "test", roles)).As<CodeSpace.Core.Services.Identity.ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<CodeSpace.Core.Services.Identity.ICurrentTeam>().SingleInstance();
        });
    }

    public async Task InitializeAsync()
    {
        await CreateTestDatabaseAsync().ConfigureAwait(false);

        ConnectionString = BuildTestConnectionString();

        new DbUpRunner(ConnectionString).Run();

        // TeamSecretEncryption normally reads CODESPACE_TEAM_SECRET_MASTER_KEY at startup
        // and fail-fasts when missing in non-Development. The integration suite needs a
        // stable key so encrypted-at-rest assertions can decrypt across fixture rebuilds —
        // pin a deterministic 32-byte test key here. Production deploys set this env var
        // via secret-store; tests set it programmatically.
        const string testMasterKeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        Environment.SetEnvironmentVariable("CODESPACE_TEAM_SECRET_MASTER_KEY", testMasterKeyBase64);

        _ioc = BuildIocContainer(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        _ioc?.Dispose();
        await DropTestDatabaseAsync().ConfigureAwait(false);
    }

    private static IConfiguration BuildTestConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    private async Task CreateTestDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_testDatabaseName}\"", conn);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task DropTestDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var killCmd = new NpgsqlCommand(
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_testDatabaseName}' AND pid <> pg_backend_pid()",
            conn);
        await killCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        await using var dropCmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_testDatabaseName}\"", conn);
        await dropCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string BuildTestConnectionString()
    {
        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = _testDatabaseName };
        return builder.ConnectionString;
    }

    private Autofac.IContainer BuildIocContainer(string connectionString)
    {
        var logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

        var configuration = new ConfigurationBuilder()
            .AddConfiguration(_testConfig)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeSpaceStore:ConnectionString"] = connectionString,
                ["Authentication:Jwt:SymmetricKey"] = "test-only-key-do-not-use-in-prod-minimum-32-chars",
                ["OAuth:CallbackUrl"] = "http://localhost:5099/api/credentials/oauth/callback"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(logger));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDataProtection();
        // HeaderCurrentTeam takes IHttpContextAccessor; tests have no real HttpContext so this
        // returns a singleton with null HttpContext, and HeaderCurrentTeam.Id becomes null —
        // tenancy behaviors then throw "X-Team-Id missing" unless the test overrides ICurrentTeam.
        services.AddHttpContextAccessor();
        // GitHubOAuthClient + GitLabOAuthClient take IHttpClientFactory; register it so the
        // singletons can be constructed even when tests don't hit the live HTTP path.
        services.AddHttpClient();

        var builder = new ContainerBuilder();
        builder.Populate(services);
        builder.RegisterModule(new CodeSpaceModule(logger, configuration));

        RegisterTestAssemblyTypes(builder);

        return builder.Build();
    }

    private static void RegisterTestAssemblyTypes(ContainerBuilder builder)
    {
        var testAssembly = typeof(PostgresFixture).Assembly;

        builder.RegisterAssemblyTypes(testAssembly)
            .AsClosedTypesOf(typeof(IRequestHandler<,>))
            .InstancePerLifetimeScope();

        builder.RegisterAssemblyTypes(testAssembly)
            .AsClosedTypesOf(typeof(INotificationHandler<>))
            .InstancePerLifetimeScope();

        builder.RegisterAssemblyTypes(testAssembly)
            .Where(t => t.IsClass && !t.IsAbstract && typeof(CodeSpace.Core.Services.Providers.Capabilities.IProviderCapability).IsAssignableFrom(t))
            .AsImplementedInterfaces()
            .InstancePerLifetimeScope();

        builder.RegisterAssemblyTypes(testAssembly)
            .Where(t => t.IsClass && !t.IsAbstract && typeof(CodeSpace.Core.Services.Providers.Events.IProviderEventSubscription).IsAssignableFrom(t))
            .AsImplementedInterfaces()
            .InstancePerLifetimeScope();

        var settingTypes = testAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IConfigurationSetting).IsAssignableFrom(t))
            .ToArray();

        builder.RegisterTypes(settingTypes).AsSelf().SingleInstance();

        builder.RegisterType<Webhooks.CapturedNormalizedEvents>().AsSelf().SingleInstance();

        // In-memory store backing TestRepositoryProvider's remote webhook lookups. Singleton
        // per fixture (= per test DB) so tests within one fixture share state (e.g. assert
        // "registrar called exactly once" via RegisterCallCount across multiple operations)
        // but cross-fixture leak is impossible.
        builder.RegisterType<Binding.TestRemoteHookStore>().AsSelf().SingleInstance();

        // ICodeSpaceBackgroundJobClient test impl. Records Enqueue calls + lets tests simulate
        // Hangfire failure via ThrowOnEnqueue. SingleInstance so tests can assert the
        // recorded call list across fixture scopes.
        builder.RegisterType<Jobs.InMemoryBackgroundJobClient>()
            .As<CodeSpace.Core.Services.Jobs.ICodeSpaceBackgroundJobClient>()
            .AsSelf()
            .SingleInstance();

        // Test-only INodeRuntime for the retry-flow tests — a node that fails a deterministic
        // number of times then succeeds. Registered as INodeRuntime so NodeRegistry picks it up
        // (engine + validator accept "test.flaky"); it is NOT an IPluginModule node, so it stays
        // out of the editor palette / node-manifest list.
        builder.RegisterType<Workflows.Infrastructure.FlakyTestNode>()
            .As<CodeSpace.Core.Services.Workflows.Nodes.INodeRuntime>()
            .SingleInstance();

        // Test-only loop body node — records the loop.* scope it sees each iteration. Same
        // registration rationale as FlakyTestNode: engine/validator see it, palette doesn't.
        builder.RegisterType<Workflows.Infrastructure.LoopProbeNode>()
            .As<CodeSpace.Core.Services.Workflows.Nodes.INodeRuntime>()
            .SingleInstance();

        // Test-only node that barriers on a shared CountdownEvent to PROVE a ready frontier ran
        // concurrently (a sequential walk would time out at the barrier). Same registration rationale.
        builder.RegisterType<Workflows.Infrastructure.ConcurrencyProbeNode>()
            .As<CodeSpace.Core.Services.Workflows.Nodes.INodeRuntime>()
            .SingleInstance();

        // Test-only synchronous node that echoes its inputs as outputs (the planner bridge + per-element
        // map transform). Stateless ⇒ thread-safe under the map fan-out. Same registration rationale.
        builder.RegisterType<Workflows.Infrastructure.JsonEmitNode>()
            .As<CodeSpace.Core.Services.Workflows.Nodes.INodeRuntime>()
            .SingleInstance();

        // Test-only SUSPENDING body node for the flow.map durable parallel-branch resume (PR2) tests — the
        // hermetic stand-in for an agent.code that parks to an AgentRun (parks an Action wait, no external
        // staging). Same registration rationale: engine/validator see it, palette doesn't.
        builder.RegisterType<Workflows.Infrastructure.SuspendProbeNode>()
            .As<CodeSpace.Core.Services.Workflows.Nodes.INodeRuntime>()
            .SingleInstance();

        // Deterministic structured-output LLM client for the headline-flow E2E's planner half. Registered as
        // BOTH ILLMClient + IStructuredLLMClient under its OWN provider tag (DeterministicPlannerLlmClient.
        // ProviderTag), so the LLMClientRegistry holds it ALONGSIDE the real Anthropic client (no duplicate-
        // provider collision) and llm.complete(provider=that tag) routes through the real structured path to
        // a fixed { subtasks: [...] } object — the honest IStructuredLLMClient-boundary fake.
        builder.RegisterType<Workflows.Infrastructure.DeterministicPlannerLlmClient>()
            .As<CodeSpace.Core.Services.Workflows.Llm.ILLMClient>()
            .As<CodeSpace.Core.Services.Workflows.Llm.IStructuredLLMClient>()
            .SingleInstance();

        // Planner-shaped structured fake for the task-first planner E2E (PR-D Slice 1). Under its OWN provider
        // tag, so the projected graph's llm.complete body + synthesizer nodes (which hold the ROOT registry)
        // can resolve it once the test retargets their provider — letting the planned flow fan out + synthesize
        // with no API key.
        builder.RegisterType<Workflows.Infrastructure.DeterministicTaskPlannerLlmClient>()
            .As<CodeSpace.Core.Services.Workflows.Llm.ILLMClient>()
            .As<CodeSpace.Core.Services.Workflows.Llm.IStructuredLLMClient>()
            .SingleInstance();

        // Coordinated (L3 checkpoint-coordinator) structured fake for PR-D.5. ONE shared SingleInstance plays
        // both roles in a run: the planner (schema has `subtasks`) and the coordinator (schema has `decision`,
        // alternating rework→done by round). Registered at root so the planning child scope AND the engine's
        // retargeted llm.complete nodes resolve the SAME instance — the coordinator's round counter is shared
        // across the two engine rounds. The single coordinated test Reset()s the counter at its start.
        builder.RegisterType<Workflows.Infrastructure.DeterministicCoordinatedLlmClient>()
            .As<CodeSpace.Core.Services.Workflows.Llm.ILLMClient>()
            .As<CodeSpace.Core.Services.Workflows.Llm.IStructuredLLMClient>()
            .SingleInstance();

        // S0a real-model phase-authorship instrument: a RECORD/REPLAY decorator over the REAL AnthropicClient,
        // under its OWN provider tag (RecordReplayStructuredLLMClient.ProviderTag), so the LLMClientRegistry holds
        // it alongside the real client + the deterministic fakes without a duplicate-provider collision. The
        // RealModelPhaseAuthorshipFlowTests retarget the planner node at this tag; every OTHER test ignores it.
        // Mode is decided per-fixture: RECORD when a real API key is present (the inner real client can reach a
        // model + the transcript is written), else REPLAY from the committed cassette. Construction is cheap +
        // never calls a model, so registering it for all fixtures is harmless — it only delegates on a real call.
        builder.Register(c =>
            {
                var inner = c.Resolve<CodeSpace.Core.Services.Workflows.Llm.Anthropic.AnthropicClient>();
                var cassettePath = Workflows.Infrastructure.RealModelCassettePaths.PlannerCassettePath;
                var hasKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CodeSpace.Core.Services.Workflows.Llm.Anthropic.AnthropicClient.ApiKeyEnvVar));

                return hasKey
                    ? Workflows.Infrastructure.RecordReplayStructuredLLMClient.ForRecording(inner, cassettePath)
                    : Workflows.Infrastructure.RecordReplayStructuredLLMClient.ForReplay(cassettePath);
            })
            .As<CodeSpace.Core.Services.Workflows.Llm.ILLMClient>()
            .As<CodeSpace.Core.Services.Workflows.Llm.IStructuredLLMClient>()
            .SingleInstance();

        // PR-E E3 supervisor: a deterministic, test-controllable decider registered OVER the production
        // LlmSupervisorDecider (last-wins), so the supervisor node's own DI scope resolves THIS — driving the
        // REAL turn service + executor with no LLM. The fixture-singleton script knob lets a test pick the arc
        // (default plan→stop keeps the E2 flow green; the E3 crown-jewel flips it to plan→spawn→stop).
        builder.RegisterType<Workflows.Infrastructure.SupervisorDecisionScript>().AsSelf().SingleInstance();
        builder.RegisterType<Workflows.Infrastructure.ScriptedSupervisorDecider>()
            .As<CodeSpace.Core.Services.Supervisor.ISupervisorDecider>()
            .InstancePerLifetimeScope();
    }
}
