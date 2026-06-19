using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Phases;

/// <summary>
/// The supervisor-ledger source's pure projection (decisions + the already-resolved ground-truth agent statuses →
/// phases). The DB read is integration-tested; here we pin the per-decision shape: a spawn decision with two staged
/// agent ids becomes a 'Spawn 2 agents' phase whose child refs reflect the REAL AgentRun statuses (not the decider's
/// word), an ask_human becomes a Waiting phase carrying the question, and the ledger phases sort after the structural
/// node phases.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorPhaseSourceTests
{
    [Fact]
    public void A_spawn_decision_yields_a_spawn_phase_with_ground_truth_child_agents()
    {
        var succeeded = Guid.NewGuid();
        var failed = Guid.NewGuid();

        var spawn = Decision(sequence: 2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $"{{\"agentCount\":2,\"agentRunIds\":[\"{succeeded}\",\"{failed}\"]}}");

        // The ground-truth fold: one Succeeded, one Failed — the source must reflect the REAL statuses, never the decider self-report.
        var statuses = new Dictionary<Guid, AgentRunStatus>
        {
            [succeeded] = AgentRunStatus.Succeeded,
            [failed] = AgentRunStatus.Failed,
        };

        var phase = SupervisorPhaseSource.ProjectDecisions(new[] { spawn }, statuses).ShouldHaveSingleItem();

        phase.Kind.ShouldBe(SupervisorDecisionKinds.Spawn);
        phase.Label.ShouldBe("Spawn 2 agents");
        phase.Status.ShouldBe(PhaseStatus.Succeeded);
        phase.SourceKey.ShouldBe(SupervisorPhaseSource.Key);
        phase.Order.ShouldBe(SupervisorPhaseSource.OrderBase + 2);

        phase.Agents.Select(a => a.AgentRunId).ShouldBe(new[] { succeeded, failed });
        phase.Agents.Single(a => a.AgentRunId == succeeded).Status.ShouldBe(nameof(AgentRunStatus.Succeeded));
        phase.Agents.Single(a => a.AgentRunId == failed).Status.ShouldBe(nameof(AgentRunStatus.Failed));

        phase.Metrics.AgentCount.ShouldBe(2);
        phase.Metrics.SucceededCount.ShouldBe(1);
        phase.Metrics.FailedCount.ShouldBe(1);
    }

    [Fact]
    public void An_ask_human_decision_is_waiting_and_carries_the_question()
    {
        var ask = Decision(sequence: 1, SupervisorDecisionKinds.AskHuman, SupervisorDecisionStatus.AwaitingApproval,
            outcomeJson: SupervisorOutcome.FoldAnswer("Which database?", token: "tok", answer: null));

        var phase = SupervisorPhaseSource.ProjectDecisions(new[] { ask }, EmptyStatuses).ShouldHaveSingleItem();

        phase.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman);
        phase.Label.ShouldBe("Ask human");
        phase.Status.ShouldBe(PhaseStatus.Waiting);
        phase.Summary.ShouldBe("Which database?");
        phase.Agents.ShouldBeEmpty();
    }

    [Fact]
    public void An_answered_ask_human_folds_the_answer_into_the_summary()
    {
        var ask = Decision(sequence: 1, SupervisorDecisionKinds.AskHuman, SupervisorDecisionStatus.Succeeded,
            outcomeJson: SupervisorOutcome.FoldAnswer("Which database?", token: "tok", answer: "Postgres"));

        var phase = SupervisorPhaseSource.ProjectDecisions(new[] { ask }, EmptyStatuses).ShouldHaveSingleItem();

        phase.Summary.ShouldBe("Which database? — Postgres");
        phase.Status.ShouldBe(PhaseStatus.Succeeded);
    }

    [Fact]
    public void Plan_then_stop_yields_two_ordered_phases_with_no_children()
    {
        var plan = Decision(sequence: 1, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded);
        var stop = Decision(sequence: 3, SupervisorDecisionKinds.Stop, SupervisorDecisionStatus.Succeeded);

        var phases = SupervisorPhaseSource.ProjectDecisions(new[] { plan, stop }, EmptyStatuses);

        phases.Select(p => p.Label).ShouldBe(new[] { "Plan", "Stop" });
        phases.Select(p => p.Order).ShouldBe(new[] { SupervisorPhaseSource.OrderBase + 1, SupervisorPhaseSource.OrderBase + 3 });
        phases.ShouldAllBe(p => p.Agents.Count == 0);
    }

    [Fact]
    public void A_pending_spawn_with_no_outcome_yet_has_no_children()
    {
        var spawn = Decision(sequence: 1, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Pending, outcomeJson: null);

        var phase = SupervisorPhaseSource.ProjectDecisions(new[] { spawn }, EmptyStatuses).ShouldHaveSingleItem();

        phase.Agents.ShouldBeEmpty("a Pending spawn has not staged its agents yet — tolerate the null outcome");
        phase.Label.ShouldBe("Spawn", "no count yet → the bare verb label");
        phase.Status.ShouldBe(PhaseStatus.Pending);
    }

    // ── L4 arc C: model-authored semantic phases project as their own band ─────────────

    [Fact]
    public void A_flat_plan_adds_no_authored_phases()
    {
        var plan = Decision(1, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, outcomeJson: """{"planned":[],"count":0}""");

        SupervisorPhaseSource.ProjectDecisions(new[] { plan }, EmptyStatuses).Count
            .ShouldBe(1, "a flat plan (no phases) contributes only the per-decision phase — the board is verbatim");
    }

    [Fact]
    public void A_phased_plan_projects_its_phases_grouping_the_agents_that_ran_their_subtasks()
    {
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();

        // plan grouped sa → Implement, sb → Verify (Verify carries an acceptance command).
        var plan = Decision(1, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, outcomeJson:
            """{"planned":[],"count":2,"phases":[{"id":"impl","title":"Implement","subtaskIds":["sa"]},{"id":"verify","title":"Verify","subtaskIds":["sb"],"acceptance":{"command":["sh","check.sh"]}}]}""");
        // the spawn fanned out [sa, sb] → [agentA, agentB] (subtaskIds[i] ↔ agentRunIds[i]).
        var spawn = Decision(2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $$"""{"agentCount":2,"agentRunIds":["{{agentA}}","{{agentB}}"]}""", payloadJson: """{"subtaskIds":["sa","sb"]}""");

        var statuses = new Dictionary<Guid, AgentRunStatus> { [agentA] = AgentRunStatus.Succeeded, [agentB] = AgentRunStatus.Succeeded };

        var all = SupervisorPhaseSource.ProjectDecisions(new[] { plan, spawn }, statuses);

        // the per-decision phases (Plan, Spawn) PLUS the two authored phases, in their own band after the tape.
        var authored = all.Where(p => p.Kind == "phase").ToList();
        authored.Select(p => p.Label).ShouldBe(new[] { "Implement", "Verify" });
        authored.Select(p => p.Order).ShouldBe(new[] { SupervisorPhaseSource.PhaseOrderBase + 0, SupervisorPhaseSource.PhaseOrderBase + 1 });

        var implement = authored.Single(p => p.Label == "Implement");
        implement.Agents.Single().AgentRunId.ShouldBe(agentA, "the Implement phase groups the agent that ran its subtask sa");
        implement.Status.ShouldBe(PhaseStatus.Succeeded, "all of the phase's agents succeeded → the phase succeeded");

        var verify = authored.Single(p => p.Label == "Verify");
        verify.Agents.Single().AgentRunId.ShouldBe(agentB);
        verify.Summary.ShouldBe("sh check.sh", "the phase's acceptance command surfaces for the board");
    }

    [Fact]
    public void A_phase_status_folds_to_failed_when_one_of_its_agents_failed()
    {
        var agentA = Guid.NewGuid();

        var plan = Decision(1, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, outcomeJson:
            """{"planned":[],"count":1,"phases":[{"id":"impl","title":"Implement","subtaskIds":["sa"]}]}""");
        var spawn = Decision(2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $$"""{"agentCount":1,"agentRunIds":["{{agentA}}"]}""", payloadJson: """{"subtaskIds":["sa"]}""");

        var statuses = new Dictionary<Guid, AgentRunStatus> { [agentA] = AgentRunStatus.Failed };

        SupervisorPhaseSource.ProjectDecisions(new[] { plan, spawn }, statuses).Single(p => p.Kind == "phase")
            .Status.ShouldBe(PhaseStatus.Failed, "a phase with a failed agent folds to Failed");
    }

    [Fact]
    public void A_phase_with_no_staged_agents_yet_is_pending()
    {
        var plan = Decision(1, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, outcomeJson:
            """{"planned":[],"count":1,"phases":[{"id":"impl","title":"Implement","subtaskIds":["sa"]}]}""");

        // No spawn yet → no subtask→agent mapping → the phase has no children → Pending.
        var phase = SupervisorPhaseSource.ProjectDecisions(new[] { plan }, EmptyStatuses).Single(p => p.Kind == "phase");

        phase.Agents.ShouldBeEmpty();
        phase.Status.ShouldBe(PhaseStatus.Pending);
    }

    [Fact]
    public void A_re_planned_run_projects_the_latest_plans_phases()
    {
        var agentA = Guid.NewGuid();

        // plan-1 grouped "old" → an early phase; plan-2 RE-planned grouping "sa" → Build; the spawn ran plan-2's subtask.
        var plan1 = Decision(1, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, outcomeJson:
            """{"planned":[],"count":1,"phases":[{"id":"stale","title":"Investigate","subtaskIds":["old"]}]}""");
        var plan2 = Decision(2, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, outcomeJson:
            """{"planned":[],"count":1,"phases":[{"id":"build","title":"Build","subtaskIds":["sa"]}]}""");
        var spawn = Decision(3, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $$"""{"agentCount":1,"agentRunIds":["{{agentA}}"]}""", payloadJson: """{"subtaskIds":["sa"]}""");

        var statuses = new Dictionary<Guid, AgentRunStatus> { [agentA] = AgentRunStatus.Succeeded };

        var authored = SupervisorPhaseSource.ProjectDecisions(new[] { plan1, plan2, spawn }, statuses).Where(p => p.Kind == "phase").ToList();

        authored.Select(p => p.Label).ShouldBe(new[] { "Build" }, "the phases track the LATEST plan — the one the spawns were built from, not the stale first plan");
        authored.Single().Agents.Single().AgentRunId.ShouldBe(agentA);
    }

    [Fact]
    public void A_retried_subtask_phase_shows_the_latest_attempt_not_the_failed_original()
    {
        var failedAgent = Guid.NewGuid();
        var retryAgent = Guid.NewGuid();

        var plan = Decision(1, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, outcomeJson:
            """{"planned":[],"count":1,"phases":[{"id":"impl","title":"Implement","subtaskIds":["sa"]}]}""");
        var spawn = Decision(2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $$"""{"agentCount":1,"agentRunIds":["{{failedAgent}}"]}""", payloadJson: """{"subtaskIds":["sa"]}""");
        // the supervisor RETRIED sa → a fresh agent that succeeded.
        var retry = Decision(3, SupervisorDecisionKinds.Retry, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $$"""{"agentCount":1,"agentRunIds":["{{retryAgent}}"]}""", payloadJson: """{"subtaskId":"sa"}""");

        var statuses = new Dictionary<Guid, AgentRunStatus> { [failedAgent] = AgentRunStatus.Failed, [retryAgent] = AgentRunStatus.Succeeded };

        var implement = SupervisorPhaseSource.ProjectDecisions(new[] { plan, spawn, retry }, statuses).Single(p => p.Kind == "phase");

        implement.Agents.Single().AgentRunId.ShouldBe(retryAgent, "the retried subtask shows its FRESH agent (latest attempt wins), not the original failed one");
        implement.Status.ShouldBe(PhaseStatus.Succeeded, "the subtask ultimately succeeded → the phase succeeded (ground-truth, not the stale failure)");
    }

    private static SupervisorDecisionRecord Decision(long sequence, string kind, SupervisorDecisionStatus status, string? outcomeJson = null, string payloadJson = "{}") => new()
    {
        Id = Guid.NewGuid(),
        TeamId = Guid.NewGuid(),
        SupervisorRunId = Guid.NewGuid(),
        Sequence = sequence,
        DecisionKind = kind,
        IdempotencyKey = $"{kind}:{sequence}",
        InputHash = "hash",
        Status = status,
        PayloadJson = payloadJson,
        OutcomeJson = outcomeJson,
        CreatedDate = DateTimeOffset.UtcNow,
        LastModifiedDate = DateTimeOffset.UtcNow,
    };

    private static readonly IReadOnlyDictionary<Guid, AgentRunStatus> EmptyStatuses = new Dictionary<Guid, AgentRunStatus>();
}
