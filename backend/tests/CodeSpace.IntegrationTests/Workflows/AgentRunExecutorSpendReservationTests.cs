using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Budget;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the real <c>LocalProcessRunner</c> and a scripted CLI): 5c — the quick lane
/// RESERVES its agent's spend before the CLI starts, and settles that claim at the terminal fold.
///
/// <para>The hole these close: <c>AgentRunBudget.Apply</c> prices a coding CLI's usage only AFTER the process has
/// exited, so every dollar the quick lane spent was invisible to admission and to the TEAM cap until the money was
/// gone — and a launch over an already-exhausted cap still ran. Every test below asserts on the ONE
/// <c>budget_reservation</c> row its own run owns (never a sweep tally), because the sweeps are bounded and global.</para>
///
/// <para>Reservation here is admission, not enforcement: an opaque CLI reports usage only at exit, so the settle
/// deliberately records an OVERSHOOT rather than clamping to the estimate — see
/// <see cref="Observed_over_reserved_settles_up_and_stays_monitored"/>.</para>
/// </summary>
public partial class AgentRunExecutorTests
{
    /// <summary>100k input tokens on <c>claude-opus-4-8</c> ($5/M in) — the observed spend every priced test below folds to.</summary>
    private const string PricedUsageScript = """printf 'working on it\n{"type":"token_count","info":{"total_token_usage":{"input_tokens":100000,"output_tokens":0}}}\n'""";
    private const decimal PricedUsageCostUsd = 0.5m;

