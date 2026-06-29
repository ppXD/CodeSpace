using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Nodes;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// The PURE narrative engine — the place the backend OWNS the room's copy + order. Proves the two hardest things the
/// frontend used to get wrong: (1) the band SEPARATION — the model-authored semantic phases become the MAP (the plan's
/// shape) while the decision tape becomes the NARRATIVE (the play-by-play), so "Spawn" never renders before
/// "Investigate"; (2) the failure HUMANIZATION — a readable cause, never the raw engine "Node 'x' failed". Plus the
/// per-verb narration, the agent-card mapping, the tones, and the map-status vocabulary.
/// </summary>
[Trait("Category", "Unit")]
public class RoomNarrativeTests
{
    [Fact]
    public void Authored_phases_are_the_map_and_the_decision_tape_is_the_narrative()
    {
        var n = Build(new[]
        {
            Tape("plan", 1),
            Tape("spawn", 2, agentCount: 3, label: "Spawn 3 agents"),
            Authored("Investigate", 0),
            Authored("Implement", 1),
            Tape("stop", 3, summary: "Shipped the login flow."),
        });

        n.Map.ShouldNotBeNull();
        n.Map!.Steps.Select(s => s.Label).ShouldBe(new[] { "Investigate", "Implement" }, "the map is the plan's semantic phases — not the decision tape");

        var lines = n.Blocks.OfType<NarrativeStepBlock>().Select(b => b.Text).ToList();
        lines.ShouldContain("Planned the approach.");
        lines.ShouldContain("Dispatched 3 agents to work in parallel.");
        lines.ShouldNotContain("Shipped the login flow.", "the stop summary is the turn headline, not a duplicated line");

        n.Summary.ShouldBe("Shipped the login flow.");

        // The within-band ORDER is load-bearing — the plan line precedes the spawn line, and a phase's line precedes its
        // own agent group. (The map band separation is asserted above; this pins the play-by-play sequence.)
        n.Blocks.Select(Describe).ShouldBe(new[]
        {
            "line:Planned the approach.",
            "line:Dispatched 3 agents to work in parallel.",
            "group:3 agents",
        });
    }

