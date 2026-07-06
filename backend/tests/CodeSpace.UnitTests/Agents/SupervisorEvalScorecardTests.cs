using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the pure supervisor scorer (PR-E E6): per-kind decision counts, replan rounds, spawned-agent totals,
/// the ground-truth spawn success rate (from REAL agent terminals, mixed success/fail), ask_human count,
/// time-to-stop, and each outcome mapping (completed / budget-exhausted / governance-denied / aborted). An
/// in-flight run is not-scored + excluded from the roll-up; an empty ledger is a zeroed run. This is the
/// measurement that makes "is the supervisor working" a number, so its edges are nailed down.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorEvalScorecardTests
{
    private static SupervisorDecisionSummary Decision(string kind, int staged = 0, string? stopReason = null, bool? acceptancePassed = null) =>
        new() { Kind = kind, StagedAgentCount = staged, StopReason = stopReason, AcceptancePassed = acceptancePassed };

    // terminalStatus null = in-flight (not scored); a value = the run's REAL terminal status. Defaults to Success
    // so a terminal run with a clean stop label keeps mapping to completed exactly as before.
    private static SupervisorRunOutcome Run(IReadOnlyList<SupervisorDecisionSummary> decisions, IReadOnlyList<AgentRunStatus>? spawnedStatuses = null, WorkflowRunStatus? terminalStatus = WorkflowRunStatus.Success, double? timeToStopSeconds = null, Guid? id = null) =>
        new()
        {
            SupervisorRunId = id ?? Guid.NewGuid(),
            Decisions = decisions,
            SpawnedAgentStatuses = spawnedStatuses ?? Array.Empty<AgentRunStatus>(),
            TerminalStatus = terminalStatus,
            TimeToStopSeconds = timeToStopSeconds,
        };

    // ─── Per-run scoring ────────────────────────────────────────────────────────────

    [Fact]
    public void Counts_each_decision_kind_and_replan_rounds()
    {
        var score = SupervisorEvalScorecard.Score(Run(new[]
        {
            Decision(SupervisorDecisionKinds.Plan),
            Decision(SupervisorDecisionKinds.Spawn, staged: 2),
            Decision(SupervisorDecisionKinds.Plan),           // a replan
            Decision(SupervisorDecisionKinds.Retry, staged: 1),
            Decision(SupervisorDecisionKinds.AskHuman),
            Decision(SupervisorDecisionKinds.Merge),
            Decision(SupervisorDecisionKinds.Plan),           // another replan
            Decision(SupervisorDecisionKinds.Stop, stopReason: "completed"),
        }));

        score.TotalDecisions.ShouldBe(8);
        score.PlanCount.ShouldBe(3);
        score.SpawnCount.ShouldBe(1);
        score.RetryCount.ShouldBe(1);
        score.AskHumanCount.ShouldBe(1);
        score.MergeCount.ShouldBe(1);
        score.StopCount.ShouldBe(1);
        score.ReplanRounds.ShouldBe(2, "plan decisions AFTER the first are the rework cycles");
    }

    [Fact]
    public void Spawned_agents_sums_staged_counts_across_spawn_and_retry()
    {
        var score = SupervisorEvalScorecard.Score(Run(new[]
        {
            Decision(SupervisorDecisionKinds.Spawn, staged: 3),
            Decision(SupervisorDecisionKinds.Retry, staged: 1),
            Decision(SupervisorDecisionKinds.Stop, stopReason: "completed"),
        }));

        score.SpawnedAgents.ShouldBe(4, "spawn(3) + retry(1)");
    }

    [Fact]
    public void Spawn_success_rate_is_from_real_agent_terminals_mixed()
    {
        // 4 spawned, only 2 truly Succeeded (one Failed, one still Running) → 0.5 — the honest rate.
        var score = SupervisorEvalScorecard.Score(Run(
            new[]
            {
                Decision(SupervisorDecisionKinds.Spawn, staged: 4),
                Decision(SupervisorDecisionKinds.Stop, stopReason: "completed"),
            },
            spawnedStatuses: new[] { AgentRunStatus.Succeeded, AgentRunStatus.Succeeded, AgentRunStatus.Failed, AgentRunStatus.Running }));

        score.SpawnedAgents.ShouldBe(4);
        score.SpawnSuccessRate.ShouldBe(0.5, "2 of 4 spawned agents actually Succeeded — not the decider's word");
    }

    [Fact]
    public void Spawn_success_rate_is_zero_when_nothing_was_spawned()
    {
        var score = SupervisorEvalScorecard.Score(Run(new[]
        {
            Decision(SupervisorDecisionKinds.Plan),
            Decision(SupervisorDecisionKinds.Stop, stopReason: "completed"),
        }));

        score.SpawnedAgents.ShouldBe(0);
        score.SpawnSuccessRate.ShouldBe(0);
    }

    [Fact]
    public void Time_to_stop_is_carried_for_a_terminal_run_and_null_for_in_flight()
    {
        SupervisorEvalScorecard.Score(Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: "completed") }, timeToStopSeconds: 42.0))
            .TimeToStopSeconds.ShouldBe(42.0);

        SupervisorEvalScorecard.Score(Run(new[] { Decision(SupervisorDecisionKinds.Plan) }, terminalStatus: null, timeToStopSeconds: 42.0))
            .TimeToStopSeconds.ShouldBeNull("an in-flight run has no time-to-stop even if a stray value is passed");
    }

    // ─── Outcome mapping (each terminal stop reason/label) ────────────────────────────

    [Theory]
    [InlineData("completed", SupervisorOutcomes.Completed)]
    [InlineData("success", SupervisorOutcomes.Completed)]
    [InlineData("Done", SupervisorOutcomes.Completed)]               // case-insensitive
    [InlineData("failed", SupervisorOutcomes.Aborted)]
    [InlineData("abandoned", SupervisorOutcomes.Aborted)]
    public void Decider_stop_label_maps_to_completed_or_aborted(string label, string expected)
    {
        SupervisorEvalScorecard.Score(Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: label) }))
            .Outcome.ShouldBe(expected);
    }

    [Theory]
    [InlineData(SupervisorStopReasons.TotalSpawnCapReached, SupervisorOutcomes.BudgetExhausted)]
    [InlineData(SupervisorStopReasons.SpawnFanOutExceedsCap, SupervisorOutcomes.BudgetExhausted)]
    [InlineData(SupervisorStopReasons.DepthCapExceeded, SupervisorOutcomes.BudgetExhausted)]
    [InlineData(SupervisorStopReasons.NoProgress, SupervisorOutcomes.BudgetExhausted)]
    [InlineData(SupervisorStopReasons.GovernanceDenied, SupervisorOutcomes.GovernanceDenied)]
    public void Forced_stop_reason_maps_to_the_bound_bucket(string reason, string expected)
    {
        SupervisorEvalScorecard.Score(Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: reason) }))
            .Outcome.ShouldBe(expected);
    }

    // ─── Objective acceptance verdict OVERRIDES the self-reported label (the C3 self-grade seam) ───────

    [Fact]
    public void A_model_stop_labelled_completed_whose_acceptance_check_FAILED_is_scored_acceptance_failed_not_completed()
    {
        // The self-grade hole: the brain authored a stop it labelled "completed", but the SERVER graded its own
        // definition-of-done as FAILED (the reviewable branch was withheld). The eval must NOT take the brain's word.
        SupervisorEvalScorecard.Score(Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: "completed", acceptancePassed: false) }))
            .Outcome.ShouldBe(SupervisorOutcomes.AcceptanceFailed);
    }

    [Theory]
    [InlineData(true)]   // acceptance passed → the success label stands
    [InlineData(null)]   // no acceptance check authored → label-based classification, byte-identical to a pre-acceptance run
    public void A_model_stop_labelled_completed_stays_completed_when_acceptance_passed_or_was_not_checked(bool? acceptancePassed)
    {
        SupervisorEvalScorecard.Score(Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: "completed", acceptancePassed: acceptancePassed) }))
            .Outcome.ShouldBe(SupervisorOutcomes.Completed);
    }

    [Fact]
    public void A_failed_acceptance_grade_overrides_even_a_non_success_label_with_the_specific_bucket()
    {
        // A stop the model labelled "failed" AND that failed acceptance is the more-specific acceptance-failed, not the
        // generic aborted — the objective grade is the precise signal.
        SupervisorEvalScorecard.Score(Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: "failed", acceptancePassed: false) }))
            .Outcome.ShouldBe(SupervisorOutcomes.AcceptanceFailed);
    }

    [Fact]
    public void A_forced_bound_stop_keeps_its_bucket_even_if_an_acceptance_grade_is_somehow_present()
    {
        // Precedence guard: a fail-closed bound / governance force-stop is classified by its reason BEFORE the
        // acceptance check — a forced stop is the engine's verdict, never the model's definition-of-done.
        SupervisorEvalScorecard.Score(Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: SupervisorStopReasons.GovernanceDenied, acceptancePassed: false) }))
            .Outcome.ShouldBe(SupervisorOutcomes.GovernanceDenied);

        SupervisorEvalScorecard.Score(Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: SupervisorStopReasons.NoProgress, acceptancePassed: false) }))
            .Outcome.ShouldBe(SupervisorOutcomes.BudgetExhausted);
    }

    [Fact]
    public void Acceptance_failed_runs_tally_in_the_outcome_distribution()
    {
        var card = SupervisorEvalScorecard.Compute(new[]
        {
            Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: "completed", acceptancePassed: true) }),
            Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: "completed", acceptancePassed: false) }),
            Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: "done", acceptancePassed: false) }),
        });

        card.Rollup.OutcomeDistribution[SupervisorOutcomes.Completed].ShouldBe(1);
        card.Rollup.OutcomeDistribution[SupervisorOutcomes.AcceptanceFailed].ShouldBe(2, "a brain that self-reports done but fails its own DoD is counted honestly");
    }

    [Fact]
    public void A_terminal_run_with_no_stop_reason_is_completed_as_the_neutral_default()
    {
        SupervisorEvalScorecard.Score(Run(new[] { Decision(SupervisorDecisionKinds.Stop) /* no reason */ }))
            .Outcome.ShouldBe(SupervisorOutcomes.Completed);
    }

    // A terminal run that recorded NO supervisor stop decision (the supervisor node failed mid-run, or an
    // operator cancelled it) must be bucketed by its REAL run status — never silently folded into completed.
    [Theory]
    [InlineData(WorkflowRunStatus.Success, SupervisorOutcomes.Completed)]    // a clean terminal run with no stop → reached its end
    [InlineData(WorkflowRunStatus.Failure, SupervisorOutcomes.Aborted)]      // the supervisor node failed (lane off mid-run, unreadable scope) → aborted, NOT completed
    [InlineData(WorkflowRunStatus.Cancelled, SupervisorOutcomes.Cancelled)]  // an operator cancelled → cancelled, NOT completed
    public void A_terminal_run_with_no_stop_decision_is_classified_by_its_real_run_status(WorkflowRunStatus terminalStatus, string expected)
    {
        // A plan-only ledger: no stop decision was ever recorded, yet the WorkflowRun reached a terminal status.
        SupervisorEvalScorecard.Score(Run(new[] { Decision(SupervisorDecisionKinds.Plan) }, terminalStatus: terminalStatus))
            .Outcome.ShouldBe(expected);
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Failure)]
    [InlineData(WorkflowRunStatus.Cancelled)]
    public void A_non_successful_terminal_run_with_no_stop_decision_is_never_bucketed_as_completed(WorkflowRunStatus terminalStatus)
    {
        // The honesty invariant: a Failure/Cancelled terminal run that never recorded a stop is scored (counts
        // toward the roll-up) but does NOT inflate the completed bucket.
        var score = SupervisorEvalScorecard.Score(Run(new[] { Decision(SupervisorDecisionKinds.Plan) }, terminalStatus: terminalStatus));

        score.NotScored.ShouldBeFalse("the run reached a terminal status — it is scored");
        score.Outcome.ShouldNotBe(SupervisorOutcomes.Completed, "a non-successful terminal run must not masquerade as completed");
    }

    [Fact]
    public void An_in_flight_run_is_not_scored()
    {
        var score = SupervisorEvalScorecard.Score(Run(
            new[] { Decision(SupervisorDecisionKinds.Plan), Decision(SupervisorDecisionKinds.Spawn, staged: 2) },
            terminalStatus: null));

        score.NotScored.ShouldBeTrue();
        score.Outcome.ShouldBe(SupervisorOutcomes.NotScored);
        score.SpawnedAgents.ShouldBe(2, "the per-kind facts are still computed — only the outcome is withheld");
    }

    [Fact]
    public void The_terminal_gate_dominates_the_acceptance_override_for_an_in_flight_run()
    {
        // Pins that the in-flight gate (Outcome = NotScored when not terminal) runs BEFORE the acceptance override —
        // an in-flight run carrying a failed acceptance grade is still not-scored, never acceptance-failed.
        SupervisorEvalScorecard.Score(Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: "completed", acceptancePassed: false) }, terminalStatus: null))
            .Outcome.ShouldBe(SupervisorOutcomes.NotScored);
    }

    [Fact]
    public void An_empty_ledger_is_a_zeroed_run()
    {
        var score = SupervisorEvalScorecard.Score(Run(Array.Empty<SupervisorDecisionSummary>()));

        score.TotalDecisions.ShouldBe(0);
        score.PlanCount.ShouldBe(0);
        score.ReplanRounds.ShouldBe(0);
        score.SpawnedAgents.ShouldBe(0);
        score.SpawnSuccessRate.ShouldBe(0);
        score.Outcome.ShouldBe(SupervisorOutcomes.Completed, "a terminal run that recorded nothing reached its end");
    }

    // ─── Roll-up ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_input_is_a_zeroed_rollup_with_no_runs()
    {
        var card = SupervisorEvalScorecard.Compute(Array.Empty<SupervisorRunOutcome>());

        card.Runs.ShouldBeEmpty();
        card.Rollup.ScoredRuns.ShouldBe(0);
        card.Rollup.NotScoredRuns.ShouldBe(0);
        card.Rollup.OverallSpawnSuccessRate.ShouldBe(0);
        card.Rollup.OutcomeDistribution.ShouldBeEmpty();
        card.Rollup.P50TimeToStopSeconds.ShouldBeNull();
    }

    [Fact]
    public void Rollup_averages_exclude_in_flight_runs_and_count_them_separately()
    {
        var card = SupervisorEvalScorecard.Compute(new[]
        {
            Run(new[] { Decision(SupervisorDecisionKinds.Plan), Decision(SupervisorDecisionKinds.Plan), Decision(SupervisorDecisionKinds.Stop, stopReason: "completed") }, timeToStopSeconds: 10),
            Run(new[] { Decision(SupervisorDecisionKinds.Plan), Decision(SupervisorDecisionKinds.Stop, stopReason: "completed") }, timeToStopSeconds: 20),
            Run(new[] { Decision(SupervisorDecisionKinds.Plan) }, terminalStatus: null),   // in-flight → excluded from averages
        });

        card.Rollup.ScoredRuns.ShouldBe(2);
        card.Rollup.NotScoredRuns.ShouldBe(1);
        card.Rollup.AvgDecisionsPerRun.ShouldBe(2.5, "scored runs have 3 + 2 decisions → mean 2.5 (the in-flight run is excluded)");
        card.Rollup.AvgReplanRounds.ShouldBe(0.5, "one replan + zero replans over the two scored runs");
    }

    [Fact]
    public void Overall_spawn_success_is_exact_from_real_terminals_across_runs()
    {
        var card = SupervisorEvalScorecard.Compute(new[]
        {
            // 3 spawned, 2 succeeded
            Run(new[] { Decision(SupervisorDecisionKinds.Spawn, staged: 3), Decision(SupervisorDecisionKinds.Stop, stopReason: "completed") },
                spawnedStatuses: new[] { AgentRunStatus.Succeeded, AgentRunStatus.Succeeded, AgentRunStatus.Failed }),
            // 1 spawned, 1 succeeded
            Run(new[] { Decision(SupervisorDecisionKinds.Spawn, staged: 1), Decision(SupervisorDecisionKinds.Stop, stopReason: "completed") },
                spawnedStatuses: new[] { AgentRunStatus.Succeeded }),
        });

        // 3 of 4 spawned agents succeeded across the two runs — the honest cross-run rate.
        card.Rollup.OverallSpawnSuccessRate.ShouldBe(0.75);
    }

    [Fact]
    public void Outcome_distribution_tallies_the_scored_runs()
    {
        var card = SupervisorEvalScorecard.Compute(new[]
        {
            Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: "completed") }),
            Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: "completed") }),
            Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: SupervisorStopReasons.NoProgress) }),
            Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: SupervisorStopReasons.GovernanceDenied) }),
            Run(new[] { Decision(SupervisorDecisionKinds.Plan) }, terminalStatus: null),   // not-scored → absent from the distribution
        });

        card.Rollup.OutcomeDistribution[SupervisorOutcomes.Completed].ShouldBe(2);
        card.Rollup.OutcomeDistribution[SupervisorOutcomes.BudgetExhausted].ShouldBe(1);
        card.Rollup.OutcomeDistribution[SupervisorOutcomes.GovernanceDenied].ShouldBe(1);
        card.Rollup.OutcomeDistribution.ContainsKey(SupervisorOutcomes.NotScored).ShouldBeFalse("not-scored runs are reported but not bucketed");
    }

    [Fact]
    public void Time_to_stop_percentiles_use_nearest_rank_over_scored_runs()
    {
        var card = SupervisorEvalScorecard.Compute(Enumerable.Range(1, 10)
            .Select(i => Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: "completed") }, timeToStopSeconds: i * 10.0))
            .ToArray());

        // times 10..100; nearest-rank p50 → 50, p95 → 100 (mirrors EvalScorecard's percentile).
        card.Rollup.P50TimeToStopSeconds.ShouldBe(50);
        card.Rollup.P95TimeToStopSeconds.ShouldBe(100);
    }

    [Fact]
    public void Is_deterministic()
    {
        var runs = new[]
        {
            Run(new[] { Decision(SupervisorDecisionKinds.Spawn, staged: 2), Decision(SupervisorDecisionKinds.Stop, stopReason: "completed") },
                spawnedStatuses: new[] { AgentRunStatus.Succeeded, AgentRunStatus.Failed }, timeToStopSeconds: 7, id: Guid.Parse("11111111-1111-1111-1111-111111111111")),
            Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: SupervisorStopReasons.NoProgress) },
                timeToStopSeconds: 3, id: Guid.Parse("22222222-2222-2222-2222-222222222222")),
            Run(new[] { Decision(SupervisorDecisionKinds.Stop, stopReason: "completed", acceptancePassed: false) },
                timeToStopSeconds: 5, id: Guid.Parse("33333333-3333-3333-3333-333333333333")),   // the acceptance-failed bucket participates in the snapshot
        };

        System.Text.Json.JsonSerializer.Serialize(SupervisorEvalScorecard.Compute(runs))
            .ShouldBe(System.Text.Json.JsonSerializer.Serialize(SupervisorEvalScorecard.Compute(runs)));
    }
}