    [Fact]
    public async Task A_quick_lane_run_reserves_before_the_CLI_and_settles_to_observed()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: 5m);
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: 5m, model: "claude-opus-4-8", workflowRunId: workflowRunId);

        await ExecuteAsync(runId, new UsageReportingHarness(PricedUsageScript));

        var row = await ReservationOfAsync(workflowRunId, runId);

        // MUTATION: skipping the terminal settle. The row would stay Reserved holding the full $5, the expiry sweep
        // would later move it to Indeterminate, and the run's real $0.50 would never reach the team's committed sum.
        row.ShouldNotBeNull("the CLI's spend must be claimed BEFORE the process starts — a row that only appears after the fold is monitoring, not admission");
        row.Kind.ShouldBe(BudgetKinds.AgentRunMonitored);
        row.State.ShouldBe(BudgetReservationStates.Settled);
        row.ReservedUsd.ShouldBe(5m, "the estimate is the monitored ceiling this CLI was authorized to spend");
        row.SettledUsd.ShouldBe(PricedUsageCostUsd, "the terminal fold settles to what the CLI actually reported, not to the estimate");
        row.CapUsd.ShouldBe(5m, "admission is against the RUN-grain cap, the same grain ReserveAsync sums against");

        // The price_version is the rates admission used — the audit stamp the agent-run plane never had.
        row.PriceVersion.ShouldBe(AgentCostPricing.SnapshotFor("claude-opus-4-8").ShouldNotBeNull().Digest,
            customMessage: "a reservation stamped with a constant cannot tell an admission made under today's prices from one made under last month's");

        var result = await PersistedResultAsync(runId);
        result.CostUsd.ShouldBe(PricedUsageCostUsd);
        result.PriceSnapshot.ShouldNotBeNull().Source.ShouldBe(ModelPriceSources.BuiltIn, "nothing overrides opus here, so the seeded table is the honest source");
    }

    [Fact]
    public async Task Observed_over_reserved_settles_up_and_stays_monitored()
    {
        if (OperatingSystem.IsWindows()) return;

        // A coding CLI has NO wire ceiling — it reports usage only at exit — so an overshoot is a real bill.
        // MUTATION: clamping the settle to the estimate (Math.Min with ReservedUsd). The ledger would then record
        // $0.10 for a run that cost $0.50, and the team's cap would under-count every over-running agent forever.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: 5m);
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: 0.1m, model: "claude-opus-4-8", workflowRunId: workflowRunId);

        await ExecuteAsync(runId, new UsageReportingHarness(PricedUsageScript));

        var row = await ReservationOfAsync(workflowRunId, runId);

        row.ShouldNotBeNull();
        row.ReservedUsd.ShouldBe(0.1m, "the task's own monitored ceiling is the tighter, honest estimate");
        row.SettledUsd.ShouldBe(PricedUsageCostUsd, "the settle records the overshoot — clamping down would book a bill nobody was charged");
        row.State.ShouldBe(BudgetReservationStates.Settled);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IBudgetLedger>().CommittedUsdAsync(workflowRunId, teamId, CancellationToken.None))
            .ShouldBe(PricedUsageCostUsd, "the run's committed total is the OBSERVED spend once settled, so the next admission sees the truth");
    }

    [Fact]
    public async Task Team_cap_counts_the_estimate_at_admission()
    {
        if (OperatingSystem.IsWindows()) return;

        // The cap that a per-run ceiling cannot express: two runs each comfortably under their own $5 and jointly
        // past the team's $1. The refusing evidence is the FIRST run's still-live ESTIMATE — it has not settled, so
        // only an admission that counts estimates can refuse the second.
        // MUTATION: passing 0 (or skipping the reserve) as the second run's estimate — 0.6 + 0 stays under $1 and the
        // second CLI launches past the team's ceiling.
        var teamId = await SeedTeamAsync();
        await SeedTeamCapAsync(teamId, capUsd: 1m);
        var inFlightRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: 5m);
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: 5m);

        using (var scope = _fixture.BeginScope())
        {
            var inFlight = await scope.Resolve<IBudgetLedger>().ReserveAsync(inFlightRunId, teamId, BudgetKinds.AgentRunMonitored, "an-agent-still-running", 0.6m, 5m, "prices-v1", null, null, CancellationToken.None);
            inFlight.Admitted.ShouldBeTrue("0.6 is under both its own run cap and the team's — this run must not be the refused one");
        }

        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: 0.6m, model: "claude-opus-4-8", workflowRunId: workflowRunId);
        var runner = new SpecRecordingDurableRunner();

        await ExecuteAsync(runId, new UsageReportingHarness(PricedUsageScript), runners: new SandboxRunnerRegistry(new ISandboxRunner[] { runner }));

        var run = await PersistedRunAsync(runId);
        run.Status.ShouldBe(AgentRunStatus.Failed, "0.6 + 0.6 would commit 1.2 past the team's 1.0 — the launch must not happen at all");
        runner.Launched.ShouldBeNull("the refusal has to land BEFORE the sandbox launches, or the money is already gone");

        var result = await PersistedResultAsync(runId);
        result.ExitReason.ShouldBe(CodeSpace.Messages.Failures.FailureCodes.RunBudgetExhausted, "a spent cap is Exhausted in the failure taxonomy — never the generic executor error a retry would be bought for");
        result.Error.ShouldNotBeNull().ShouldContain("team cap", Case.Insensitive, "a TEAM refusal must not read as the run exhausting its own cap — the remedies differ");
        result.CostUsd.ShouldBe(0m, "nothing was invoked, so the accounting fact is a known zero rather than an unknown");

        (await ReservationOfAsync(workflowRunId, runId)).ShouldBeNull("a refused admission mints no row to settle");
    }

    [Fact]
    public async Task Indeterminate_settles_pessimistically_at_the_reservation()
    {
        if (OperatingSystem.IsWindows()) return;

        // MUTATION: settling 0 (or the observed-but-unpriceable null collapsing to zero). A run nobody could price
        // would then FREE its whole claim, and the next admission would spend headroom this CLI may already have used.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: 2m);
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: 2m, model: "unpriced-cli-model", workflowRunId: workflowRunId);

        await ExecuteAsync(runId, new UsageReportingHarness(PricedUsageScript));

        var row = await ReservationOfAsync(workflowRunId, runId);

        row.ShouldNotBeNull();
        row.State.ShouldBe(BudgetReservationStates.Indeterminate, "an unpriceable outcome is uncertainty, not a confirmed bill");
        row.SettledUsd.ShouldBeNull("a null actual must never be written as a number — an invented figure is worse than an honest unknown");
        row.ReservedUsd.ShouldBe(2m);
        row.PriceVersion.ShouldBe(ModelPriceSnapshot.UnpricedVersion, "an admission made with no price is a different audit fact from one made under rates");

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IBudgetLedger>().CommittedUsdAsync(workflowRunId, teamId, CancellationToken.None))
            .ShouldBe(2m, "Indeterminate keeps HOLDING the reserved estimate — pessimism is the only safe direction for an unknown bill");

        (await PersistedResultAsync(runId)).CostIndeterminate.ShouldBeTrue();
    }

    [Fact]
    public async Task A_refused_admission_fails_the_run_before_launch()
    {
        if (OperatingSystem.IsWindows()) return;

        // The run's OWN cap, already spent by an earlier claim on the same workflow run.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: 1m);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IBudgetLedger>().ReserveAsync(workflowRunId, teamId, BudgetKinds.AgentRunMonitored, "an-earlier-agent", 1m, 1m, "prices-v1", null, null, CancellationToken.None))
                .Admitted.ShouldBeTrue();

        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: 1m, model: "claude-opus-4-8", workflowRunId: workflowRunId);
        var runner = new SpecRecordingDurableRunner();

        await ExecuteAsync(runId, new UsageReportingHarness(PricedUsageScript), runners: new SandboxRunnerRegistry(new ISandboxRunner[] { runner }));

        // MUTATION: admitting and launching anyway (a reserve whose refusal is logged but not acted on). The recorder
        // is the proof: it is the ONLY assertion here that a real process never started.
        runner.Launched.ShouldBeNull("a launch under an exhausted cap spends money the operator already said no to — diagnose by checking the run's budget_reservation rows for this workflow run");

        var run = await PersistedRunAsync(runId);
        run.Status.ShouldBe(AgentRunStatus.Failed);
        run.CompletedAt.ShouldNotBeNull("a refused launch still lands a terminal — never a run left Running for the reconciler to abandon");

        var result = await PersistedResultAsync(runId);
        result.ExitReason.ShouldBe(CodeSpace.Messages.Failures.FailureCodes.RunBudgetExhausted);
        result.Error.ShouldNotBeNull().ShouldContain(CodeSpace.Core.Services.Supervisor.SupervisorStopReasons.CostCapReached, Case.Insensitive, "the Room shows a cost-cap stop in the same words the supervisor lane uses");

        (await EventsOfAsync(runId, teamId)).ShouldBeEmpty("no harness may run after the ledger refuses the launch");
    }

    [Fact]
    public async Task A_bare_launch_without_a_workflow_run_is_not_reserved()
    {
        if (OperatingSystem.IsWindows()) return;

        // Owner decision: a bare launch has no run-grain cap to admit against (and no Room budget block to appear
        // in), so it reserves nothing and reads as unadmitted rather than getting an invented ceiling.
        // MUTATION: reserving against Guid.Empty or the agent run's own id would mint rows keyed to a workflow run
        // that does not exist — unreachable by every settle, sweep and Room query.
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: 5m, model: "claude-opus-4-8");

        await ExecuteAsync(runId, new UsageReportingHarness(PricedUsageScript));

        (await PersistedRunAsync(runId)).Status.ShouldBe(AgentRunStatus.Succeeded, "a bare launch is unaffected by admission — it simply is not admitted");

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking().Where(r => r.TeamId == teamId).ToListAsync())
            .ShouldBeEmpty("a run with no owning workflow run must claim nothing — a row nobody can reach is worse than no row");
    }

    [Fact]
    public async Task A_run_whose_route_declares_no_cap_records_an_unbudgeted_row_instead_of_nothing()
    {
        if (OperatingSystem.IsWindows()) return;

        // The same shape LlmBudgetGuard's null-cap path records: an operator who configured no ceiling still gets an
        // honest answer to "what did this run spend on its agent", instead of an absence.
        // MUTATION: returning early on a null cap. The Room's unbudgeted total would then silently omit the single
        // most expensive plane on the run.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: null);
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: 5m, model: "claude-opus-4-8", workflowRunId: workflowRunId);

        await ExecuteAsync(runId, new UsageReportingHarness(PricedUsageScript));

        var row = await ReservationOfAsync(workflowRunId, runId, $"{BudgetKinds.UnbudgetedPrefix}{BudgetKinds.AgentRunMonitored}");

        row.ShouldNotBeNull("a run with no declared ceiling records its spend for observability — it just never enforces anything");
        row.CapUsd.ShouldBeNull("an unbudgeted claim declares no cap, so it can neither refuse nor be refused");
        row.ReservedUsd.ShouldBe(0m, "a row that gates nothing claims nothing — the Room sums an unbudgeted reserve AS spend, so a ceiling here would report money the run has not spent");
        row.SettledUsd.ShouldBe(PricedUsageCostUsd);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IBudgetLedger>().CommittedUsdAsync(workflowRunId, teamId, CancellationToken.None))
            .ShouldBe(0m, "an unbudgeted row is excluded from every committed sum — it must never eat into another plane's real cap on the same run");
    }

    // ── fixtures ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A real workflow run carrying the launch-stamped route provenance <c>RunCostCap</c> reads a run's own ceiling back from — the exact column the quick lane's projection writes.</summary>
    private async Task<Guid> SeedCappedWorkflowRunAsync(Guid teamId, decimal? capUsd)
    {
        Guid workflowId;
        using (var scope = await WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, teamId))
            workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = $"quick-lane-{Guid.NewGuid():N}", Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = Array.Empty<WorkflowActivationInput>(), Enabled = true });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, routePlanJson: capUsd is { } cap ? WorkflowsTestSeed.RouteJsonWithCostCap(cap) : null);
    }

    private async Task SeedTeamCapAsync(Guid teamId, decimal capUsd)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.BudgetTeamCap.Add(new BudgetTeamCap { TeamId = teamId, CapUsd = capUsd, CapWindow = TeamCostCap.RollingThirtyDays });

        await db.SaveChangesAsync();
    }

    /// <summary>The ONE row this test's own run owns — never a sweep tally, which a bounded global pass can fill with other tests' rows.</summary>
    private async Task<BudgetReservation?> ReservationOfAsync(Guid workflowRunId, Guid agentRunId, string? kind = null)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking()
            .SingleOrDefaultAsync(r => r.WorkflowRunId == workflowRunId && r.Kind == (kind ?? BudgetKinds.AgentRunMonitored) && r.ScopeKey == agentRunId.ToString("N"));
    }

    private async Task<AgentRun> PersistedRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
    }

    private async Task<AgentRunResult> PersistedResultAsync(Guid runId) =>
        JsonSerializer.Deserialize<AgentRunResult>((await PersistedRunAsync(runId)).ResultJson!, AgentJson.Options)!;

    private async Task<IReadOnlyList<AgentRunEvent>> EventsOfAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, teamId, 0, CancellationToken.None);
    }
}
