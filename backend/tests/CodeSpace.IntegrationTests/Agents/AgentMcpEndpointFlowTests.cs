using System.Diagnostics;
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
using CodeSpace.Core.Services.Decisions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
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
///
/// <para>The three <c>..._proxy_..</c> tests raise the fidelity another notch: instead of the in-test <c>McpClient</c>
/// they spawn the REAL <c>codespace-mcp</c> proxy BINARY (a <c>dotnet codespace-mcp.dll --proxy</c> child process) and
/// pipe JSON-RPC over its stdin/stdout — proving the WHOLE production transport chain stdio↔proxy↔UDS↔endpoint↔handler
/// end-to-end (the only un-runnable leg being the CLI's own config-loading, which is deployment-gated). The proxy dll
/// is built via a build-only ProjectReference in the csproj; these tests skip if it isn't found (Rule 12.1).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentMcpEndpointFlowTests
{
    private const string ProtocolVersion = "2024-11-05";

    /// <summary>
    /// ON-DEMAND gate for the real-CLI config-load smoke. Default-OFF so CI (which has no proprietary <c>claude</c>
    /// binary) skips it; a developer sets it to "1" with a real <c>claude</c> on PATH (or via
    /// <c>CODESPACE_CLAUDE_CODE_PATH</c>) to prove the REAL CLI loads the harness-rendered <c>.mcp.json</c> and lists
    /// the codespace MCP tools over the real proxy + endpoint. We do NOT fake the CLI — the CI-runnable proof is the
    /// 🟢 <c>A_real_codespace_mcp_proxy_process_...</c> tests (real proxy binary over the real UDS); this closes the
    /// last (deployment-gated) leg on demand.
    /// </summary>
    private const string RealCliSmokeEnvVar = "CODESPACE_RUN_REAL_CLI_MCP_SMOKE";

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

    // ── REAL codespace-mcp proxy BINARY over the per-run UDS (Tier 🟢 high-fidelity) ───────────────────────────────
    //
    // The tests above drive the per-run socket with an IN-TEST AF_UNIX client (McpClient). The three below instead
    // spawn the REAL `codespace-mcp` proxy as a child `dotnet codespace-mcp.dll --proxy` process — exactly what a Codex
    // /Claude CLI does — and pipe newline-delimited JSON-RPC over its STDIN/STDOUT. The proxy itself reads the socket
    // path + token from its env, connects the per-run UDS, sends the token as line 1, then raw-byte forwards both ways.
    // So a passing test proves the ENTIRE production transport chain end-to-end: stdio ↔ proxy ↔ UDS ↔ endpoint
    // accept-loop ↔ McpFramingLoop ↔ handler ↔ gate ↔ tenancy ↔ NodeAgentTool ↔ real git. The only un-runnable leg is
    // the CLI's own config-loading (deployment-gated), which the proxy does not touch.

    [Fact]
    public async Task A_real_codespace_mcp_proxy_process_drives_a_full_session_over_the_per_run_socket()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;
        if (!await GitAvailableAsync()) return;
        var proxyDll = ProxyDllPathOrNull();
        if (proxyDll is null) return;   // the build-only reference should have produced it; skip rather than fail for portability

        var teamId = await SeedTeamAsync();
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "hello-from-team-a");
        var repoId = await SeedRepositoryAsync(teamId, new Uri(origin.Path).AbsoluteUri, "main");

        // A team-B repo whose id team A's run may NOT see — used to prove tenancy fail-closed travels through the proxy.
        var teamB = await SeedTeamAsync();
        using var originB = new TempDir();
        await SeedLocalRepoAsync(originB.Path, "README.md", "secret-of-team-b");
        var teamBRepoId = await SeedRepositoryAsync(teamB, new Uri(originB.Path).AbsoluteUri, "main");

        // Unleashed so the destructive agent.run_command is gated-Allow. A LONG-sleeping harness keeps the endpoint open;
        // we cancel the worker the instant the session asserts pass (the cancel-decouple pattern), so the test is bounded
        // by the choreography, not the sleep.
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        using var connects = ConnectRegistryFromFixture();
        using var workerCts = new CancellationTokenSource();
        var run = Task.Run(() => ExecuteAsync(runId, new ScriptedHarness("sleep 120"), mcpEnabled: true, cancellationToken: workerCts.Token));

        try
        {
            var connect = await WaitForConnectAsync(connects, runId, run);

            await using var proxy = RealProxyProcess.Start(proxyDll, connect.SocketPath, connect.Token);

            // (a) initialize → pinned protocol version + serverInfo name (the proxy forwarded the handshake verbatim).
            var init = await proxy.ExchangeAsync(1, "initialize");
            init.GetProperty("result").GetProperty("protocolVersion").GetString().ShouldBe(ProtocolVersion);
            init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("codespace");

            // (b) tools/list → the REAL catalog (a write tool AND a read git tool both present).
            var list = await proxy.ExchangeAsync(2, "tools/list");
            var tools = ToolNames(list);
            tools.ShouldContain("agent.run_command", customMessage: "the real registry must project agent.run_command over the proxy");
            tools.ShouldContain("git.list_prs", customMessage: "the real registry must project a read git tool over the proxy");

            // (c) tools/call team-A happy path → proves the FULL chain: proxy→UDS→handler→gate(Unleashed Allow)→
            //     tenancy(team A)→NodeAgentTool→real git clone→command on the cloned repo.
            var call = await proxy.CallToolAsync(3, "agent.run_command", new { repositoryId = repoId.ToString(), command = "cat", args = new[] { "README.md" } });
            call.GetProperty("isError").GetBoolean().ShouldBeFalse(customMessage: "the team-A repo must resolve and the command run inside its clone, through the REAL proxy");
            Text(call).ShouldContain("hello-from-team-a", customMessage: "the command read the team-A repo file end-to-end through the proxy binary");

            // (d) a NOTIFICATION (no id) → the server emits NO response line; the raw-byte proxy forwards it and the
            //     framing survives (proven by the next request still getting its matching reply).
            await proxy.SendNotificationAsync("notifications/initialized");

            // (e) a SUBSEQUENT cross-team call → tenancy fail-closed ("not found", no leak) travels through the real
            //     proxy AND the session survived the notification (framing intact on one long-lived connection).
            var crossTeam = await proxy.CallToolAsync(4, "agent.run_command", new { repositoryId = teamBRepoId.ToString(), command = "cat", args = new[] { "README.md" } });
            crossTeam.GetProperty("isError").GetBoolean().ShouldBeTrue(customMessage: "a cross-team repo id must fail closed even through the real proxy");
            Text(crossTeam).ShouldContain("not found", customMessage: "a cross-team repo is indistinguishable from a missing one");
            Text(crossTeam).ShouldNotContain("secret-of-team-b");

            // Close stdin → the proxy's stdin pump hits EOF → it tears the socket down and exits 0 (the happy-path exit).
            await proxy.CompleteAndAssertCleanExitAsync();
        }
        finally
        {
            workerCts.Cancel();
            try { await run; } catch (OperationCanceledException) { /* worker death — expected, decouples from the 120s sleep */ }
        }
    }

    [Fact]
    public async Task Two_concurrent_proxy_processes_each_get_their_own_served_session()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;
        if (!await GitAvailableAsync()) return;
        var proxyDll = ProxyDllPathOrNull();
        if (proxyDll is null) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        using var connects = ConnectRegistryFromFixture();
        using var workerCts = new CancellationTokenSource();
        var run = Task.Run(() => ExecuteAsync(runId, new ScriptedHarness("sleep 120"), mcpEnabled: true, cancellationToken: workerCts.Token));

        try
        {
            var connect = await WaitForConnectAsync(connects, runId, run);

            // TWO real proxy processes against the SAME open endpoint, concurrently — proving the accept-loop serves
            // concurrent connections (one McpFramingLoop per connection, one ServeConnectionAsync task each).
            await using var proxyA = RealProxyProcess.Start(proxyDll, connect.SocketPath, connect.Token);
            await using var proxyB = RealProxyProcess.Start(proxyDll, connect.SocketPath, connect.Token);

            await SessionInitAndListAsync(proxyA, "A");
            await SessionInitAndListAsync(proxyB, "B");

            await proxyA.CompleteAndAssertCleanExitAsync();
            await proxyB.CompleteAndAssertCleanExitAsync();
        }
        finally
        {
            workerCts.Cancel();
            try { await run; } catch (OperationCanceledException) { /* worker death — expected */ }
        }
    }

    [Fact]
    public async Task A_wrong_token_proxy_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;
        if (!await GitAvailableAsync()) return;
        var proxyDll = ProxyDllPathOrNull();
        if (proxyDll is null) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        using var connects = ConnectRegistryFromFixture();
        using var workerCts = new CancellationTokenSource();
        var run = Task.Run(() => ExecuteAsync(runId, new ScriptedHarness("sleep 120"), mcpEnabled: true, cancellationToken: workerCts.Token));

        try
        {
            var connect = await WaitForConnectAsync(connects, runId, run);

            // The proxy sends the (tampered) token as line 1; the endpoint reads it, fails the constant-time compare, and
            // silently closes → the proxy's socket→stdout pump hits EOF → ForwardAsync returns → the process exits. An
            // initialize sent to its stdin gets NO reply line (the connection is already gone).
            await using var proxy = RealProxyProcess.Start(proxyDll, connect.SocketPath, connect.Token + "-tampered");

            var reply = await proxy.TryExchangeAsync(1, "initialize");
            reply.ShouldBeNull(customMessage: "a wrong-token proxy must get no JSON-RPC reply — the endpoint closes after the bad token line");

            // With the socket closed, stdin-EOF (or the already-dead socket pump) ends the proxy. It exits non-2 (a clean
            // forward teardown, not the usage-error path) — assert it terminated rather than pinning the exact code.
            (await proxy.WaitForExitAsync()).ShouldBeTrue(customMessage: "a refused proxy must terminate, not hang");
        }
        finally
        {
            workerCts.Cancel();
            try { await run; } catch (OperationCanceledException) { /* worker death — expected */ }
        }
    }

    [Fact]
    public async Task On_demand_the_real_claude_cli_loads_the_rendered_declaration_and_lists_the_codespace_tools()
    {
        // ON-DEMAND ONLY (Rule 12 fidelity honesty): default-OFF so CI — which lacks the proprietary `claude` binary —
        // skips. A developer sets CODESPACE_RUN_REAL_CLI_MCP_SMOKE=1 with a real `claude` on PATH to prove the LAST,
        // deployment-gated leg: the real CLI parses the harness-rendered .mcp.json + connects the codespace MCP server
        // through the real proxy + endpoint and lists its tools. We do NOT fake the CLI — if the gate is off or the
        // binary is absent we skip rather than assert a stand-in.
        if (Environment.GetEnvironmentVariable(RealCliSmokeEnvVar) is not ("1" or "true" or "TRUE")) return;
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;
        var proxyDll = ProxyDllPathOrNull();
        if (proxyDll is null) return;
        var claude = ResolveClaudeOrNull();
        if (claude is null) return;   // no real CLI present → skip (do not fake)

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        using var connects = ConnectRegistryFromFixture();
        using var workerCts = new CancellationTokenSource();
        var run = Task.Run(() => ExecuteAsync(runId, new ScriptedHarness("sleep 120"), mcpEnabled: true, cancellationToken: workerCts.Token));

        try
        {
            var connect = await WaitForConnectAsync(connects, runId, run);

            // Render the REAL Claude harness declaration (the production .mcp.json) pointing at this run's socket+token
            // and the real proxy dll launcher, then write it into a temp config home the CLI will read.
            using var home = new TempDir();
            var context = new McpDeclarationContext { ProxyCommand = "dotnet", SocketPath = connect.SocketPath, Token = connect.Token, ServerName = "codespace" };
            var declaration = ((IMcpHarnessDeclaration)new CodeSpace.Core.Services.Agents.Harnesses.Claude.ClaudeCodeHarness()).BuildMcpDeclaration(context);
            await File.WriteAllTextAsync(Path.Combine(home.Path, declaration.RelativeFileName), declaration.Content);

            // Run the real CLI's MCP listing against the rendered config home. `claude mcp list` connects each declared
            // server and reports it — no model call, so no gateway needed. Assert it sees the codespace server.
            var psi = new ProcessStartInfo { FileName = claude, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = home.Path };
            psi.Environment["CLAUDE_CONFIG_DIR"] = home.Path;
            psi.ArgumentList.Add("mcp");
            psi.ArgumentList.Add("list");

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            stdout.ShouldContain("codespace", customMessage: $"the real claude CLI must load the rendered declaration and list the codespace server. stdout was:\n{stdout}");
        }
        finally
        {
            workerCts.Cancel();
            try { await run; } catch (OperationCanceledException) { /* worker death — expected */ }
        }
    }

    /// <summary>The real claude binary: the CODESPACE_CLAUDE_CODE_PATH override, else `claude` on PATH if present; null to skip the on-demand smoke.</summary>
    private static string? ResolveClaudeOrNull()
    {
        var configured = Environment.GetEnvironmentVariable("CODESPACE_CLAUDE_CODE_PATH");
        if (!string.IsNullOrEmpty(configured)) return File.Exists(configured) ? configured : null;

        try
        {
            using var probe = Process.Start(new ProcessStartInfo { FileName = "claude", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, ArgumentList = { "--version" } });
            probe!.WaitForExit(5000);
            return probe.ExitCode == 0 ? "claude" : null;
        }
        catch { return null; }
    }

    /// <summary>Drive one proxy through initialize + tools/list, asserting both succeed (the per-connection session label aids concurrent-failure diagnosis).</summary>
    private async Task SessionInitAndListAsync(RealProxyProcess proxy, string label)
    {
        var init = await proxy.ExchangeAsync(1, "initialize");
        init.GetProperty("result").GetProperty("protocolVersion").GetString().ShouldBe(ProtocolVersion, customMessage: $"proxy {label}: initialize must succeed on its own connection");

        var list = await proxy.ExchangeAsync(2, "tools/list");
        ToolNames(list).ShouldContain("agent.run_command", customMessage: $"proxy {label}: tools/list must succeed on its own connection");
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
    public async Task A_full_fabric_off_run_tears_down_its_read_only_endpoint_after_completion()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        using var connects = ConnectRegistryFromFixture();

        // Full fabric OFF (the default): the endpoint opens in READ-ONLY mode for the harness span (proven serving in
        // A_full_fabric_off_run_opens_a_read_only_endpoint...), then disposes on exit like any endpoint — so AFTER a
        // quick run the seam no longer resolves the run and the socket is unlinked.
        await ExecuteAsync(runId, new ScriptedHarness("printf 'done\\n'"), mcpEnabled: false);

        connects.TryConnect(runId, out _).ShouldBeFalse(customMessage: "the read-only endpoint is torn down on the harness's exit — the seam must not resolve a completed run");
        File.Exists(LocalProcessRunner.McpSocketPathFor(runId.ToString("N"))).ShouldBeFalse(customMessage: "dispose must unlink the per-run socket file");

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
    }

    [Fact]
    public async Task A_full_fabric_off_run_opens_a_read_only_endpoint_serving_get_context_not_the_side_effecting_tools()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var teamId = await SeedTeamAsync();
        // Unleashed so a side-effecting tool WOULD be gate-Allowed in full mode — proving the read-only MODE (not the
        // autonomy gate) is what withholds it.
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        using var connects = ConnectRegistryFromFixture();
        // mcpEnabled:false → the full side-effecting fabric is OFF → the endpoint opens in read-only mode. A sleeping
        // harness keeps it open while we drive JSON-RPC over the real per-run socket.
        var run = Task.Run(() => ExecuteAsync(runId, new ScriptedHarness("sleep 6"), mcpEnabled: false));

        var connect = await WaitForConnectAsync(connects, runId, run);
        await using var client = await McpClient.ConnectAsync(connect);

        // tools/list serves the read-only tools (get_context + the git reads) but NOT the side-effecting fabric.
        var list = await client.ExchangeAsync(1, "tools/list");
        var tools = ToolNames(list);
        tools.ShouldContain("get_context", customMessage: "the safe read tool is served by default with the full fabric off");
        tools.ShouldContain("git.list_prs", customMessage: "the git read tools are read-only → served by default too");
        tools.ShouldNotContain("agent.run_command", customMessage: "a side-effecting tool is NOT advertised in read-only mode");

        // A side-effecting call is refused by the MODE even though Unleashed would allow it in full mode.
        var call = await client.CallToolAsync(2, "agent.run_command", new { command = "true" });
        call.GetProperty("isError").GetBoolean().ShouldBeTrue(customMessage: "run_command is refused in read-only mode regardless of tier");
        Text(call).ShouldContain("read-only", customMessage: "the refusal explains the run serves only read-only tools");

        await run;
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

    // ── PR-C: allow-list augmentation + governance closed-loop in a real executor run ──────────────────────────────

    [Fact]
    public async Task A_restricted_run_with_the_endpoint_open_receives_the_tier_permitted_mcp_tools_in_the_allow_list()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var teamId = await SeedTeamAsync();
        // Unleashed so every tool (incl. the destructive ones) is tier-permitted → projected into the allow-list.
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed, tools: new[] { "Read", "Grep" });

        var harness = new AllowedToolsCapturingHarness("printf 'done\\n'");

        // Endpoint flag ON + a declaring + tool-projecting harness + the proxy present → the wiring is written, so the
        // executor augments the harness allow-list with the governed mcp__codespace__* names before BuildInvocation.
        await ExecuteAsync(runId, harness, mcpEnabled: true);

        var tools = harness.CapturedTools.ShouldNotBeNull(customMessage: "the executor must have invoked BuildInvocation with a Tools list");

        // Additive: the author's restricted tools stay, AND the governed codespace tools are merged in.
        tools.ShouldContain("Read");
        tools.ShouldContain("Grep");
        tools.ShouldContain("mcp__codespace__agent.run_command", customMessage: "a RESTRICTED run must still receive the governed codespace tools the open endpoint serves");
        tools.ShouldContain("mcp__codespace__git.list_prs", customMessage: "the read git tool is projected too");
        tools.ShouldAllBe(t => !t.StartsWith("mcp__") || t.StartsWith("mcp__codespace__"), customMessage: "only the codespace server's tools are added");

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
    }

    [Fact]
    public async Task A_restricted_run_with_the_full_fabric_OFF_gets_only_the_read_only_codespace_tools_in_the_allow_list()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var teamId = await SeedTeamAsync();
        // Unleashed so a side-effecting tool WOULD be tier-permitted — proving the read-only MODE (not the tier) is what
        // keeps it out of the allow-list.
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed, tools: new[] { "Read", "Grep" });

        var harness = new AllowedToolsCapturingHarness("printf 'done\\n'");

        // Full fabric OFF (the default) + a declaring harness + the proxy present → the read-only endpoint opens and its
        // allow-list augmentation merges ONLY the read-only codespace tools.
        await ExecuteAsync(runId, harness, mcpEnabled: false);

        var tools = harness.CapturedTools.ShouldNotBeNull(customMessage: "the executor must have invoked BuildInvocation with a Tools list");

        tools.ShouldContain("Read");
        tools.ShouldContain("Grep");
        tools.ShouldContain("mcp__codespace__get_context", customMessage: "the read-only endpoint merges the safe read tool into a restricted allow-list");
        tools.ShouldContain("mcp__codespace__git.list_prs", customMessage: "the read git tools are read-only → merged too");
        tools.ShouldNotContain("mcp__codespace__agent.run_command", customMessage: "a side-effecting tool is NOT in the read-only allow-list, even at Unleashed");

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
    }

    [Fact]
    public async Task A_default_all_run_is_not_narrowed_no_allowed_tools_are_forced()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed, tools: null);   // author named NO tools → harness-default-all

        var harness = new AllowedToolsCapturingHarness("printf 'done\\n'");

        await ExecuteAsync(runId, harness, mcpEnabled: true);

        // No regression: a default-all run keeps a null/empty allow-list so the CLI default still reaches the MCP tools —
        // augmenting must NOT convert it into a restricted list of only the codespace tools.
        (harness.CapturedTools is null || harness.CapturedTools.Count == 0)
            .ShouldBeTrue(customMessage: "a default-all run must NOT be narrowed to a restricted allow-list by the augmentation");

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
    }

    [Fact]
    public async Task A_side_effecting_governed_call_writes_a_ledger_row_a_read_only_call_does_not()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "hello-from-team-a");
        var repoId = await SeedRepositoryAsync(teamId, new Uri(origin.Path).AbsoluteUri, "main");

        // Unleashed so the destructive agent.run_command is gated-Allow (runs once through the ledger, not parked).
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        using var connects = ConnectRegistryFromFixture();
        // Endpoint AND governance ON → the side-effecting path routes through the exactly-once ToolCallLedger.
        var run = RunExecutorInBackground(runId, new ScriptedHarness("sleep 6"), governanceEnabled: true);

        var connect = await WaitForConnectAsync(connects, runId);
        await using var client = await McpClient.ConnectAsync(connect);

        // (a) a READ-ONLY tool (git.list_prs) — served BEFORE the ledger (the IsReadOnly short-circuit), so it writes
        //     NO ledger row REGARDLESS of its own outcome (it errors here only because the seeded local-file repo has
        //     no real provider API to list PRs against — the short-circuit fires before any of that). We assert the
        //     "no row" property below, not the tool's success.
        await client.CallToolAsync(1, "git.list_prs", new { repositoryId = repoId.ToString(), state = "open" });

        // (b) a SIDE-EFFECTING governed tool (agent.run_command) — routes through the ledger → exactly one terminal row.
        var writeCall = await client.CallToolAsync(2, "agent.run_command", new { repositoryId = repoId.ToString(), command = "cat", args = new[] { "README.md" } });
        writeCall.GetProperty("isError").GetBoolean().ShouldBeFalse(customMessage: "the team-A repo resolves and the command runs in its clone");

        await run;   // the sleeping harness returns → the run completes; the ledger rows persist

        // The audit query (GetForRunAsync) surfaces EXACTLY the side-effecting call — proving the governance loop ran
        // end-to-end in a real executor run, and that the read-only call was NOT tracked (the IsReadOnly short-circuit).
        using var scope = _fixture.BeginScope();
        var rows = await scope.Resolve<IToolCallLedgerService>().GetForRunAsync(runId, teamId, CancellationToken.None);

        var row = rows.ShouldHaveSingleItem();
        row.ToolKind.ShouldBe("agent.run_command", customMessage: "only the side-effecting tool is ledger-tracked; the read-only call wrote no row");
        row.Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
        rows.ShouldNotContain(r => r.ToolKind == "git.list_prs", customMessage: "a read-only tool is served BEFORE the ledger — never a ledger row (the IsReadOnly short-circuit), even when the tool itself errors");
    }

    [Fact]
    public async Task A_missing_proxy_under_governance_still_fails_closed_no_declaration_no_ledger_run_succeeds()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Unleashed);

        // Endpoint + governance ON but the proxy binary is missing → fail-closed: no declaration is written (the CLI
        // would have no MCP server to reach), and the run still completes (the fabric is optional infra). The clear
        // per-run Warning is logged by BuildMcpWiring; the boot diagnostic (LogMcpProxyReadiness) is unit-pinned.
        await ExecuteAsync(runId, new DeclaringScriptedHarness("printf 'done\\n'"), mcpEnabled: true, proxyPresent: false, governanceEnabled: true);

        var declarationPath = Path.Combine(LocalProcessRunner.SpoolDirectoryFor(runId.ToString("N")), "agent-home", ".mcp.json");
        File.Exists(declarationPath).ShouldBeFalse(customMessage: "a missing proxy binary must fail closed — no declaration pointing at it");

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IToolCallLedgerService>().GetForRunAsync(runId, teamId, CancellationToken.None)).ShouldBeEmpty(customMessage: "no MCP server reachable → no tool calls → no ledger rows");
        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded, customMessage: "the tool fabric is optional infra; a missing proxy binary does not fail the run");
    }

    // ── Driving the REAL executor ───────────────────────────────────────────

    /// <summary>Resolve the connect-registry SINGLETON from the fixture; it is the same instance the executor's MCP scope registers into.</summary>
    private FlagScope ConnectRegistryFromFixture()
    {
        var scope = _fixture.BeginScope();
        return new FlagScope(scope, scope.Resolve<IAgentMcpConnectRegistry>());
    }

    /// <summary>The connect-registry SINGLETON from the governance-on container — the same instance a run driven through that container (<c>useGovernanceContainer</c>) registers its endpoint into.</summary>
    private FlagScope ConnectRegistryFromGovernanceContainer()
    {
        var scope = _fixture.BeginGovernanceOnScope();
        return new FlagScope(scope, scope.Resolve<IAgentMcpConnectRegistry>());
    }

    /// <summary>Start the real ExecuteAsync (flag ON) on a background task so the test can drive JSON-RPC while the harness sleeps.</summary>
    // ── B2: a REAL agent RAISES a decision over the endpoint → parks → is answered → resumes → exits clean ──

    [Fact]
    public async Task A_real_agent_raising_a_decision_parks_blocks_is_answered_then_resumes_and_exits_clean()
    {
        // Real-scenario coverage B2 — the deepest whole-interaction edge: a REAL agent PROCESS raises a real
        // decision.request through the real per-run MCP endpoint (via the real codespace-mcp proxy), the durable
        // decision substrate PARKS + blocks it, a human answers via the queue, and the SAME agent process RESUMES past
        // the blocked call and EXITS 0. Joins AgentMcpEndpointFlowTests' real-process MCP transport with the decision
        // substrate McpDecisionFlowTests proves at the handler level. Five gates (owner acceptance): (1) the decision
        // ledger row is never lost; (2) the answer is exactly-once; (3) the agent does not re-run its completed
        // pre-decision side effect; (4) it exits 0 after resume (Succeeded, not NeedsReview); (5) the full
        // raise→park→answer→resume lifecycle is observable.
        if (OperatingSystem.IsWindows()) return;
        if (!Socket.OSSupportsUnixDomainSockets) return;
        var proxyDll = ProxyDllPathOrNull();
        if (proxyDll is null) return;   // the build-only proxy ref should produce it; skip (not fail) for portability

        var teamId = await SeedTeamAsync();
        var ownerId = await TeamOwnerAsync(teamId);
        var runId = await CreateRunAsync(teamId, AgentAutonomyLevel.Standard);

        using var fake = new DecisionRaisingFakeCli();
        using var connects = ConnectRegistryFromGovernanceContainer();
        using var workerCts = new CancellationTokenSource();

        // The REAL executor runs the fake agent through the GOVERNANCE-ON container (so the endpoint's DI registry holds
        // decision.request) with runtime governance ON (so the handler runs the decision loop) while the endpoint is open.
        var run = Task.Run(() => ExecuteAsync(runId, new ScriptedHarness(fake.Script), mcpEnabled: true, governanceEnabled: true, useGovernanceContainer: true, cancellationToken: workerCts.Token));

        try
        {
            // The endpoint opened → hand the agent the per-run socket + token (minted only now) + the proxy dll path.
            var connect = await WaitForConnectAsync(connects, runId, run);
            fake.WriteCreds(connect.SocketPath, connect.Token, proxyDll);

            // GATE 1 — the real agent raised the decision and it PARKED durably (regardless of any chat surface).
            Guid ledgerId;
            try { (ledgerId, _) = await WaitForParkedDecisionAsync(teamId, runId); }
            catch (TimeoutException)
            {
                var rows = await ReadRunRowsDiagAsync(teamId, runId);
                throw new Exception($"B2 diag — no parked decision.\nfake debug log:\n{fake.ReadDebug()}\nledger rows: {rows}");
            }
            var parked = await ReadDecisionRowAsync(ledgerId);
            parked.Status.ShouldBe(ToolCallLedgerStatus.AwaitingApproval, "the agent's decision parks to the durable ledger");
            parked.ToolKind.ShouldBe("decision.request");
            parked.DecisionEnvelopeJson.ShouldNotBeNullOrEmpty("the parked row stashes the decision envelope durably");
            parked.DecisionEnvelopeJson!.ShouldContain(DecisionRaisingFakeCli.Question, customMessage: "the stashed envelope is the agent's REAL question, end to end through the proxy");

            // GATE 3 (pre) — the pre-decision side effect ran exactly ONCE, and the agent is BLOCKED (not resumed) yet.
            File.ReadAllLines(fake.PreMarker).Length.ShouldBe(1, "the agent did its pre-decision work exactly once before raising");
            File.Exists(fake.PostMarker).ShouldBeFalse("the agent is blocked on the unanswered decision — it has NOT resumed");

            // Answer via the queue/REST path (a human) → resolves the durable row. Routed through the governance-on
            // container so it signals the endpoint's waiter SINGLETON (the agent's fast-path resume).
            (await AnswerDecisionViaQueueAsync(ledgerId, new[] { "a" }, teamId, ownerId, useGovernanceContainer: true)).Outcome.ShouldBe(DecisionAnswerOutcome.Answered);

            var answered = await ReadDecisionRowAsync(ledgerId);
            answered.Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the answer CAS flips the durable row to Succeeded");
            JsonSerializer.Deserialize<DecisionAnswer>(answered.ResultJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!
                .SelectedOptions.ShouldBe(new[] { "a" }, "the recorded answer is the human's chosen option");

            // GATE 2 — a SECOND answer is an idempotent no-op (exactly-once); the first answer stands byte-identical.
            (await AnswerDecisionViaQueueAsync(ledgerId, new[] { "b" }, teamId, ownerId, useGovernanceContainer: true)).Outcome.ShouldBe(DecisionAnswerOutcome.AlreadyResolved, "exactly-once: the second answer never re-applies");
            (await ReadDecisionRowAsync(ledgerId)).ResultJson.ShouldBe(answered.ResultJson, "the first answer stands — the row is byte-identical after the no-op");

            // GATE 4 — the agent process RESUMED past the now-answered call and exited 0 → the run is Succeeded, not NeedsReview.
            await run.WaitAsync(TimeSpan.FromSeconds(60));
            File.ReadAllLines(fake.PostMarker).Length.ShouldBe(1, "the agent resumed after the answer and recorded its post-decision marker exactly once (APPENDED, so a double-resume would show >1)");
            File.ReadAllLines(fake.PreMarker).Length.ShouldBe(1, "GATE 3: resuming NEVER re-ran the completed pre-decision side effect");

            (await ReadAgentRunStatusAsync(runId)).ShouldBe(AgentRunStatus.Succeeded, "GATE 4: the resumed agent exited 0 → Succeeded, never NeedsReview");
            // GATE 5: the full lifecycle is observable — the parked row carried the envelope, the answer recorded the verdict, the run completed.
        }
        finally
        {
            workerCts.Cancel();
            try { await run; } catch { /* cancelled / already completed */ }
        }
    }

    /// <summary>The team's owner user id — the human actor that answers the agent's decision via the queue.</summary>
    private async Task<Guid> TeamOwnerAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().Team.AsNoTracking().Where(t => t.Id == teamId).Select(t => t.OwnerUserId).SingleAsync();
    }

    /// <summary>Poll for the run's parked decision.request ledger row — the durable queue surface, present regardless of any chat card (Slice 0). Mirrors McpDecisionFlowTests.WaitForParkedDecisionAsync.</summary>
    private async Task<(Guid LedgerId, Guid? ApprovalMessageId)> WaitForParkedDecisionAsync(Guid teamId, Guid runId)
    {
        for (var i = 0; i < 300; i++)   // ~15s budget under full-suite Postgres contention + the proxy launch
        {
            using (var scope = _fixture.BeginScope())
            {
                var row = await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking()
                    // Require the stashed envelope, NOT just AwaitingApproval: ParkDecisionAsync flips the status and then
                    // SetDecisionEnvelopeAsync stashes the envelope in a SEPARATE write, so under full-suite Postgres
                    // contention a status-only poll can catch the row in the window before the envelope lands (a real race
                    // → DecisionEnvelopeJson null). Waiting for the envelope makes the parked state the test reads atomic.
                    .Where(l => l.AgentRunId == runId && l.TeamId == teamId && l.ToolKind == "decision.request" && l.Status == ToolCallLedgerStatus.AwaitingApproval && l.DecisionEnvelopeJson != null)
                    .Select(l => new { l.Id, l.ApprovalMessageId })
                    .FirstOrDefaultAsync();

                if (row is not null) return (row.Id, row.ApprovalMessageId);
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"No parked decision for run {runId} within 15s — the real agent's decision.request must park to the durable ledger.");
    }

    private async Task<ToolCallLedger> ReadDecisionRowAsync(Guid ledgerId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
    }

    private async Task<string> ReadRunRowsDiagAsync(Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var rows = await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking()
            .Where(l => l.AgentRunId == runId && l.TeamId == teamId)
            .Select(l => new { l.ToolKind, l.Status, l.ResultJson }).ToListAsync();
        return rows.Count == 0 ? "(none)" : string.Join(" | ", rows.Select(r => $"{r.ToolKind}:{r.Status}:{r.ResultJson}"));
    }

    private async Task<AnswerDecisionResult> AnswerDecisionViaQueueAsync(Guid decisionId, IReadOnlyList<string> selectedOptions, Guid teamId, Guid actorUserId, bool useGovernanceContainer = false)
    {
        // The answer signals the blocked endpoint via the per-container IToolApprovalWaiterRegistry SINGLETON, so it must
        // run through the SAME container the endpoint opened in — else the agent only wakes when the 600s durable bound
        // elapses (the DB row is the authority, but the in-memory waiter is the fast-path the test's 60s budget relies on).
        using var scope = useGovernanceContainer ? _fixture.BeginGovernanceOnScope() : _fixture.BeginScope();
        return await scope.Resolve<IDecisionAnswerService>().AnswerAsync(decisionId, selectedOptions, null, teamId, actorUserId, CancellationToken.None);
    }

    private async Task<AgentRunStatus> ReadAgentRunStatusAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status;
    }

    private Task RunExecutorInBackground(Guid runId, IAgentHarness harness, bool governanceEnabled = false) => Task.Run(() => ExecuteAsync(runId, harness, mcpEnabled: true, governanceEnabled: governanceEnabled));

    /// <summary>
    /// Drive the REAL ExecuteAsync. The wiring is FOLDED into the single endpoint flag (FIX 4): when it's on AND the
    /// harness declares an MCP server AND the proxy binary exists, a declaration is written — no second flag. The proxy
    /// binary isn't copied into the test bin, so when <paramref name="proxyPresent"/> we point <c>CODESPACE_MCP_PROXY_PATH</c>
    /// at a real existing stand-in (the test only File.Exists-checks it; the scripted harness runs /bin/sh, not the proxy);
    /// when false we point it at a missing path to exercise the fail-closed "no declaration" branch.
    /// </summary>
    private async Task ExecuteAsync(Guid runId, IAgentHarness harness, bool mcpEnabled, bool proxyPresent = true, bool governanceEnabled = false, bool useGovernanceContainer = false, CancellationToken cancellationToken = default)
    {
        var previousEndpoint = Environment.GetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar);
        var previousProxy = Environment.GetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar);
        var previousGovernance = Environment.GetEnvironmentVariable(McpRequestHandler.GovernanceEnabledEnvVar);
        Environment.SetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar, mcpEnabled ? "true" : null);
        // The endpoint now opens for EVERY run (read-only by default, full on mcpEnabled), so the proxy is wanted
        // regardless of the full-fabric flag — drive its presence by proxyPresent alone, not by mcpEnabled.
        Environment.SetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar, proxyPresent ? StandInProxyPath() : "/nonexistent/codespace-mcp");
        // The endpoint reads IsGovernanceEnabled() once at open, so the loop only runs E2E when this is set ON too.
        Environment.SetEnvironmentVariable(McpRequestHandler.GovernanceEnabledEnvVar, governanceEnabled ? "true" : null);

        try
        {
            // useGovernanceContainer routes the run through the SECOND, governance-on container so the endpoint's DI
            // IAgentToolRegistry actually contains decision.request (registry composition is fixed at container build).
            using var scope = useGovernanceContainer ? _fixture.BeginGovernanceOnScope() : _fixture.BeginScope();
            await NewExecutor(scope, harness).ExecuteAsync(runId, cancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar, previousEndpoint);
            Environment.SetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar, previousProxy);
            Environment.SetEnvironmentVariable(McpRequestHandler.GovernanceEnabledEnvVar, previousGovernance);
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
        new HarnessModelReconciler(new AgentHarnessRegistry(new[] { harness }), scope.Resolve<CodeSpaceDbContext>()),
        scope.Resolve<ISandboxRunnerRegistry>(),
        scope.Resolve<IAgentWorkspaceResolver>(),
        scope.Resolve<IModelCredentialResolver>(),
        scope.Resolve<IWorkspaceProviderRegistry>(),
        scope.Resolve<IAgentRunCompletionNotifier>(),
        scope.Resolve<IServiceScopeFactory>(),
        scope.Resolve<CodeSpaceDbContext>(),
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

    /// <summary>
    /// The REAL <c>codespace-mcp</c> proxy as a spawned child process (<c>dotnet codespace-mcp.dll --proxy</c>) — exactly
    /// what a Codex/Claude CLI launches. The socket path + token are staged into its ENV (the proxy auto-sends the token
    /// as line 1, so the test must NOT send it); the test then writes newline-delimited JSON-RPC to its STDIN and reads
    /// responses from its STDOUT. Dispose closes stdin, kills the process tree (even on the failure path), and drains
    /// STDERR so a timeout failure can name what the proxy reported.
    /// </summary>
    private sealed class RealProxyProcess : IAsyncDisposable
    {
        // The proxy reads these from its env (McpProxyEnv.SocketEnvVar / TokenEnvVar — internal to CodeSpace.Mcp, which is
        // referenced build-only so the literals are hardcoded here; pinned by McpProxyTests.Env_var_name_literals_are_pinned).
        private const string SocketEnvVar = "CODESPACE_MCP_SOCKET";
        private const string TokenEnvVar = "CODESPACE_RUN_TOKEN";

        private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(20);

        private readonly Process _process;
        private readonly StringBuilder _stderr = new();

        private RealProxyProcess(Process process)
        {
            _process = process;
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_stderr) _stderr.AppendLine(e.Data); };
            _process.BeginErrorReadLine();
        }

        public static RealProxyProcess Start(string proxyDllPath, string socketPath, string token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(proxyDllPath);
            psi.ArgumentList.Add("--proxy");
            psi.Environment[SocketEnvVar] = socketPath;
            psi.Environment[TokenEnvVar] = token;

            var process = Process.Start(psi) ?? throw new InvalidOperationException("could not spawn the codespace-mcp proxy process");
            return new RealProxyProcess(process);
        }

        /// <summary>Write one JSON-RPC request line to the proxy's stdin and read one response line from its stdout (timeout-bounded).</summary>
        public async Task<JsonElement> ExchangeAsync(int id, string method, object? @params = null)
        {
            var line = await SendThenReadAsync(Serialize(id, method, @params), method).ConfigureAwait(false)
                ?? throw await DiagnosticAsync($"the proxy closed stdout before a response to '{method}' (id {id})").ConfigureAwait(false);
            return JsonDocument.Parse(line).RootElement.Clone();
        }

        /// <summary>tools/call over the proxy → the unwrapped <c>result</c> element (mirrors McpClient.CallToolAsync).</summary>
        public async Task<JsonElement> CallToolAsync(int id, string name, object arguments)
        {
            var call = await ExchangeAsync(id, "tools/call", new { name, arguments }).ConfigureAwait(false);
            return call.GetProperty("result");
        }

        /// <summary>Send a NOTIFICATION (no id) and read NOTHING — the server emits no response for a notification.</summary>
        public async Task SendNotificationAsync(string method)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method })).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>Like ExchangeAsync but returns null on EOF / broken pipe (the wrong-token path: the endpoint closes after the bad token line).</summary>
        public async Task<string?> TryExchangeAsync(int id, string method)
        {
            try { return await SendThenReadAsync(Serialize(id, method, null), method).ConfigureAwait(false); }
            catch (IOException) { return null; }
        }

        /// <summary>Close stdin (the happy-path completion signal) and assert the proxy exits 0 — its stdin pump hits EOF, it tears the socket down and returns.</summary>
        public async Task CompleteAndAssertCleanExitAsync()
        {
            _process.StandardInput.Close();

            if (!await WaitForExitAsync().ConfigureAwait(false))
                throw await DiagnosticAsync("the proxy did not exit within 20s after stdin was closed").ConfigureAwait(false);

            if (_process.ExitCode != 0)
                throw await DiagnosticAsync($"the proxy exited {_process.ExitCode} on the happy path (expected 0)").ConfigureAwait(false);
        }

        /// <summary>Wait (timeout-bounded) for the process to exit; true if it did.</summary>
        public async Task<bool> WaitForExitAsync()
        {
            try { await _process.WaitForExitAsync().WaitAsync(ExchangeTimeout).ConfigureAwait(false); return true; }
            catch (TimeoutException) { return false; }
        }

        private async Task<string?> SendThenReadAsync(string request, string method)
        {
            await _process.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);

            try { return await _process.StandardOutput.ReadLineAsync().WaitAsync(ExchangeTimeout).ConfigureAwait(false); }
            catch (TimeoutException) { throw await DiagnosticAsync($"timed out after 20s awaiting the proxy's stdout response to '{method}'").ConfigureAwait(false); }
        }

        private static string Serialize(int id, string method, object? @params) =>
            @params is null
                ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method })
                : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });

        /// <summary>Build a descriptive failure that names the watched signal AND surfaces the proxy's drained STDERR for manual diagnosis (Rule 12.10).</summary>
        private async Task<TimeoutException> DiagnosticAsync(string what)
        {
            await Task.Delay(50).ConfigureAwait(false);   // let any final stderr line flush before we snapshot it
            string stderr;
            lock (_stderr) stderr = _stderr.ToString();
            return new TimeoutException($"Real codespace-mcp proxy E2E: {what}. Proxy STDERR was:\n{(stderr.Length == 0 ? "(empty)" : stderr)}");
        }

        public async ValueTask DisposeAsync()
        {
            try { _process.StandardInput.Close(); } catch { /* already closed / broken pipe */ }
            try { _process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { /* best-effort */ }
            _process.Dispose();
        }
    }

    // ── Driving the REAL codespace-mcp proxy BINARY over the real UDS ────────

    /// <summary>
    /// Resolve the built <c>codespace-mcp.dll</c> path, or null to SKIP. From the test bin (<c>AppContext.BaseDirectory</c>,
    /// e.g. <c>.../tests/CodeSpace.IntegrationTests/bin/Debug/net10.0/</c>) we walk UP to the directory holding
    /// <c>CodeSpace.sln</c>, then build <c>&lt;root&gt;/src/CodeSpace.Mcp/bin/&lt;Configuration&gt;/net10.0/codespace-mcp.dll</c>,
    /// deriving <c>&lt;Configuration&gt;</c> from the test bin path. A build-only ProjectReference (csproj) guarantees the
    /// dll is produced before these tests run; we still skip (not fail) if it's absent so the suite stays portable.
    /// </summary>
    private static string? ProxyDllPathOrNull()
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodeSpace.sln"))) dir = dir.Parent;
        if (dir is null) return null;

        var dll = Path.Combine(dir.FullName, "src", "CodeSpace.Mcp", "bin", configuration, "net10.0", "codespace-mcp.dll");
        return File.Exists(dll) ? dll : null;
    }

    private static string[] ToolNames(JsonElement listResponse) =>
        listResponse.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();

    private static string Text(JsonElement toolResult) => toolResult.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

    // ── Seeding (mirrors McpToolTeamScopeFlowTests + AgentRunExecutorTests) ──

    private async Task<Guid> CreateRunAsync(Guid teamId, AgentAutonomyLevel autonomy, IReadOnlyList<string>? tools = null)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "scripted", Harness = "scripted", Model = "test-model", TimeoutSeconds = 1800, Autonomy = autonomy, Tools = tools },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
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

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

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

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" };
    }

    /// <summary>
    /// A declaring scripted harness that ALSO mirrors ClaudeCodeHarness's allow-list projection: it CAPTURES the
    /// <c>task.Tools</c> the executor hands BuildInvocation (so the test can assert the augmented mcp__codespace__*
    /// names landed). It declares an MCP server + a config home so the wiring is written (which triggers the
    /// augmentation), and runs /bin/sh — no real CLI. This proves the augmentation end-to-end in a REAL executor run.
    /// </summary>
    private sealed class AllowedToolsCapturingHarness : IAgentHarness, IMcpHarnessDeclaration
    {
        private readonly string _script;

        public AllowedToolsCapturingHarness(string script) => _script = script;

        public IReadOnlyList<string>? CapturedTools { get; private set; }

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task)
        {
            CapturedTools = task.Tools;   // the executor augments task.Tools BEFORE calling this (when the wiring landed)

            return new SandboxSpec
            {
                Command = "/bin/sh",
                Args = new[] { "-c", _script },
                WorkingDirectory = task.WorkspaceDirectory,
                TimeoutSeconds = task.TimeoutSeconds,
                ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR" },
            };
        }

        public McpHarnessDeclaration BuildMcpDeclaration(McpDeclarationContext context) => new() { RelativeFileName = ".mcp.json", Content = McpDeclarationWriter.RenderClaudeJson(context) };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" };
    }
}
