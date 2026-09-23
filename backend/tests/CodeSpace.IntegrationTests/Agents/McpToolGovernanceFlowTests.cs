using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The exactly-once-side-effect proof for the governance vertical, driving the REAL McpRequestHandler + REAL
/// ToolCallLedgerService over real Postgres (the handler is the production transport mapper; the in-test counting tool
/// stands in for a side-effecting tool whose body must run exactly once). Covers: a write tool deduped across two
/// identical calls (the body runs ONCE, both responses carry the same result, exactly one Succeeded ledger row); a
/// read-only tool writing NO row; a different-args call running separately; redact-before-persist on the stored row;
/// and the flag-OFF byte-identical path (no rows, the tool runs on every call).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class McpToolGovernanceFlowTests
{
    private readonly PostgresFixture _fixture;

    public McpToolGovernanceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Two_identical_write_calls_run_the_side_effect_once_and_return_the_same_result()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        using var scope = _fixture.BeginScope();
        var handler = GovernedHandler(scope, teamId, runId, tool);

        var first = await CallToolAsync(handler, "git.open_pr", new { title = "Fix", branch = "main" });
        var second = await CallToolAsync(handler, "git.open_pr", new { title = "Fix", branch = "main" });

        tool.CallCount.ShouldBe(1, "the second identical call must DEDUP — the side effect runs exactly once");
        first.GetProperty("isError").GetBoolean().ShouldBeFalse();
        Text(second).ShouldBe(Text(first), "the dedup replays the winner's stored result verbatim");

        var rows = await scope.Resolve<IToolCallLedgerService>().GetForRunAsync(runId, teamId, CancellationToken.None);
        rows.ShouldHaveSingleItem().Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "exactly one Succeeded ledger row for the (run, key)");
    }

    [Fact]
    public async Task A_different_args_call_runs_a_second_time_with_its_own_row()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        using var scope = _fixture.BeginScope();
        var handler = GovernedHandler(scope, teamId, runId, tool);

        await CallToolAsync(handler, "git.open_pr", new { branch = "main" });
        await CallToolAsync(handler, "git.open_pr", new { branch = "release" });   // different input → different key

        tool.CallCount.ShouldBe(2, "a genuinely different intent (different args) is a different key → it runs");
        (await scope.Resolve<IToolCallLedgerService>().GetForRunAsync(runId, teamId, CancellationToken.None)).Count.ShouldBe(2, "two distinct keys → two rows");
    }

    [Fact]
    public async Task A_read_only_tool_writes_no_ledger_row()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingReadTool();

        using var scope = _fixture.BeginScope();
        var handler = GovernedHandler(scope, teamId, runId, tool);

        await CallToolAsync(handler, "git.list_prs", new { });
        await CallToolAsync(handler, "git.list_prs", new { });

        tool.CallCount.ShouldBe(2, "a read-only tool is never deduped — it runs every time");
        (await scope.Resolve<IToolCallLedgerService>().GetForRunAsync(runId, teamId, CancellationToken.None)).ShouldBeEmpty("a read-only tool is NEVER tracked in the ledger");
    }

    [Fact]
    public async Task Flag_off_writes_no_row_and_runs_the_write_tool_on_every_call()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        using var scope = _fixture.BeginScope();
        // governanceEnabled: false → byte-identical to today: no ledger, the tool runs on every call.
        var handler = new McpRequestHandler(new SingleToolRegistry(tool), AgentAutonomyLevel.Unleashed, teamId, null, runId, scope.Resolve<IToolCallLedgerService>(), 0, governanceEnabled: false);

        await CallToolAsync(handler, "git.open_pr", new { branch = "main" });
        await CallToolAsync(handler, "git.open_pr", new { branch = "main" });

        tool.CallCount.ShouldBe(2, "flag-OFF runs the tool on every call (no dedup) — byte-identical to today");
        (await scope.Resolve<IToolCallLedgerService>().GetForRunAsync(runId, teamId, CancellationToken.None)).ShouldBeEmpty("flag-OFF writes NO ledger rows");
    }

    [Fact]
    public async Task An_interrupted_call_records_a_terminal_Failed_and_a_re_call_dedups_to_it_not_InFlight()
    {
        // The BLOCKER regression: a tool call interrupted by cancellation (timeout / teardown / disconnect) must NOT
        // leave its Pending row stranded forever — it records a terminal Failed best-effort BEFORE the cancellation
        // propagates. So a subsequent identical call dedups to that terminal Failed and the model learns the call's effect
        // is unknown, rather than wedging on InFlight indefinitely (the deterministic key would otherwise hit the stranded
        // Pending row).
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        using var run = new CancellationTokenSource();
        var tool = new CancellingWriteTool(run);

        using var scope = _fixture.BeginScope();
        var handler = GovernedHandler(scope, teamId, runId, tool);

        // First call is interrupted: the run is cancelled mid-call and the tool observes it. The handler re-throws (the
        // run's cancellation must propagate) AFTER recording the terminal Failed row.
        await Should.ThrowAsync<OperationCanceledException>(() => CallToolAsync(handler, "git.open_pr", new { branch = "main" }, run.Token));

        var ledger = scope.Resolve<IToolCallLedgerService>();
        var afterInterrupt = (await ledger.GetForRunAsync(runId, teamId, CancellationToken.None)).ShouldHaveSingleItem();
        afterInterrupt.Status.ShouldBe(ToolCallLedgerStatus.Failed, "an interrupted call records a TERMINAL Failed — the Pending row is never stranded");
        afterInterrupt.Error.ShouldBe(McpRequestHandler.InterruptedToolCallError, "the recovery row says the effect is unknown and that an identical re-call replays it — never that a retry is safe: the write may have landed");

        // The re-call dedups to that terminal Failed (NOT InFlight) and returns the Failed result — the model is told the
        // prior call's effect is unknown, instead of being told forever that the call is in progress.
        tool.NextCallSucceeds = true;   // even if the tool would now succeed, dedup replays the terminal Failed
        var reCall = await CallToolAsync(handler, "git.open_pr", new { branch = "main" });

        reCall.GetProperty("isError").GetBoolean().ShouldBeTrue("the re-call dedups to the terminal Failed, not InFlight");
        Text(reCall).ShouldNotContain("in progress", customMessage: "the wedged-InFlight failure mode must NOT occur — the row is terminal");
        Text(reCall).ShouldBe(McpRequestHandler.InterruptedToolCallError, "the identical re-call replays the row's effect-unknown text");
        tool.SuccessCallCount.ShouldBe(0, "dedup replays the recorded Failed — it never re-runs the (now-succeeding) side effect");

        (await ledger.GetForRunAsync(runId, teamId, CancellationToken.None)).ShouldHaveSingleItem().Status.ShouldBe(ToolCallLedgerStatus.Failed, "still exactly one terminal Failed row after the re-call");
    }

    [Fact]
    public async Task A_tools_own_timeout_settles_its_row_Failed_and_answers_that_the_effect_is_unknown_instead_of_throwing()
    {
        // The same interruption, but the TOOL's: its own timeout fires while the run is live. The row must still settle
        // terminal Failed — and HandleAsync must ANSWER, because propagating the exception ends the framing pump and
        // closes the run's MCP connection for the rest of the run. The answer promises that an identical re-call returns
        // the same result without running the tool again; the re-call below holds it to that.
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var tool = new TimingOutWriteTool();

        using var scope = _fixture.BeginScope();
        var handler = GovernedHandler(scope, teamId, runId, tool);

        JsonElement? result = null;
        var escaped = await Record.ExceptionAsync(async () => result = await CallToolAsync(handler, "git.open_pr", new { branch = "main" }));

        escaped.ShouldBeNull("the tool's own timeout is a tool fault — HandleAsync answers it rather than propagating it as the run's cancellation");
        result!.Value.GetProperty("isError").GetBoolean().ShouldBeTrue();
        Text(result.Value).ShouldBe(McpRequestHandler.InterruptedToolCallError, "a governed write that timed out may have landed — the answer says the effect is unknown, never \"retry\"");

        var row = (await scope.Resolve<IToolCallLedgerService>().GetForRunAsync(runId, teamId, CancellationToken.None)).ShouldHaveSingleItem();
        row.Status.ShouldBe(ToolCallLedgerStatus.Failed, "the timed-out call's row is terminal — never stranded Pending, so a re-call dedups instead of wedging on InFlight");
        row.Error.ShouldBe(Text(result.Value), "the answer is exactly the text the row replays");

        var reCall = await CallToolAsync(handler, "git.open_pr", new { branch = "main" });

        Text(reCall).ShouldBe(Text(result.Value), "an identical re-call returns this same result, as the answer promised");
        tool.CallCount.ShouldBe(1, "...without running the tool again");
    }

    [Fact]
    public async Task A_governed_side_effecting_tool_on_a_null_team_run_does_not_crash_and_writes_no_row()
    {
        // FIX 3: the ledger row's team_id is NOT NULL (FK to team), so a governed write on a teamless run would hit a
        // 23503 FK violation on the dedup hot path. The handler SKIPS the ledger when _teamId is null (mirroring the
        // read-only skip) → the call behaves exactly as flag-OFF: the tool runs, NO ledger row is written, no crash.
        var teamId = await SeedTeamAsync();   // a real team only so we can team-scope the "no rows" read
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool();

        using var scope = _fixture.BeginScope();
        // governanceEnabled: true but teamId: null — the null-team skip must divert to the legacy invoke path.
        var handler = new McpRequestHandler(new SingleToolRegistry(tool), AgentAutonomyLevel.Unleashed, teamId: null, null, runId, scope.Resolve<IToolCallLedgerService>(), 0, governanceEnabled: true);

        var result = await CallToolAsync(handler, "git.open_pr", new { branch = "main" });

        result.GetProperty("isError").GetBoolean().ShouldBeFalse("a null-team governed run does not crash — it behaves as flag-OFF for that call");
        tool.CallCount.ShouldBe(1, "the tool still runs on the legacy path (no ledger)");
        (await scope.Resolve<IToolCallLedgerService>().GetForRunAsync(runId, teamId, CancellationToken.None)).ShouldBeEmpty("a null-team run writes NO ledger row — the FK-violating insert is never attempted");
    }

    [Fact]
    public async Task The_stored_ledger_result_is_redacted_before_persist()
    {
        const string secret = "SECRET-abc123";
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var tool = new CountingWriteTool($$"""{"stdout":"TOKEN={{secret}}"}""");

        using var scope = _fixture.BeginScope();
        var handler = new McpRequestHandler(new SingleToolRegistry(tool), AgentAutonomyLevel.Unleashed, teamId, new SecretRedactor(new[] { secret }), runId, scope.Resolve<IToolCallLedgerService>(), 0, governanceEnabled: true);

        await CallToolAsync(handler, "git.open_pr", new { branch = "main" });

        var row = (await scope.Resolve<IToolCallLedgerService>().GetForRunAsync(runId, teamId, CancellationToken.None)).ShouldHaveSingleItem();
        row.ResultJson!.ShouldNotContain(secret, customMessage: "the ledger stores the ALREADY-REDACTED result — no raw secret at rest");
        row.ResultJson!.ShouldContain(SecretRedactor.Placeholder);
    }

    private static McpRequestHandler GovernedHandler(ILifetimeScope scope, Guid teamId, Guid runId, IAgentTool tool) =>
        new(new SingleToolRegistry(tool), AgentAutonomyLevel.Unleashed, teamId, null, runId, scope.Resolve<IToolCallLedgerService>(), 0, governanceEnabled: true);

    private static async Task<JsonElement> CallToolAsync(McpRequestHandler handler, string name, object arguments, CancellationToken runToken = default)
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } });
        var resp = (await handler.HandleAsync(JsonDocument.Parse(request).RootElement.Clone(), runToken))!.Value;
        return resp.GetProperty("result");
    }

    private static string Text(JsonElement toolResult) => toolResult.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"gov-{userId:N}@test.local", Name = $"gov-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"gov-{teamId:N}", Name = "Gov Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    /// <summary>A side-effecting (destructive → governed) tool that COUNTS its invocations — the dedup proof asserts the count.</summary>
    private sealed class CountingWriteTool : IAgentTool
    {
        private readonly string _output;
        public CountingWriteTool(string output = """{"opened":true}""") => _output = output;

        public int CallCount { get; private set; }
        public string Kind => "git.open_pr";
        public string Description => "open a PR";
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public JsonElement OutputSchema { get; } = JsonDocument.Parse("{}").RootElement.Clone();
        public bool IsReadOnly => false;
        public bool IsDestructive => true;

        public AgentToolValidation ValidateInput(JsonElement input) => AgentToolValidation.Valid;

        public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(AgentToolResult.Ok(JsonDocument.Parse(_output).RootElement.Clone(), _output.Length));
        }
    }

    /// <summary>A side-effecting tool whose first call is interrupted by the RUN's cancellation — it cancels the run mid-call,
    /// as a run timeout / endpoint teardown / harness disconnect would — then succeeds. Proves the interrupted call records
    /// a terminal Failed (never stranded Pending) and the re-call dedups to it.</summary>
    private sealed class CancellingWriteTool : IAgentTool
    {
        private readonly CancellationTokenSource _run;
        public CancellingWriteTool(CancellationTokenSource run) => _run = run;

        public bool NextCallSucceeds { get; set; }
        public int SuccessCallCount { get; private set; }
        public string Kind => "git.open_pr";
        public string Description => "open a PR";
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public JsonElement OutputSchema { get; } = JsonDocument.Parse("{}").RootElement.Clone();
        public bool IsReadOnly => false;
        public bool IsDestructive => true;

        public AgentToolValidation ValidateInput(JsonElement input) => AgentToolValidation.Valid;

        public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken ct)
        {
            if (!NextCallSucceeds)
            {
                _run.Cancel();                       // the run is cancelled mid-call
                ct.ThrowIfCancellationRequested();   // the interruption, observed through the run's own token
            }

            SuccessCallCount++;
            return Task.FromResult(AgentToolResult.Ok(JsonDocument.Parse("""{"opened":true}""").RootElement.Clone(), 14));
        }
    }

    /// <summary>A side-effecting tool whose call hits its OWN deadline — the real TaskCanceledException a CancelAfter inside a
    /// tool throws, the shape HttpClient.Timeout produces — while the run's token stays live.</summary>
    private sealed class TimingOutWriteTool : IAgentTool
    {
        public int CallCount { get; private set; }
        public string Kind => "git.open_pr";
        public string Description => "open a PR";
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public JsonElement OutputSchema { get; } = JsonDocument.Parse("{}").RootElement.Clone();
        public bool IsReadOnly => false;
        public bool IsDestructive => true;

        public AgentToolValidation ValidateInput(JsonElement input) => AgentToolValidation.Valid;

        public async Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken ct)
        {
            CallCount++;

            using var own = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));

            await Task.Delay(Timeout.Infinite, own.Token);

            return AgentToolResult.Ok(JsonDocument.Parse("""{"opened":true}""").RootElement.Clone(), 14);
        }
    }

    /// <summary>A read-only tool — the handler must SKIP the ledger for it entirely.</summary>
    private sealed class CountingReadTool : IAgentTool
    {
        public int CallCount { get; private set; }
        public string Kind => "git.list_prs";
        public string Description => "list PRs";
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public JsonElement OutputSchema { get; } = JsonDocument.Parse("{}").RootElement.Clone();
        public bool IsReadOnly => true;
        public bool IsDestructive => false;

        public AgentToolValidation ValidateInput(JsonElement input) => AgentToolValidation.Valid;

        public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(AgentToolResult.Ok(JsonDocument.Parse("""{"prs":[]}""").RootElement.Clone(), 9));
        }
    }

    private sealed class SingleToolRegistry : IAgentToolRegistry
    {
        private readonly IAgentTool _tool;
        public SingleToolRegistry(IAgentTool tool) => _tool = tool;
        public IReadOnlyList<IAgentTool> All => new[] { _tool };
        public IAgentTool? Resolve(string kind) => kind == _tool.Kind ? _tool : null;
    }
}
