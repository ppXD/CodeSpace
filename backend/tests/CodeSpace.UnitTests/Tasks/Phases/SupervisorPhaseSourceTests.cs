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

    private static SupervisorDecisionRecord Decision(long sequence, string kind, SupervisorDecisionStatus status, string? outcomeJson = null) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = Guid.NewGuid(),
        SupervisorRunId = Guid.NewGuid(),
        Sequence = sequence,
        DecisionKind = kind,
        IdempotencyKey = $"{kind}:{sequence}",
        InputHash = "hash",
        Status = status,
        PayloadJson = "{}",
        OutcomeJson = outcomeJson,
        CreatedDate = DateTimeOffset.UtcNow,
        LastModifiedDate = DateTimeOffset.UtcNow,
    };

    private static readonly IReadOnlyDictionary<Guid, AgentRunStatus> EmptyStatuses = new Dictionary<Guid, AgentRunStatus>();
}
