using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The executor-owned per-run UDS MCP endpoint lifecycle proof. Unlike <c>McpToolTeamScopeFlowTests</c> (which calls
/// the handler directly), this drives the REAL <c>AgentRunExecutor.ExecuteAsync</c> with the endpoint flag ON and a
/// SLEEPING ScriptedHarness on a background task, then connects through the per-run
/// <see cref="IAgentMcpConnectRegistry"/> seam to the endpoint the executor opened — connecting a REAL
/// <c>UnixDomainSocketEndPoint</c> client (the proxy's stand-in) to <c>connect.SocketPath</c>, presenting
/// <c>connect.Token</c> as the first line, then speaking newline-delimited JSON-RPC over the socket — proving
/// open-before-harness → authenticate → serve-over-UDS → close-after-harness, NOT just that the loop/handler work.
///
/// <para>Per-run isolation: the seam is keyed by run id, so a consumer reaches ONLY its own run's socket; the entry is
/// removed AND the socket file unlinked on the endpoint's dispose, so after the harness returns the run no longer
/// resolves. The connect registry is a DI singleton shared between the executor's dedicated MCP scope and this test's
/// scope, so the test sees the entry the executor registered.</para>
///
/// <para>Tier 🟢 high-fidelity: real production executor + real DI registry + real Postgres + real git clone + real
/// AF_UNIX socket (Rule 12). Skips on Windows / when git is absent / on a host without UDS support so a cross-host
/// <c>dotnet test</c> stays clean (Rule 12.1).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentMcpEndpointFlowTests
{
    private const string ProtocolVersion = "2024-11-05";

    private readonly PostgresFixture _fixture;

    public AgentMcpEndpointFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Real_execute_opens_the_endpoint_serves_initialize_tools_list_and_a_team_scoped_call_then_closes_it()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "hello-from-team-a");
        var repoId = await SeedRepositoryAsync(teamId, new Uri(origin.Path).AbsoluteUri, "main");

        // A run with team A's autonomy at Unleashed (so the destructive agent.run_command is gated-Allow), sleeping so
        // the endpoint stays open while we drive JSON-RPC over it.
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        using var connects = ConnectRegistryFromFixture();
        var run = RunExecutorInBackground(runId, new ScriptedHarness("sleep 6"));

        var connect = await WaitForConnectAsync(connects, runId);
        var socketPath = connect.SocketPath;
        await using var client = await McpClient.ConnectAsync(connect);

        // initialize → pinned protocol version + serverInfo name.
        var init = await client.ExchangeAsync(1, "initialize");
        init.GetProperty("result").GetProperty("protocolVersion").GetString().ShouldBe(ProtocolVersion);
        init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("codespace");

        // tools/list → the REAL catalog (agent.run_command present).
        var list = await client.ExchangeAsync(2, "tools/list");
        ToolNames(list).ShouldContain("agent.run_command", customMessage: "the real registry must project agent.run_command");

        // tools/call team-A happy path → reads team A's repo from the clone the node made within the run's tenant.
        var call = await client.CallToolAsync(3, "agent.run_command", new { repositoryId = repoId.ToString(), command = "cat", args = new[] { "README.md" } });
        call.GetProperty("isError").GetBoolean().ShouldBeFalse(customMessage: "the team-A repo must resolve and the command run inside its clone");
        Text(call).ShouldContain("hello-from-team-a", customMessage: "the command read the team-A repo file");

        await run;   // the sleeping harness returns → ExecuteAsync completes → the endpoint disposes

        connects.TryConnect(runId, out _).ShouldBeFalse(customMessage: "after the harness returns the endpoint must be torn down and the seam must no longer resolve the run");
        File.Exists(socketPath).ShouldBeFalse(customMessage: "dispose must unlink the per-run socket file");
    }

    [Fact]
    public async Task Endpoint_at_Confined_denies_a_destructive_tool_before_execution_with_no_clone()
    {
        if (OperatingSystem.IsWindows()) return;   // the executor still spawns /bin/sh for the sleeping harness
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Confined);

        using var connects = ConnectRegistryFromFixture();
        var run = RunExecutorInBackground(runId, new ScriptedHarness("sleep 6"));

        var connect = await WaitForConnectAsync(connects, runId);
        await using var client = await McpClient.ConnectAsync(connect);

        // agent.run_command is destructive → gated. At Confined the autonomy gate denies it before any clone is tried.
        var call = await client.CallToolAsync(1, "agent.run_command", new { command = "true" });
        call.GetProperty("isError").GetBoolean().ShouldBeTrue();
        Text(call).ShouldContain("not permitted", customMessage: "a destructive tool at Confined is denied before execution");

        await run;
    }

    [Fact]
    public async Task A_team_A_endpoint_naming_team_Bs_repo_fails_closed_without_leaking_existence()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;
        if (!await GitAvailableAsync()) return;

        var teamA = await SeedTeamAsync();
        var teamB = await SeedTeamAsync();
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "secret-of-team-b");
        var teamBRepoId = await SeedRepositoryAsync(teamB, new Uri(origin.Path).AbsoluteUri, "main");

        var runId = await CreateRunAsync(teamA, AgentAutonomyLevel.Unleashed);

        using var connects = ConnectRegistryFromFixture();
        var run = RunExecutorInBackground(runId, new ScriptedHarness("sleep 6"));

        var connect = await WaitForConnectAsync(connects, runId);
        await using var client = await McpClient.ConnectAsync(connect);

        var call = await client.CallToolAsync(1, "agent.run_command", new { repositoryId = teamBRepoId.ToString(), command = "cat", args = new[] { "README.md" } });
        call.GetProperty("isError").GetBoolean().ShouldBeTrue(customMessage: "a cross-team repo id must fail closed, never clone");
        Text(call).ShouldContain("not found", customMessage: "a cross-team repo is indistinguishable from a missing one");
        Text(call).ShouldNotContain("secret-of-team-b");

        await run;
    }

    [Fact]
    public async Task Connecting_with_a_wrong_token_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        using var connects = ConnectRegistryFromFixture();
        var run = RunExecutorInBackground(runId, new ScriptedHarness("sleep 6"));

        var connect = await WaitForConnectAsync(connects, runId);

        // Present a WRONG token as the first line; the endpoint closes the connection before serving any JSON-RPC.
        await using var client = await McpClient.ConnectWithRawTokenAsync(connect.SocketPath, connect.Token + "-tampered");

        var reply = await client.TrySendThenReadAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
        reply.ShouldBeNull(customMessage: "a wrong token must read EOF before any JSON-RPC reply");

        await run;
    }

    [Fact]
    public async Task Flag_off_mints_no_endpoint()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        using var connects = ConnectRegistryFromFixture();

        // Flag OFF (default): ExecuteAsync runs the harness with NO endpoint — the seam never has an entry for the run.
        await ExecuteAsync(runId, new ScriptedHarness("printf 'done\\n'"), mcpEnabled: false);

        connects.TryConnect(runId, out _).ShouldBeFalse(customMessage: "flag-OFF ExecuteAsync must mint no endpoint (byte-identical to today)");
        File.Exists(LocalProcessRunner.McpSocketPathFor(runId.ToString("N"))).ShouldBeFalse(customMessage: "flag-OFF must never bind a socket under the run's spool dir");

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
    }

    [Fact]
    public async Task Wiring_on_writes_a_valid_declaration_into_the_config_home_pointing_at_the_run_socket_and_token()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        // Endpoint flag ON + a declaring harness that requests a config home + the proxy binary present → the runner
        // writes the .mcp.json before the (quick) harness runs. After completion the spool persists (reaped by age, not
        // on completion), so we read it.
        await ExecuteAsync(runId, new DeclaringScriptedHarness("printf 'done\\n'"), mcpEnabled: true);

        var declarationPath = Path.Combine(LocalProcessRunner.SpoolDirectoryFor(runId.ToString("N")), "agent-home", ".mcp.json");
        File.Exists(declarationPath).ShouldBeTrue(customMessage: "the endpoint flag ON + a declaring harness must write the MCP declaration into the per-run config-home");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(declarationPath));   // valid JSON
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("codespace");
        server.GetProperty("command").GetString().ShouldBe(StandInProxyPath(), customMessage: "the declaration command is the ABSOLUTE resolved proxy path, not a bare PATH name");

        var env = server.GetProperty("env");
        env.GetProperty("CODESPACE_MCP_SOCKET").GetString().ShouldBe(LocalProcessRunner.McpSocketPathFor(runId.ToString("N")),
            customMessage: "the declaration must point at the SAME socket path the executor's listener binds");
        env.GetProperty("CODESPACE_RUN_TOKEN").GetString().ShouldNotBeNullOrEmpty(customMessage: "the declaration carries the run token the proxy authenticates with");

        // 0600: the token lives in this file, so it must not be group/other-readable.
        File.GetUnixFileMode(declarationPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite, customMessage: "the token-bearing declaration must be 0600");

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
    }

    [Fact]
    public async Task A_missing_proxy_binary_fails_closed_no_declaration_is_written_and_the_run_still_succeeds()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        // Endpoint flag ON + a declaring harness, but the resolved proxy binary does NOT exist host-side (FIX 2 fail-
        // closed): the executor writes NO declaration (handing the agent a config pointing at a missing binary would
        // surface as a confusingly-broken MCP init), and the run still completes — MCP is optional infra.
        await ExecuteAsync(runId, new DeclaringScriptedHarness("printf 'done\\n'"), mcpEnabled: true, proxyPresent: false);

        var declarationPath = Path.Combine(LocalProcessRunner.SpoolDirectoryFor(runId.ToString("N")), "agent-home", ".mcp.json");
        File.Exists(declarationPath).ShouldBeFalse(customMessage: "a missing proxy binary must fail closed — no declaration pointing at it");

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded,
            customMessage: "the tool fabric is optional infra; a missing proxy binary does not fail the run");
    }

    [Fact]
    public async Task A_bind_failure_fails_soft_the_run_still_succeeds_with_no_endpoint_registered()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        using var connects = ConnectRegistryFromFixture();

        // PRE-OCCUPY the exact socket path the executor will bind with a DIRECTORY (not a live socket): the endpoint
        // ctor's stale-socket File.Delete can't remove a directory, so Bind fails on it → the A10 fail-soft kicks in.
        // (A live socket file would be unlinked by that File.Delete and the rebind would succeed, so a directory is the
        // platform-robust way to force the bind failure.) The endpoint is optional infra, so the run still runs.
        var occupiedPath = LocalProcessRunner.McpSocketPathFor(runId.ToString("N"));
        Directory.CreateDirectory(occupiedPath);

        try
        {
            // Flag ON + a DECLARING harness (so a successful bind WOULD write a declaration), but the bind is doomed →
            // ExecuteAsync logs a Warning, proceeds WITHOUT the endpoint, writes NO declaration (the wiring is gated on a
            // non-null endpoint), and the quick harness still completes the run.
            await ExecuteAsync(runId, new DeclaringScriptedHarness("printf 'done\\n'"), mcpEnabled: true);

            connects.TryConnect(runId, out _).ShouldBeFalse(customMessage: "a bind failure must register NO endpoint — the seam never resolves the run");

            var declarationPath = Path.Combine(LocalProcessRunner.SpoolDirectoryFor(runId.ToString("N")), "agent-home", ".mcp.json");
            File.Exists(declarationPath).ShouldBeFalse(customMessage: "a fail-soft bind (no endpoint) must write NO declaration — it would point at a socket that was never bound");

            using var scope = _fixture.BeginScope();
            (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded,
                customMessage: "the endpoint is optional infra; a bind failure does not fail the run");
        }
        finally { try { Directory.Delete(occupiedPath, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task A_reattach_after_worker_death_re_opens_the_endpoint_with_the_PERSISTED_token()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        using var connects = ConnectRegistryFromFixture();

        // 1. REAL ExecuteAsync, endpoint flag ON, a SLEEPING declaring harness (so the handle persists with McpRunToken
        //    while the supervisor stays alive). Run it on a cancellable background task = the original worker.
        // The supervisor sleeps long enough to outlive the whole two-phase choreography under any CI/host load — the test
        // never WAITS this out (it cancels the re-attach the instant the token-survival assertions pass), so the duration
        // is bounded by the choreography, not the sleep. A short sleep would race the endpoint-opens and flake under load.
        using var workerCts = new CancellationTokenSource();
        var firstRun = Task.Run(() => ExecuteAsync(runId, new DeclaringScriptedHarness("sleep 120"), mcpEnabled: true, cancellationToken: workerCts.Token));

        var firstConnect = await WaitForConnectAsync(connects, runId, firstRun);
        var persistedToken = firstConnect.Token;   // == the token minted + persisted on the handle (single source)

        // The endpoint registers (above) BEFORE the durable launch writes the handle, so cancelling the instant we
        // connect would race ahead of SetRunnerHandleAsync and leave NO persisted token to re-attach with. Production
        // only re-attaches runs whose handle was written (an earlier death just abandons the run), so mirror that
        // invariant: wait for the token to land on the handle before simulating the worker death.
        await WaitForPersistedTokenAsync(persistedToken, runId, firstRun);

        // 2. Simulate WORKER DEATH: cancel the executor's token. ExecuteAsync leaves the run Running (for the reconciler),
        //    disposes its in-process endpoint (drops the connect + unlinks the socket) — the detached supervisor keeps
        //    sleeping with its on-disk declaration still pointing at this token.
        workerCts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => firstRun);
        await WaitForNoConnectAsync(connects, runId);   // the original endpoint is gone

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Running, "worker death leaves the run Running for the reconciler");

        // 3. RECLAIM (the reconciler's atomic step) then a FRESH executor's ReattachAsync — it re-opens the endpoint on
        //    the SAME socket+token the handle recorded at launch, bounded to the re-tail span (the supervisor is still
        //    sleeping). Run it on a background task so we can drive JSON-RPC against the re-bound socket.
        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeTrue();

        using var reattachCts = new CancellationTokenSource();
        var reattach = Task.Run(() => ReattachAsync(runId, new DeclaringScriptedHarness("sleep 120"), mcpEnabled: true, cancellationToken: reattachCts.Token));

        var reConnect = await WaitForConnectAsync(connects, runId, reattach);
        reConnect.Token.ShouldBe(persistedToken, "the re-attach re-binds the SAME token the agent's declaration already holds — survived worker death via the persisted handle");

        // (a) a client presenting the PERSISTED token completes initialize/tools-list against the RE-BOUND socket.
        await using (var client = await McpClient.ConnectWithRawTokenAsync(reConnect.SocketPath, persistedToken))
        {
            var init = await client.ExchangeAsync(1, "initialize");
            init.GetProperty("result").GetProperty("protocolVersion").GetString().ShouldBe(ProtocolVersion);

            var list = await client.ExchangeAsync(2, "tools/list");
            ToolNames(list).ShouldContain("agent.run_command", customMessage: "the re-opened endpoint serves the real tool catalog over the re-bound socket");
        }

        // (b) a DIFFERENT token is refused by the re-opened endpoint — the token gate survived intact.
        await using (var wrong = await McpClient.ConnectWithRawTokenAsync(reConnect.SocketPath, persistedToken + "-tampered"))
        {
            var reply = await wrong.TrySendThenReadAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
            reply.ShouldBeNull(customMessage: "a wrong token must be refused even after a cross-worker-death re-open");
        }

        // Token survival across worker death is now PROVEN (re-bound same token serves; wrong token refused). Tear the
        // re-attach down by cancelling (a second worker death) rather than waiting out the 120s supervisor — the
        // re-attach→completion tail is a separate concern already covered by AgentRunReattachFlowTests. The re-opened
        // endpoint disposes on the cancel (idempotent, never-throws); the detached supervisor self-exits.
        reattachCts.Cancel();
        try { await reattach; } catch (OperationCanceledException) { /* second worker death — expected */ }

        await WaitForNoConnectAsync(connects, runId);   // the re-opened endpoint is torn down when the re-attach worker dies

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Running, "a second worker death leaves the run Running for the next reconciler pass");
    }

    // ── Driving the REAL executor ───────────────────────────────────────────

    /// <summary>Resolve the connect-registry SINGLETON from the fixture; it is the same instance the executor's MCP scope registers into.</summary>
    private FlagScope ConnectRegistryFromFixture()
    {
        var scope = _fixture.BeginScope();
        return new FlagScope(scope, scope.Resolve<IAgentMcpConnectRegistry>());
    }

    /// <summary>Start the real ExecuteAsync (flag ON) on a background task so the test can drive JSON-RPC while the harness sleeps.</summary>
    private Task RunExecutorInBackground(Guid runId, IAgentHarness harness) => Task.Run(() => ExecuteAsync(runId, harness, mcpEnabled: true));

    /// <summary>
    /// Drive the REAL ExecuteAsync. The wiring is FOLDED into the single endpoint flag (FIX 4): when it's on AND the
    /// harness declares an MCP server AND the proxy binary exists, a declaration is written — no second flag. The proxy
    /// binary isn't copied into the test bin, so when <paramref name="proxyPresent"/> we point <c>CODESPACE_MCP_PROXY_PATH</c>
    /// at a real existing stand-in (the test only File.Exists-checks it; the scripted harness runs /bin/sh, not the proxy);
    /// when false we point it at a missing path to exercise the fail-closed "no declaration" branch.
    /// </summary>
    private async Task ExecuteAsync(Guid runId, IAgentHarness harness, bool mcpEnabled, bool proxyPresent = true, CancellationToken cancellationToken = default)
    {
        var previousEndpoint = Environment.GetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar);
        var previousProxy = Environment.GetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar);
        Environment.SetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar, mcpEnabled ? "true" : null);
        Environment.SetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar, mcpEnabled ? (proxyPresent ? StandInProxyPath() : "/nonexistent/codespace-mcp") : null);

        try
        {
            using var scope = _fixture.BeginScope();
            await NewExecutor(scope, harness).ExecuteAsync(runId, cancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar, previousEndpoint);
            Environment.SetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar, previousProxy);
        }
    }

    /// <summary>Drive a FRESH executor's ReattachAsync with the endpoint flag (+ proxy path) ON — the cross-worker-death re-open path.</summary>
    private async Task ReattachAsync(Guid runId, IAgentHarness harness, bool mcpEnabled, CancellationToken cancellationToken = default)
    {
        var previousEndpoint = Environment.GetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar);
        var previousProxy = Environment.GetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar);
        Environment.SetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar, mcpEnabled ? "true" : null);
        Environment.SetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar, mcpEnabled ? StandInProxyPath() : null);

        try
        {
            using var scope = _fixture.BeginScope();
            await NewExecutor(scope, harness).ReattachAsync(runId, cancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar, previousEndpoint);
            Environment.SetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar, previousProxy);
        }
    }

    private static AgentRunExecutor NewExecutor(ILifetimeScope scope, IAgentHarness harness) => new(
        scope.Resolve<IAgentRunService>(),
        new AgentHarnessRegistry(new[] { harness }),
        scope.Resolve<ISandboxRunnerRegistry>(),
        scope.Resolve<IAgentWorkspaceResolver>(),
        scope.Resolve<IModelCredentialResolver>(),
        scope.Resolve<IWorkspaceProviderRegistry>(),
        scope.Resolve<IAgentRunCompletionNotifier>(),
        scope.Resolve<IServiceScopeFactory>(),
        NullLogger<AgentRunExecutor>.Instance);

    /// <summary>A real existing executable to satisfy the proxy-path File.Exists fail-closed guard — /bin/sh always exists on POSIX (the proxy is never actually exec'd; the scripted harness runs /bin/sh and the declaration is only read, not spawned).</summary>
    private static string StandInProxyPath() => "/bin/sh";

    private static async Task<IAgentMcpClientConnect> WaitForConnectAsync(FlagScope connects, Guid runId, Task? backgroundRun = null)
    {
        // ~15s budget: the endpoint opens early in ExecuteAsync, but under full-suite load (Postgres contention, JIT,
        // workspace clone) reaching that point can take several seconds — a tight 5s window flakes.
        for (var i = 0; i < 300; i++)
        {
            if (connects.Registry.TryConnect(runId, out var connect)) return connect;

            // Surface a faulted background ExecuteAsync immediately rather than waiting out the full timeout — its
            // exception is far more diagnostic than a bare "did not register".
            if (backgroundRun is { IsFaulted: true }) await backgroundRun;

            await Task.Delay(50);
        }

        throw new TimeoutException($"The MCP endpoint for run {runId} did not register within 15s — check that ExecuteAsync opened it before the harness (env {AgentRunExecutor.McpEndpointEnabledEnvVar}=true).");
    }

    /// <summary>Wait until the run's durable handle has been persisted WITH the McpRunToken — production only re-attaches runs whose handle was written, so the test must not race the cancel ahead of SetRunnerHandleAsync (the endpoint registers earlier, in OpenMcpEndpointIfEnabledAsync).</summary>
    private async Task WaitForPersistedTokenAsync(string expectedToken, Guid runId, Task backgroundRun)
    {
        for (var i = 0; i < 300; i++)
        {
            using (var scope = _fixture.BeginScope())
            {
                var json = (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).RunnerHandleJson;
                if (json is { Length: > 0 } && JsonSerializer.Deserialize<SandboxHandle>(json, AgentJson.Options)?.McpRunToken == expectedToken) return;
            }

            if (backgroundRun is { IsFaulted: true }) await backgroundRun;

            await Task.Delay(50);
        }

        throw new TimeoutException($"Run {runId}'s durable handle did not persist the MCP token within 15s — the durable launch should write it before AttachAsync blocks.");
    }

    private static async Task WaitForNoConnectAsync(FlagScope connects, Guid runId)
    {
        for (var i = 0; i < 100; i++)
        {
            if (!connects.Registry.TryConnect(runId, out _)) return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"The MCP endpoint for run {runId} was still registered after 5s — the original worker's endpoint should have been disposed on cancel.");
    }

    // ── JSON-RPC over the real UDS (the proxy's stand-in) ───────────────────

    /// <summary>
    /// A real <c>AF_UNIX</c> client connecting to the per-run socket the executor bound — the proxy's stand-in. It
    /// authenticates by sending the run token as the connection's FIRST line (Utf8NoBom, '\n'-terminated) before any
    /// JSON-RPC, then exchanges newline-delimited requests/responses over the socket's NetworkStream.
    /// </summary>
    private sealed class McpClient : IAsyncDisposable
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly Socket _socket;
        private readonly NetworkStream _net;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private McpClient(Socket socket)
        {
            _socket = socket;
            _net = new NetworkStream(socket, ownsSocket: false);
            _reader = new StreamReader(_net, Utf8NoBom, detectEncodingFromByteOrderMarks: false);
            _writer = new StreamWriter(_net, Utf8NoBom) { AutoFlush = false, NewLine = "\n" };
        }

        public static Task<McpClient> ConnectAsync(IAgentMcpClientConnect connect) => ConnectWithRawTokenAsync(connect.SocketPath, connect.Token);

        public static async Task<McpClient> ConnectWithRawTokenAsync(string socketPath, string token)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));

            var client = new McpClient(socket);
            await client._writer.WriteLineAsync(token);
            await client._writer.FlushAsync();

            return client;
        }

        public async Task<JsonElement> ExchangeAsync(int id, string method)
        {
            var line = await SendThenReadAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method }))
                ?? throw new InvalidOperationException("endpoint closed before a response");
            return JsonDocument.Parse(line).RootElement.Clone();
        }

        public async Task<JsonElement> CallToolAsync(int id, string name, object arguments)
        {
            var line = await SendThenReadAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments } }))
                ?? throw new InvalidOperationException("endpoint closed before a response");
            return JsonDocument.Parse(line).RootElement.Clone().GetProperty("result");
        }

        /// <summary>
        /// Send a request and read the response line, returning null on EOF — OR null when the send/read fails with a
        /// broken pipe / reset (the server closed the connection: a refusal manifests EITHER as a write that breaks on
        /// the already-closed socket OR as a read that returns EOF, depending on the race). Used by the wrong-token test.
        /// </summary>
        public async Task<string?> TrySendThenReadAsync(string request)
        {
            try { return await SendThenReadAsync(request); }
            catch (IOException) { return null; }
            catch (SocketException) { return null; }
        }

        private async Task<string?> SendThenReadAsync(string request)
        {
            await _writer.WriteLineAsync(request);
            await _writer.FlushAsync();

            return await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            // Best-effort: a writer flush on an already-closed socket (the wrong-token path) would throw on dispose.
            try { _writer.Dispose(); } catch { /* broken pipe on flush */ }
            _reader.Dispose();
            await _net.DisposeAsync();
            _socket.Dispose();
        }
    }

    private static string[] ToolNames(JsonElement listResponse) =>
        listResponse.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();

    private static string Text(JsonElement toolResult) => toolResult.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

    // ── Seeding (mirrors McpToolTeamScopeFlowTests + AgentRunExecutorTests) ──

    private async Task<Guid> CreateRunAsync(Guid teamId, AgentAutonomyLevel autonomy)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "scripted", Harness = "scripted", Model = "test-model", TimeoutSeconds = 1800, Autonomy = autonomy },
            teamId, null, null, CancellationToken.None);
        return run.Id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"agent-{userId:N}@test.local", Name = $"agent-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-{teamId:N}", Name = "Agent Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId, string cloneUrlHttps, string defaultBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "local", BaseUrl = "https://local" });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = null,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = defaultBranch, CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private static async Task SeedLocalRepoAsync(string dir, string file, string content)
    {
        await RunGitInAsync(dir, "init", "-b", "main");
        await RunGitInAsync(dir, "config", "user.email", "test@codespace.dev");
        await RunGitInAsync(dir, "config", "user.name", "Test");
        await RunGitInAsync(dir, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(dir, file), content);
        await RunGitInAsync(dir, "add", ".");
        await RunGitInAsync(dir, "commit", "-m", "seed");
    }

    private static async Task RunGitInAsync(string workdir, params string[] args)
    {
        var result = await new LocalProcessRunner().RunAsync(
            new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
        if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-mcp-ep-origin-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>Holds the fixture scope open while a test reads the connect-registry singleton it resolved from it.</summary>
    private sealed class FlagScope : IDisposable
    {
        private readonly IDisposable _scope;
        public FlagScope(IDisposable scope, IAgentMcpConnectRegistry registry) { _scope = scope; Registry = registry; }
        public IAgentMcpConnectRegistry Registry { get; }
        public bool TryConnect(Guid runId, out IAgentMcpClientConnect connect) => Registry.TryConnect(runId, out connect);
        public void Dispose() => _scope.Dispose();
    }

    /// <summary>A CLI-less test harness (copy of AgentRunExecutorTests.ScriptedHarness): runs /bin/sh on a fixed script.</summary>
    private sealed class ScriptedHarness : IAgentHarness
    {
        private readonly string _script;

        public ScriptedHarness(string script) => _script = script;

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", _script }, WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = task.TimeoutSeconds };

        public AgentEvent? ParseEvent(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? null : new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() };

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" };
    }

    /// <summary>A scripted harness that ALSO declares an MCP server + requests a per-run config home — so the runner has somewhere to write the declaration the wiring produces. Mirrors a real harness's BuildMcpDeclaration shape without a CLI.</summary>
    private sealed class DeclaringScriptedHarness : IAgentHarness, IMcpHarnessDeclaration
    {
        private readonly string _script;

        public DeclaringScriptedHarness(string script) => _script = script;

        // Kind "scripted" so the executor resolves it for the run CreateRunAsync seeds (Harness = "scripted").
        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new()
        {
            Command = "/bin/sh",
            Args = new[] { "-c", _script },
            WorkingDirectory = task.WorkspaceDirectory,
            TimeoutSeconds = task.TimeoutSeconds,
            ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR" },   // gives the runner a per-run home to write the declaration into
        };

        public McpHarnessDeclaration BuildMcpDeclaration(McpDeclarationContext context) => new() { RelativeFileName = ".mcp.json", Content = McpDeclarationWriter.RenderClaudeJson(context) };

        public AgentEvent? ParseEvent(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? null : new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() };

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" };
    }
}
