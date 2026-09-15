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

    [Theory]
    [InlineData(CodeSpace.Messages.Tasks.TaskProjectionKinds.PlanMapSynth, "map-branch")]
    [InlineData(CodeSpace.Messages.Tasks.TaskProjectionKinds.Supervisor, BudgetKinds.AgentAttempt)]
    public async Task A_fan_out_lanes_agent_records_unbudgeted_instead_of_claiming_the_cap_a_second_time(string projectionKind, string ancestorKind)
    {
        if (OperatingSystem.IsWindows()) return;

        // Both fan-out lanes admit the work BEFORE the agent run exists — the map reserves cap÷N per branch
        // (WorkflowEngine.AdmitBranchAsync), the supervisor one agent-attempt per staged agent
        // (RealSupervisorActionExecutor) against the SAME workflow run id the agent run is bound to. So the money is
        // already claimed on this agent's behalf.
        // MUTATION THIS CATCHES: admitting every agent against the run cap regardless of projection. Both fixtures
        // are a capped run mid-fan-out — with the projection check gone, the agent's own claim is summed ALONGSIDE
        // the ancestor's by CommittedInTxAsync, so the run and team caps see this CLI twice and ReserveAsync refuses
        // (committed == cap), killing every agent a capped Standard or Deep run staged. No other test in the suite
        // drives a fan-out agent through the real executor.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedRoutedWorkflowRunAsync(teamId, WorkflowsTestSeed.RouteJsonFor(projectionKind, capUsd: 5m));

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IBudgetLedger>().ReserveAsync(workflowRunId, teamId, ancestorKind, "the-ancestors-own-claim", 5m, 5m, "prices-v1", null, null, CancellationToken.None))
                .Admitted.ShouldBeTrue("the fan-out's own admission runs first and claims the run's ceiling");

        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: 5m, model: "claude-opus-4-8", workflowRunId: workflowRunId);

        await ExecuteAsync(runId, new UsageReportingHarness(PricedUsageScript));

        // The real runner drives a real process, so a priced result IS the proof the CLI ran rather than being refused.
        (await PersistedRunAsync(runId)).Status.ShouldBe(AgentRunStatus.Succeeded, "an agent the fan-out already admitted must still run — diagnose a failure here by comparing this run's budget_reservation kinds against its route's projectionKind");
        (await PersistedResultAsync(runId)).CostUsd.ShouldBe(PricedUsageCostUsd, "a refused launch would have landed a zero-cost run_budget_exhausted result instead of a priced one");

        (await ReservationOfAsync(workflowRunId, runId)).ShouldBeNull("a fan-out lane's agent mints no enforcing claim — the ancestor row above already holds this money");

        var observed = await ReservationOfAsync(workflowRunId, runId, $"{BudgetKinds.UnbudgetedPrefix}{BudgetKinds.AgentRunMonitored}");
        observed.ShouldNotBeNull("it still RECORDS what it spent — the Room must not go blind on the fan-out lanes");
        observed.SettledUsd.ShouldBe(PricedUsageCostUsd);
        observed.CapUsd.ShouldBeNull();

        using var read = _fixture.BeginScope();
        (await read.Resolve<IBudgetLedger>().CommittedUsdAsync(workflowRunId, teamId, CancellationToken.None))
            .ShouldBe(5m, "the physical CLI is counted ONCE — by the ancestor's claim. A monitored row here would double it against both the run and the team cap");
    }

    [Fact]
    public async Task A_second_run_claims_what_the_first_left_instead_of_the_whole_ceiling()
    {
        if (OperatingSystem.IsWindows()) return;

        // MUTATION THIS CATCHES: estimating `min(task.MaxCostUsd ?? cap, cap)` — the whole ceiling rather than the
        // remainder. The first run then holds all $5 even after settling at $0.50, and the second agent on the same
        // workflow run is refused for money nobody spent.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: 5m);

        var first = await CreateScriptedRunAsync(teamId, maxCostUsd: 5m, model: "claude-opus-4-8", workflowRunId: workflowRunId);
        await ExecuteAsync(first, new UsageReportingHarness(PricedUsageScript));

        var second = await CreateScriptedRunAsync(teamId, maxCostUsd: 5m, model: "claude-opus-4-8", workflowRunId: workflowRunId);
        await ExecuteAsync(second, new UsageReportingHarness(PricedUsageScript));

        (await PersistedRunAsync(second)).Status.ShouldBe(AgentRunStatus.Succeeded, "$0.50 of a $5 ceiling is spent — the second agent must be admissible against the $4.50 remaining");

        var row = await ReservationOfAsync(workflowRunId, second);
        row.ShouldNotBeNull();
        row.ReservedUsd.ShouldBe(4.5m, "it claims the REMAINDER, not the ceiling");
        row.SettledUsd.ShouldBe(PricedUsageCostUsd);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IBudgetLedger>().CommittedUsdAsync(workflowRunId, teamId, CancellationToken.None))
            .ShouldBe(1m, "two settled runs at $0.50 each — the ledger holds the observed total, not two ceilings");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_run_authorized_to_spend_nothing_is_refused_typed(int maxCostUsd)
    {
        if (OperatingSystem.IsWindows()) return;

        // MUTATION THIS CATCHES: flooring the estimate at 0 and reserving anyway. A zero ceiling would then be
        // admitted vacuously (committed + 0 is not > cap) and the CLI would launch for a run authorized to spend
        // nothing; a NEGATIVE one reached the ledger's own ArgumentOutOfRangeException and surfaced as the untyped
        // `executor-error`, which a node's retry policy treats as a transient worth re-buying.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: 5m);
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: maxCostUsd, model: "claude-opus-4-8", workflowRunId: workflowRunId);
        var runner = new SpecRecordingDurableRunner();

        await ExecuteAsync(runId, new UsageReportingHarness(PricedUsageScript), runners: new SandboxRunnerRegistry(new ISandboxRunner[] { runner }));

        runner.Launched.ShouldBeNull("a ceiling of zero or less authorizes no spend at all — check the run's result exit reason to diagnose");

        var result = await PersistedResultAsync(runId);
        result.ExitReason.ShouldBe(CodeSpace.Messages.Failures.FailureCodes.RunBudgetExhausted, "it is a budget refusal, not an executor fault");
        result.Error.ShouldNotBeNull().ShouldContain("spend nothing", Case.Insensitive, "the operator must read WHICH ceiling refused — a spent run cap and a zero ceiling have different remedies");

        (await ReservationOfAsync(workflowRunId, runId)).ShouldBeNull();
    }

    [Fact]
    public async Task A_run_that_lands_through_reattach_settles_the_claim_its_launch_minted()
    {
        if (OperatingSystem.IsWindows()) return;

        // The normal shape after a deploy or worker restart: the worker that reserved is gone, and a DIFFERENT one
        // lands the run. MUTATION THIS CATCHES: settling only on the ExecuteAsync arms. The claim would then stay
        // Reserved until its deadline, the expiry sweep would move it to Indeterminate — which still HOLDS the whole
        // estimate in every committed sum — so each restart would permanently burn a run's and a team's cap.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: 5m);
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: 5m, model: "claude-opus-4-8", workflowRunId: workflowRunId);

        // Exactly the row the launch mints (same kind, same scope key) — this worker never saw the reserve.
        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IBudgetLedger>().ReserveAsync(workflowRunId, teamId, BudgetKinds.AgentRunMonitored, runId.ToString("N"), 5m, 5m, "prices-v1", null, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None))
                .Admitted.ShouldBeTrue();

        var reservation = await StrandRunningWithExitedProcessAsync(runId, teamId);

        await ReattachAsync(reservation, new UsageReportingHarness(PricedUsageScript));

        (await PersistedRunAsync(runId)).Status.ShouldBe(AgentRunStatus.Succeeded, "the re-attached observer tailed the exited process and landed the run");

        var row = await ReservationOfAsync(workflowRunId, runId);
        row.ShouldNotBeNull();
        row.State.ShouldBe(BudgetReservationStates.Settled, "the terminal a re-attach reaches closes the claim the launch minted — diagnose by checking whether AgentRunExecutor threaded a rebuilt claim into CompleteAndNotifyAsync");
        row.SettledUsd.ShouldBe(PricedUsageCostUsd);

        using var read = _fixture.BeginScope();
        (await read.Resolve<IBudgetLedger>().CommittedUsdAsync(workflowRunId, teamId, CancellationToken.None))
            .ShouldBe(PricedUsageCostUsd, "the $4.50 this run did not spend is back in the run's headroom, not held until a deadline");
    }

    [Fact]
    public async Task A_second_settle_on_a_landed_claim_is_a_no_op()
    {
        // A run can reach a terminal twice (an executor terminal, then a re-attach that lands the same run), and the
        // second pass rebuilds the same claim. MUTATION THIS CATCHES: a settle that overwrites a confirmed receipt —
        // the later pass carries no observed cost, so it would replace a real $0.50 with Indeterminate and the run's
        // committed total would jump back to the full reserve.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: 5m);

        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();
        await ledger.ReserveAsync(workflowRunId, teamId, BudgetKinds.AgentRunMonitored, "twice", 5m, 5m, "prices-v1", null, null, CancellationToken.None);

        await ledger.SettleAsync(workflowRunId, teamId, BudgetKinds.AgentRunMonitored, "twice", PricedUsageCostUsd, CancellationToken.None);
        await ledger.SettleAsync(workflowRunId, teamId, BudgetKinds.AgentRunMonitored, "twice", null, CancellationToken.None);

        var row = await scope.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking()
            .SingleAsync(r => r.WorkflowRunId == workflowRunId && r.ScopeKey == "twice");

        row.State.ShouldBe(BudgetReservationStates.Settled, "a confirmed receipt is final — a later uncertain pass may not reopen it");
        row.SettledUsd.ShouldBe(PricedUsageCostUsd);
    }

    [Fact]
    public async Task The_settlement_sweep_closes_a_monitored_claim_whose_worker_never_came_back()
    {
        // The backstop for a claim no terminal ever reaches: the expiry sweep moves it to Indeterminate at its
        // deadline and this pass closes the bookkeeping. MUTATION THIS CATCHES: leaving AgentRunMonitored out of the
        // sweep's reconcile list — the row would sit Indeterminate forever, the one kind with no closing pass.
        // NOTE it deliberately does NOT free headroom: Reconciled still counts its reserve, exactly like every other
        // kind here. Only a settled actual releases the unspent remainder, which is why the terminal settle matters.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: 5m);

        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();
        await ledger.ReserveAsync(workflowRunId, teamId, BudgetKinds.AgentRunMonitored, "orphan", 5m, 5m, "prices-v1", null, DateTimeOffset.UtcNow.AddSeconds(-1), CancellationToken.None);

        await ledger.ExpireOverdueAsync(100, CancellationToken.None);
        await scope.Resolve<IBudgetSettlementService>().SweepAsync(100, CancellationToken.None);

        var row = await scope.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking()
            .SingleAsync(r => r.WorkflowRunId == workflowRunId && r.ScopeKey == "orphan");

        row.State.ShouldBe(BudgetReservationStates.Reconciled, "the orphan's bookkeeping is closed rather than left Indeterminate forever");
        row.SettledUsd.ShouldBeNull("closing the label never invents a bill");
    }

    // ── fixtures ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Leave the run exactly as a vanished worker leaves it: Running, with a durable handle to a REAL process that
    /// has already written its output and exited, and a lapsed lease — then reserve the re-attach the reconciler
    /// would. The re-attached observer tails that spool from offset 0 and folds a genuine result.
    /// </summary>
    private async Task<AgentRunReattachReservation> StrandRunningWithExitedProcessAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var runner = (ISandboxDurableRunner)scope.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind);

        var handle = await runner.LaunchAsync(new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", PricedUsageScript }, TimeoutSeconds = 60 }, runId.ToString("N"), CancellationToken.None);

        // Running with a durable handle — the row a vanished worker leaves behind.
        await runs.MarkRunningAsync(runId, CancellationToken.None);
        await runs.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);

        // Wait for the real process to finish writing, then lapse the lease so the re-attach can be reserved.
        while ((await runner.ProbeAsync(handle, CancellationToken.None)).State == SandboxRunState.Running)
            await Task.Delay(50);

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");

        return (await runs.ReserveReattachAsync(runId, CancellationToken.None))!;
    }

    /// <summary>A real workflow run carrying the QUICK lane's launch-stamped route provenance — the exact column and projection the single-agent builder writes, which is what makes its agent the sole claimant of the ceiling.</summary>
    private Task<Guid> SeedCappedWorkflowRunAsync(Guid teamId, decimal? capUsd) =>
        SeedRoutedWorkflowRunAsync(teamId, capUsd is { } cap ? WorkflowsTestSeed.RouteJsonWithCostCap(cap) : null);

    private async Task<Guid> SeedRoutedWorkflowRunAsync(Guid teamId, string? routePlanJson)
    {
        Guid workflowId;
        using (var scope = await WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, teamId))
            workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = $"quick-lane-{Guid.NewGuid():N}", Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = Array.Empty<WorkflowActivationInput>(), Enabled = true });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, routePlanJson: routePlanJson);
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
