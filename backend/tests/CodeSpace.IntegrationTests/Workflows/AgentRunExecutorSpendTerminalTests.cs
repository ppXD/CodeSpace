using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Failures;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres, real <c>AgentRunService</c> / <c>AgentRunReconcilerService</c> / <c>BudgetLedger</c>
/// out of the container): the two terminal writers that land an agent run WITHOUT the executor's fold must close the
/// run's spend claims themselves, and a retry of the same run must not be refused over the claim it is retrying.
///
/// <para>The hole these close: <c>CancelRunningAsync</c> and the reconciler's own terminalize flip the run's status
/// by conditional SQL and nothing else. Every <c>agent-run-monitored</c> row the launch minted stayed
/// <c>Reserved</c> — a claim that reads as money in flight for a run that is over — until its reservation deadline
/// (up to a day) let the expiry sweep move it on. Both now settle at the terminal itself: exactly, when the run's
/// own durable result recorded a cost, and Indeterminate when nothing did, which is every killed CLI (an agent
/// stopped mid-flight never reports its usage, and a reservation estimate is not a bill).</para>
/// </summary>
public partial class AgentRunExecutorTests
{
    /// <summary>The cost a priced terminal leaves behind, and the reserve every claim below is minted for.</summary>
    private const decimal TerminalObservedUsd = 0.75m;
    private const decimal TerminalClaimUsd = 5m;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cancelled_running_run_settles_its_live_spend_claim_immediately(bool resultCarriesACost)
    {
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: TerminalClaimUsd);
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: TerminalClaimUsd, model: "claude-opus-4-8", workflowRunId: workflowRunId);

        await MintLaunchClaimAsync(workflowRunId, teamId, runId);
        await StartRunningAsync(runId, resultCarriesACost);

        // The predicate starts FALSE: the claim is live right up to the cancel, so the assertion after it can only
        // pass because the cancel closed it.
        (await ReservationOfAsync(workflowRunId, runId)).ShouldNotBeNull().State
            .ShouldBe(BudgetReservationStates.Reserved, "precondition: the launch's claim must still be live when the cancel arrives");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().CancelRunningAsync(runId, "operator cancel", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None))
                .ShouldBeTrue("the run was Running at the epoch the cancel observed — if the CAS lost, this test proves nothing about settlement");

        // MUTATION THIS CATCHES: dropping AgentRunService's SettleSpendClaimsQuietlyAsync call. The row stays
        // Reserved for the whole AttemptReservationDeadline (a wall clock up to 24h + 30min) with nothing but the
        // expiry sweep to move it — a cancelled run whose ledger still says its money is in flight.
        var row = (await ReservationOfAsync(workflowRunId, runId)).ShouldNotBeNull();
        row.State.ShouldBe(resultCarriesACost ? BudgetReservationStates.Settled : BudgetReservationStates.Indeterminate,
            customMessage: "a cancel settles at its own terminal — check whether CancelRunningAsync calls the ledger AFTER its CAS wins");
        row.SettledUsd.ShouldBe(resultCarriesACost ? TerminalObservedUsd : null,
            customMessage: "the settle lands exactly what the run's own result recorded, and never invents one when it recorded nothing");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reconciler_abandoned_run_settles_its_live_spend_claim_immediately(bool resultCarriesACost)
    {
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: TerminalClaimUsd);
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: TerminalClaimUsd, model: "claude-opus-4-8", workflowRunId: workflowRunId);

        await MintLaunchClaimAsync(workflowRunId, teamId, runId);
        await StartRunningAsync(runId, resultCarriesACost);
        await StrandWithoutAHeartbeatAsync(runId);

        (await ReservationOfAsync(workflowRunId, runId)).ShouldNotBeNull().State
            .ShouldBe(BudgetReservationStates.Reserved, "precondition: the launch's claim must still be live when the sweep arrives");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        (await PersistedRunAsync(runId)).Status.ShouldBe(AgentRunStatus.Failed,
            "precondition: the sweep must have abandoned THIS run — a run it left alone settles nothing, and the claim assertion below would prove nothing");

        // MUTATION THIS CATCHES: dropping the settle from TerminalizeCandidateAsync. Same shape as the cancel — the
        // row rides to its deadline holding the run's whole remaining cap while the run itself is Failed.
        var row = (await ReservationOfAsync(workflowRunId, runId)).ShouldNotBeNull();
        row.State.ShouldBe(resultCarriesACost ? BudgetReservationStates.Settled : BudgetReservationStates.Indeterminate,
            customMessage: "the reconciler settles at its own terminal — check whether TerminalizeCandidateAsync settles AFTER its CAS wins");
        row.SettledUsd.ShouldBe(resultCarriesACost ? TerminalObservedUsd : null,
            customMessage: "an abandoned agent never reported its usage; the reserve is held pessimistically, never presented as a bill");
    }

    [Fact]
    public async Task A_cancels_spend_close_commits_on_its_own_connection()
    {
        // WHY this must not join the cancelling command's transaction: CancelRunCommand is transactional, the
        // ledger's settle takes advisory locks and writes, and a Postgres error there (a deadlock against the expiry
        // or reconcile sweeps, which touch the same rows unordered) aborts the WHOLE transaction. The kill wave
        // would then have terminated every sandbox and rolled back every status flip — dead processes, runs still
        // Running, a 500 for the operator — with the swallowed exception as the only trace. A joined advisory lock
        // would also be held for the rest of the command, serializing every admission on that workflow run.
        // MUTATION THIS CATCHES: resolving IBudgetLedger from the scoped context instead of a fresh DI scope. The
        // settle then lives inside the uncommitted transaction below and the reader never sees it.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: TerminalClaimUsd);
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: TerminalClaimUsd, model: "claude-opus-4-8", workflowRunId: workflowRunId);

        await MintLaunchClaimAsync(workflowRunId, teamId, runId);
        await StartRunningAsync(runId, withACost: false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        // Stands in for TransactionalBehavior, which opens exactly this on exactly this context for every ICommand.
        await using var ambient = await db.Database.BeginTransactionAsync();

        (await scope.Resolve<IAgentRunService>().CancelRunningAsync(runId, "operator cancel", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None)).ShouldBeTrue();

        // Read from a DIFFERENT connection, which can only see the settle if it committed independently.
        (await ReservationOfAsync(workflowRunId, runId)).ShouldNotBeNull().State
            .ShouldBe(BudgetReservationStates.Indeterminate, "the close must own its connection — diagnose by checking whether SettleSpendClaimsQuietlyAsync resolves the ledger from its own DI scope");

        await ambient.RollbackAsync();
    }

    [Fact]
    public async Task A_run_recovered_from_its_spool_settles_its_live_claim_at_that_terminal()
    {
        // The reconciler's OTHER terminal: the run had already finished unobserved, so the sweep salvages its exit
        // code into a result and lands it. That result carries no cost — the reconciler cannot decrypt the run's
        // secret to read its usage — so this is the honest Indeterminate close, at the terminal rather than a day
        // later at the deadline.
        // MUTATION THIS CATCHES: settling only on the abandon arm instead of inside TerminalizeCandidateAsync.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: TerminalClaimUsd);
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: TerminalClaimUsd, model: "claude-opus-4-8", workflowRunId: workflowRunId);

        await MintLaunchClaimAsync(workflowRunId, teamId, runId);
        await StartRunningAsync(runId, withACost: false);
        await StrandWithAFinishedSpoolAsync(runId);

        (await ReservationOfAsync(workflowRunId, runId)).ShouldNotBeNull().State
            .ShouldBe(BudgetReservationStates.Reserved, "precondition: the launch's claim must still be live when the sweep arrives");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        var run = await PersistedRunAsync(runId);
        run.Status.ShouldBe(AgentRunStatus.Succeeded, "precondition: the sweep must have RECOVERED this run from its spool, not abandoned it — otherwise this test duplicates the abandon one");
        run.Error.ShouldBeNull();

        var row = (await ReservationOfAsync(workflowRunId, runId)).ShouldNotBeNull();
        row.State.ShouldBe(BudgetReservationStates.Indeterminate, "the spool carries an exit code, never a bill");
        row.SettledUsd.ShouldBeNull();
    }

    [Fact]
    public async Task A_terminal_with_several_live_claims_settles_none_of_them_at_the_observed_figure()
    {
        // An observed figure is ONE invocation's bill. A run that left two invocations' claims live has no way to
        // attribute it, so neither row may take it — the alternative is charging one invocation for another's work.
        // MUTATION THIS CATCHES: passing actualUsd straight through to every row. Both rows would settle at $0.75,
        // booking $1.50 for a run whose result recorded $0.75 once.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: TerminalClaimUsd);
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: TerminalClaimUsd, model: "claude-opus-4-8", workflowRunId: workflowRunId);

        await MintLaunchClaimAsync(workflowRunId, teamId, runId, epoch: 1, reservedUsd: 1m);
        await MintLaunchClaimAsync(workflowRunId, teamId, runId, epoch: 2, reservedUsd: 1m);
        await StartRunningAsync(runId, withACost: true);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().CancelRunningAsync(runId, "operator cancel", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None)).ShouldBeTrue();

        foreach (var epoch in new long[] { 1, 2 })
        {
            var row = (await ReservationOfAsync(workflowRunId, runId, scopeKey: AgentRunExecutor.RunSpendScopeKey(runId, epoch, 0))).ShouldNotBeNull();
            row.State.ShouldBe(BudgetReservationStates.Indeterminate, $"epoch {epoch}'s claim cannot be given a figure that may belong to the other attempt");
            row.SettledUsd.ShouldBeNull($"epoch {epoch}");
        }
    }

    [Fact]
    public async Task A_re_dispatch_under_a_new_epoch_mints_its_own_claim_beside_the_dead_attempts_hold()
    {
        // The re-entry this slice is about is NOT a Hangfire automatic retry — HangfireRegistrarBase pins
        // AutomaticRetryAttempts to 0. It is the sliding-invisibility re-fetch: a worker stops renewing, the job
        // becomes visible again after InvisibilityTimeout, and a NEW worker claims the run under a NEW fence epoch.
        // That attempt's claim is its own, keyed by that epoch — the dead attempt ran a CLI too, and collapsing both
        // onto one row would settle them at the survivor's cost and lose the dead one's spend entirely.
        // MUTATION THIS CATCHES: the pre-attempt key shape ({runId:N}). The re-dispatch replays the dead attempt's
        // row with a recomputed deadline, the ledger refuses it "reservation-intent-mismatch", and the executor
        // renders that as run_budget_exhausted.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: TerminalClaimUsd);
        var runId = Guid.NewGuid();
        var task = new AgentTask { Goal = "scripted", Harness = "scripted", Model = "claude-opus-4-8", TimeoutSeconds = 1800, MaxCostUsd = 1m };

        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();

        var dead = await AdmitLikeTheLaunchAsync(ledger, workflowRunId, teamId, runId, task, epoch: 1);
        dead.Admitted.ShouldBeTrue("precondition: the attempt that dies must hold a real claim");

        var reDispatch = await AdmitLikeTheLaunchAsync(ledger, workflowRunId, teamId, runId, task, epoch: 2);

        reDispatch.Admitted.ShouldBeTrue("a new attempt is a new claim — diagnose by checking RunSpendScopeKey carries the fence epoch");
        reDispatch.IsReplay.ShouldBeFalse("nothing is replayed: the dead attempt's row is a DIFFERENT key that keeps holding its own reserve");
        reDispatch.ReservationId.ShouldNotBe(dead.ReservationId);

        var rows = await scope.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId && r.ScopeKey.StartsWith(runId.ToString("N"))).OrderBy(r => r.ScopeKey).ToListAsync();

        rows.Select(r => r.ScopeKey).ShouldBe([AgentRunExecutor.RunSpendScopeKey(runId, 1, 0), AgentRunExecutor.RunSpendScopeKey(runId, 2, 0)]);
        rows.Sum(r => r.ReservedUsd).ShouldBe(2m, "both attempts' CLI spend is claimed — the dead one's is unknown, not zero");
        (await ledger.CommittedUsdAsync(workflowRunId, teamId, CancellationToken.None))
            .ShouldBe(2m, "the dead attempt's hold still counts against the cap; only a settle can release it");
    }

    [Fact]
    public async Task A_re_dispatch_is_refused_by_name_when_the_dead_attempt_holds_the_whole_cap()
    {
        if (OperatingSystem.IsWindows()) return;

        // The trade-off this design accepts, stated out loud: an attempt that reserved the run's WHOLE remaining cap
        // and then died holds it until it settles, so the re-dispatch has nothing to claim. Refusing is the honest
        // outcome — that CLI may really have spent the money, and the platform never invents a figure — but "cost cap
        // reached" would send an operator hunting for spend that no receipt shows.
        // MUTATION THIS CATCHES: dropping RunSpendRefusedDetailAsync. The operator is told the cap is reached and
        // given no way to find the attempt, the epoch, the amount, or when it clears.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: TerminalClaimUsd);
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: TerminalClaimUsd, model: "claude-opus-4-8", workflowRunId: workflowRunId);

        await MintLaunchClaimAsync(workflowRunId, teamId, runId, epoch: 7);

        await ExecuteAsync(runId, new UsageReportingHarness(PricedUsageScript));

        var result = await PersistedResultAsync(runId);
        result.ExitReason.ShouldBe(FailureCodes.RunBudgetExhausted);
        result.Error.ShouldNotBeNull().ShouldContain("epoch 7", Case.Insensitive, "the refusal must name WHICH attempt holds the cap");
        result.Error!.ShouldContain("unknown spend", Case.Insensitive, "a held cap is not a spent cap, and the operator must be able to tell them apart");

        var dead = (await ReservationOfAsync(workflowRunId, runId, scopeKey: AgentRunExecutor.RunSpendScopeKey(runId, 7, 0))).ShouldNotBeNull();
        dead.State.ShouldBe(BudgetReservationStates.Indeterminate, "the refusal still lands a terminal, and the terminal closes every claim the run holds");
        dead.SettledUsd.ShouldBeNull("closing the bookkeeping of an unobserved CLI never invents its bill, so the hold stays — which is why the refusal above had to name it");
    }

    [Fact]
    public async Task A_re_dispatch_relaunches_beside_the_hold_its_first_attempt_left()
    {
        if (OperatingSystem.IsWindows()) return;

        // The same shape with headroom left: the dead attempt holds $1 of a $5 cap, so the re-dispatch is admitted
        // for the $4 that remains and runs its CLI. This is the whole point of the attempt grain — the dead hold
        // REDUCES the retry's headroom (it is real money that may be gone) without cancelling the retry.
        // MUTATION THIS CATCHES: the pre-attempt key shape — the re-dispatch replays the dead row, is refused on
        // intent mismatch, and never reaches the sandbox.
        var teamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: TerminalClaimUsd);
        var runId = await CreateScriptedRunAsync(teamId, maxCostUsd: TerminalClaimUsd, model: "claude-opus-4-8", workflowRunId: workflowRunId);

        await MintLaunchClaimAsync(workflowRunId, teamId, runId, epoch: 7, reservedUsd: 1m);

        await ExecuteAsync(runId, new UsageReportingHarness(PricedUsageScript));

        (await PersistedResultAsync(runId)).ExitReason.ShouldNotBe(FailureCodes.RunBudgetExhausted, "check AdmitRunSpendAsync — $4 of the run's cap is free");
        (await PersistedRunAsync(runId)).Status.ShouldBe(AgentRunStatus.Succeeded, "a refused re-dispatch never starts its CLI at all");

        var live = (await ReservationOfAsync(workflowRunId, runId, scopeKey: AgentRunExecutor.RunSpendScopeKey(runId, 1, 0))).ShouldNotBeNull();
        live.ReservedUsd.ShouldBe(4m, "the new attempt claims what is left BESIDE the dead attempt's hold");
        live.SettledUsd.ShouldBe(PricedUsageCostUsd, "its own CLI really ran and reported its usage");

        (await ReservationOfAsync(workflowRunId, runId, scopeKey: AgentRunExecutor.RunSpendScopeKey(runId, 7, 0)))
            .ShouldNotBeNull().State.ShouldBe(BudgetReservationStates.Indeterminate, "the dead attempt's spend is still unknown — the terminal closes its bookkeeping, it never invents a bill");
    }

    [Fact]
    public async Task A_claim_of_the_same_agent_run_from_another_team_is_still_refused()
    {
        // A claim is authority for the team that made it and nobody else — unchanged, and pinned here at the agent
        // run's own key shape. MUTATION THIS CATCHES: dropping sameTeam from the ledger's replay branch.
        var teamId = await SeedTeamAsync();
        var otherTeamId = await SeedTeamAsync();
        var workflowRunId = await SeedCappedWorkflowRunAsync(teamId, capUsd: TerminalClaimUsd);
        var runId = Guid.NewGuid();
        var task = new AgentTask { Goal = "scripted", Harness = "scripted", Model = "claude-opus-4-8", TimeoutSeconds = 1800, MaxCostUsd = TerminalClaimUsd };

        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();

        (await AdmitLikeTheLaunchAsync(ledger, workflowRunId, teamId, runId, task, epoch: 1)).Admitted.ShouldBeTrue();

        var poacher = await AdmitLikeTheLaunchAsync(ledger, workflowRunId, otherTeamId, runId, task, epoch: 1);

        poacher.Admitted.ShouldBeFalse("an existing claim is not authority to bill a different team");
        poacher.ReservationId.ShouldBeNull();
        poacher.ReservationState.ShouldBeNull("another team's claim is not this team's to inspect either");
    }

    // ── fixtures ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The admission <c>AgentRunExecutor.AdmitRunSpendAsync</c> performs, composed from the same production pieces:
    /// the ledger's committed total, the executor's own estimate and scope key, the supervisor's own deadline, and
    /// the kind a capped launch mints. Only the wiring is the test's — every figure is production's.
    /// </summary>
    private static async Task<BudgetAdmission> AdmitLikeTheLaunchAsync(IBudgetLedger ledger, Guid workflowRunId, Guid teamId, Guid agentRunId, AgentTask task, long epoch)
    {
        var committed = await ledger.CommittedUsdAsync(workflowRunId, teamId, CancellationToken.None);
        var estimate = AgentRunExecutor.RunSpendEstimate(task, TerminalClaimUsd, committed).ShouldNotBeNull("the executor would have refused this launch before the ledger — the test's premise is gone");
        var priceVersion = AgentCostPricing.SnapshotFor(task.Model)?.Digest ?? ModelPriceSnapshot.UnpricedVersion;

        return await ledger.ReserveAsync(workflowRunId, teamId, BudgetKinds.AgentRunMonitored, AgentRunExecutor.RunSpendScopeKey(agentRunId, epoch, round: 0), estimate, TerminalClaimUsd, priceVersion, null, RealSupervisorActionExecutor.AttemptReservationDeadline(task), CancellationToken.None);
    }

    /// <summary>
    /// Exactly the row a capped launch mints for one attempt's first CLI invocation — same kind, the production
    /// scope key for <paramref name="epoch"/>, and the price stamp admission computes from the dispatched model. The
    /// worker that wrote it is the one that never comes back.
    /// </summary>
    private async Task MintLaunchClaimAsync(Guid workflowRunId, Guid teamId, Guid agentRunId, long epoch = 1, decimal? reservedUsd = null)
    {
        using var scope = _fixture.BeginScope();
        var priceVersion = AgentCostPricing.SnapshotFor("claude-opus-4-8")?.Digest ?? ModelPriceSnapshot.UnpricedVersion;
        var key = AgentRunExecutor.RunSpendScopeKey(agentRunId, epoch, round: 0);

        (await scope.Resolve<IBudgetLedger>().ReserveAsync(workflowRunId, teamId, BudgetKinds.AgentRunMonitored, key, reservedUsd ?? TerminalClaimUsd, TerminalClaimUsd, priceVersion, null, DateTimeOffset.UtcNow.AddHours(4), CancellationToken.None))
            .Admitted.ShouldBeTrue("precondition: the launch's own claim must be admitted before a terminal can close it");
    }

    /// <summary>
    /// Flip the run Running through the real service, then — for the priced arm — stamp the result a terminal fold
    /// would have left. No production writer stamps a priced result on a still-RUNNING run today (only
    /// <c>CompleteAsync</c> and the reconciler's own CAS write <c>result_jsonb</c>, and both land terminal), so the
    /// unpriced arm is what both sites see in production. The priced arm pins the other half of the rule this slice
    /// is about: the settle lands the run's OWN recorded figure rather than the estimate, whenever one is there.
    /// </summary>
    private async Task StartRunningAsync(Guid runId, bool withACost)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        if (!withACost) return;

        var resultJson = JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Running, ExitReason = "priced-mid-flight", CostUsd = TerminalObservedUsd, CumulativeCostUsd = TerminalObservedUsd }, AgentJson.Options);

        await scope.Resolve<CodeSpaceDbContext>().Database
            .ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET result_jsonb = CAST({resultJson} AS jsonb) WHERE id = {runId}");
    }

    /// <summary>Leave the run exactly as a vanished worker leaves it for the liveness sweep: Running, lease lapsed, no handle to probe and no recent events — the blind-abandon candidate.</summary>
    private async Task StrandWithoutAHeartbeatAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour', heartbeat_at = clock_timestamp() - interval '1 hour', started_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
    }

    /// <summary>The same vanished worker, but its detached process had ALREADY finished and written its exit marker — the spool-recovery arm rather than the abandon one. A real process, launched and waited out, so the recovery reads a genuine marker.</summary>
    private async Task StrandWithAFinishedSpoolAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var runner = (ISandboxDurableRunner)scope.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind);
        var handle = await runner.LaunchAsync(new SandboxSpec { Command = "/bin/sh", Args = ["-c", "printf 'done\\n'"], TimeoutSeconds = 60 }, runId.ToString("N"), CancellationToken.None);

        while ((await runner.ProbeAsync(handle, CancellationToken.None)).State == SandboxRunState.Running)
            await Task.Delay(50);

        await scope.Resolve<IAgentRunService>().SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);
        await StrandWithoutAHeartbeatAsync(runId);
    }
}