    [Fact]
    public void An_agent_card_falls_back_through_label_then_subtask_then_role_then_default()
    {
        var phase = SpawnWith(
            new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Running), Label = null, AssignedSubtask = "Wire the API", Role = "backend" },
            new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Running), Label = null, AssignedSubtask = null, Role = "frontend" },
            new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Running), Label = null, AssignedSubtask = null, Role = null });

        var cards = Build(new[] { phase }, WorkflowRunStatus.Running).Blocks.OfType<AgentGroupBlock>().Single().Agents;

        cards[0].Label.ShouldBe("Wire the API", "no label → the assigned subtask");
        cards[1].Label.ShouldBe("frontend", "no label / subtask → the role");
        cards[2].Label.ShouldBe("Agent", "no label / subtask / role → the default");
    }

    [Fact]
    public void Card_tokens_are_null_when_the_agent_reports_no_usage_distinct_from_a_real_zero()
    {
        var unknown = SpawnWith(new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Running), InputTokens = null, OutputTokens = null });
        Build(new[] { unknown }, WorkflowRunStatus.Running).Blocks.OfType<AgentGroupBlock>().Single().Agents.Single()
            .Tokens.ShouldBeNull("unknown usage is null, not 0");

        var zero = SpawnWith(new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Succeeded), InputTokens = 0, OutputTokens = 0 });
        Build(new[] { zero }, WorkflowRunStatus.Running).Blocks.OfType<AgentGroupBlock>().Single().Agents.Single()
            .Tokens.ShouldBe(0, "a real zero is preserved");
    }

    [Fact]
    public void A_spawn_emits_an_agent_group_with_mapped_cards()
    {
        var n = Build(new[] { Tape("spawn", 1, agentCount: 2, label: "Spawn 2 agents") });

        var group = n.Blocks.OfType<AgentGroupBlock>().ShouldHaveSingleItem();
        group.Title.ShouldBe("2 agents");
        group.Agents.Count.ShouldBe(2);

        var card = group.Agents[0];
        card.Tokens.ShouldBe(150, "input + output tokens are summed");
        card.Model.ShouldBe("opus");
        card.FilesChanged.ShouldBe(2);
        card.Status.ShouldBe(nameof(AgentRunStatus.Succeeded));
    }

    [Fact]
    public void A_flat_plan_with_no_authored_phases_uses_the_decision_tape_as_the_map()
    {
        var n = Build(new[]
        {
            Tape("plan", 1, label: "Plan"),
            Tape("spawn", 2, agentCount: 2, label: "Spawn 2 agents"),
            Tape("stop", 3, summary: "Done deal.", label: "Stop"),
        });

        n.Map!.Steps.Select(s => s.Label).ShouldBe(new[] { "Plan", "Spawn 2 agents", "Stop" });
    }

    [Fact]
    public void A_single_agent_run_uses_the_structural_node_as_the_map_and_narrates_its_label()
    {
        var n = Build(new[] { Structural("agent", "Run the agent", order: 1, agentCount: 1) });

        n.Map!.Steps.ShouldHaveSingleItem().Label.ShouldBe("Run the agent");
        n.Blocks.OfType<NarrativeStepBlock>().ShouldHaveSingleItem().Text.ShouldBe("Run the agent");
        n.Blocks.OfType<AgentGroupBlock>().ShouldHaveSingleItem().Title.ShouldBe("Agent");
    }

    [Fact]
    public void A_failure_humanizes_the_engine_error_and_emits_a_diagnostic()
    {
        var n = Build(Array.Empty<RunPhase>(), WorkflowRunStatus.Failure, error: "Node 'sup' failed: the agent could not compile the project");

        var diag = n.Blocks.OfType<DiagnosticBlock>().ShouldHaveSingleItem();
        diag.Tone.ShouldBe(NarrativeTone.Error);
        diag.Text.ShouldBe("the agent could not compile the project", "the 'Node x failed:' engine prefix is stripped to the real cause");
        n.Summary.ShouldBe("the agent could not compile the project");
    }

    [Fact]
    public void A_bare_engine_failure_with_no_detail_falls_back_to_a_plain_sentence()
    {
        var n = Build(Array.Empty<RunPhase>(), WorkflowRunStatus.Failure, error: "Node 'sup' failed");

        n.Blocks.OfType<DiagnosticBlock>().ShouldHaveSingleItem().Text.ShouldBe("This turn ended with an error.", "never surface bare canvas jargon");
    }

    [Fact]
    public void A_cancelled_turn_reads_as_cancelled()
    {
        var n = Build(Array.Empty<RunPhase>(), WorkflowRunStatus.Cancelled);

        n.Blocks.OfType<DiagnosticBlock>().ShouldHaveSingleItem().Text.ShouldBe("This turn was cancelled.");
    }

    [Fact]
    public void A_failed_phases_own_detail_is_preferred_over_the_run_error()
    {
        var n = Build(new[] { Tape("merge", 1, status: PhaseStatus.Failed, summary: "merge conflict in auth.ts") },
            WorkflowRunStatus.Failure, error: "Node 'sup' failed: generic wrapper");

        n.Blocks.OfType<DiagnosticBlock>().ShouldHaveSingleItem().Text.ShouldBe("merge conflict in auth.ts");
    }

    [Fact]
    public void Ask_human_narrates_the_question_and_the_turn_is_waiting()
    {
        var n = Build(new[] { Tape("ask_human", 1, status: PhaseStatus.Waiting, summary: "Which database? — Postgres") },
            WorkflowRunStatus.Suspended);

        n.Blocks.OfType<NarrativeStepBlock>().ShouldHaveSingleItem().Text.ShouldBe("Which database? — Postgres");
        n.Summary.ShouldBeNull("an in-progress / waiting turn has no headline lead — the status word conveys it");
    }

    [Fact]
    public void A_zero_agent_spawn_emits_no_line_and_no_group()
    {
        var n = Build(new[] { Tape("plan", 1), Tape("spawn", 2, agentCount: 0, label: "Spawn") }, WorkflowRunStatus.Running);

        n.Blocks.OfType<NarrativeStepBlock>().Select(b => b.Text).ShouldBe(new[] { "Planned the approach." }, "a 0-agent spawn is a no-op — no 'Dispatched 0 agents' noise");
        n.Blocks.OfType<AgentGroupBlock>().ShouldBeEmpty();
    }

    [Fact]
    public void An_agent_card_carries_its_result_summary()
    {
        var phase = SpawnWith(new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Succeeded), Label = "Wire the API", Summary = "Added the login endpoint and tests." });

        Build(new[] { phase }, WorkflowRunStatus.Success).Blocks.OfType<AgentGroupBlock>().Single().Agents.Single()
            .Summary.ShouldBe("Added the login endpoint and tests.", "the agent's model-authored result takeaway shows on the card");
    }

    [Fact]
    public void Map_status_maps_each_phase_status_to_the_step_vocabulary()
    {
        var n = Build(new[]
        {
            Structural("a", "A", 1, PhaseStatus.Active),
            Structural("b", "B", 2, PhaseStatus.Waiting),
            Structural("c", "C", 3, PhaseStatus.Failed),
            Structural("d", "D", 4, PhaseStatus.Succeeded),
            Structural("e", "E", 5, PhaseStatus.Skipped),
        }, WorkflowRunStatus.Running);

        n.Map!.Steps.Select(s => s.Status).ShouldBe(new[]
        {
            ExecutionStepStatus.Running, ExecutionStepStatus.Blocked, ExecutionStepStatus.Failed, ExecutionStepStatus.Done, ExecutionStepStatus.Pending,
        });
    }

    [Fact]
    public void Pending_decisions_are_appended_as_the_current_ask()
    {
        var decision = new DecisionBlock { Id = "decision-x", Seq = 7, DecisionId = Guid.NewGuid(), Question = "Proceed?", Shape = "confirm" };

        var n = RoomNarrative.Build("turn-1", 7, new[] { Tape("plan", 1) }, WorkflowRunStatus.Suspended, null, new[] { decision });

        n.Blocks.OfType<DecisionBlock>().ShouldHaveSingleItem().ShouldBe(decision);
    }

    [Fact]
    public void An_empty_run_has_no_map_and_no_lead()
    {
        var n = Build(Array.Empty<RunPhase>());

        n.Map.ShouldBeNull();
        n.Blocks.ShouldBeEmpty();
        n.Summary.ShouldBeNull("no model summary → no generic 'Done.' lead; the status word conveys completion");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static RoomNarrative.TurnNarrative Build(IReadOnlyList<RunPhase> phases, WorkflowRunStatus status = WorkflowRunStatus.Success, string? error = null) =>
        RoomNarrative.Build("turn-1", 7, phases, status, error, Array.Empty<DecisionBlock>());

    private static string Describe(RoomBlock b) => b switch
    {
        NarrativeStepBlock s => $"line:{s.Text}",
        AgentGroupBlock g => $"group:{g.Title}",
        DecisionBlock d => $"decision:{d.Question}",
        DiagnosticBlock x => $"diag:{x.Text}",
        _ => b.GetType().Name,
    };

    private static RunPhase SpawnWith(params PhaseAgentRef[] agents) => new()
    {
        Id = "decision-1",
        Label = $"Spawn {agents.Length} agents",
        Kind = SupervisorDecisionKinds.Spawn,
        Status = PhaseStatus.Active,
        Order = SupervisorPhaseSource.OrderBase + 1,
        SourceKey = SupervisorPhaseSource.Key,
        Agents = agents,
        Metrics = new PhaseMetrics { AgentCount = agents.Length },
        StartedAt = DateTimeOffset.UnixEpoch,
    };

    private static RunPhase Tape(string kind, int seq, PhaseStatus status = PhaseStatus.Succeeded, string? summary = null, int agentCount = 0, string? label = null) => new()
    {
        Id = $"decision-{seq}",
        Label = label ?? kind,
        Kind = kind,
        Status = status,
        Order = SupervisorPhaseSource.OrderBase + seq,
        SourceKey = SupervisorPhaseSource.Key,
        Summary = summary,
        Agents = Agents(agentCount),
        Metrics = new PhaseMetrics { AgentCount = agentCount },
        StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(seq),
    };

    private static RunPhase Authored(string title, int index) => new()
    {
        Id = $"phase-{index}",
        Label = title,
        Kind = SupervisorPhaseSource.AuthoredPhaseKind,
        Status = PhaseStatus.Succeeded,
        Order = SupervisorPhaseSource.PhaseOrderBase + index,
        SourceKey = SupervisorPhaseSource.Key,
    };

    private static RunPhase Structural(string id, string label, int order, PhaseStatus status = PhaseStatus.Succeeded, int agentCount = 0) => new()
    {
        Id = id,
        Label = label,
        Kind = "node",
        Status = status,
        Order = order,
        SourceKey = WorkflowNodePhaseSource.Key,
        Agents = Agents(agentCount),
        Metrics = new PhaseMetrics { AgentCount = agentCount },
        StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(order),
    };

    private static IReadOnlyList<PhaseAgentRef> Agents(int n) =>
        Enumerable.Range(0, n).Select(i => new PhaseAgentRef
        {
            AgentRunId = Guid.NewGuid(),
            Status = nameof(AgentRunStatus.Succeeded),
            Label = $"agent-{i}",
            InputTokens = 100,
            OutputTokens = 50,
            Model = "opus",
            FilesChanged = 2,
        }).ToList();
}
