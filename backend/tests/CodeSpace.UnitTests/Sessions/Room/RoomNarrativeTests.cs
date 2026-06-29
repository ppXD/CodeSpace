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
/// The PURE narrative engine — where the backend OWNS the Session Room's copy + order. Proves the design's shape: the
/// canonical Plan→Work→Review→Deliver execution map (folded from the decision tape + run lifecycle + acceptance), the
/// stat rows (subtasks / files / tools / reasoning) from the projector facts, the delivery (PR) card, and the rich
/// failure diagnostic (humanized cause + typed remediation + raw-behind-disclosure). No engine jargon ever reaches a block.
/// </summary>
[Trait("Category", "Unit")]
public class RoomNarrativeTests
{
    [Fact]
    public void A_supervisor_success_maps_to_the_canonical_lifecycle_with_per_stage_detail()
    {
        var n = Build(new[]
        {
            Tape("plan", 1, label: "Plan"),
            Tape("spawn", 2, agentCount: 3, label: "Spawn 3 agents"),
            Tape("stop", 3, summary: "Shipped the login flow.", label: "Stop"),
        });

        n.Map.ShouldNotBeNull();
        n.Map!.Steps.Select(s => s.Label).ShouldBe(new[] { "Plan", "Work", "Review", "Deliver" }, "a supervisor turn maps to the canonical lifecycle, not the model's phase titles or the decision tape");
        n.Map!.Steps.Select(s => s.Status).ShouldBe(new[] { ExecutionStepStatus.Done, ExecutionStepStatus.Done, ExecutionStepStatus.Done, ExecutionStepStatus.Done });
        n.Map!.Steps.Single(s => s.Label == "Work").Detail.ShouldBe("3 agents");
        n.Map!.Steps.Single(s => s.Label == "Review").Detail.ShouldBe("passed");

        n.Summary.ShouldBe("Shipped the login flow.", "the stop summary is the turn headline");
    }

    [Fact]
    public void The_canonical_map_folds_lifecycle_status_for_running_and_failed_turns()
    {
        var runningSpawn = new RunPhase
        {
            Id = "decision-2", Label = "Spawn 2 agents", Kind = SupervisorDecisionKinds.Spawn, Status = PhaseStatus.Active,
            Order = SupervisorPhaseSource.OrderBase + 2, SourceKey = SupervisorPhaseSource.Key,
            Agents = new[]
            {
                new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Succeeded) },
                new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Running) },
            },
            Metrics = new PhaseMetrics { AgentCount = 2 },
            StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
        };

        var running = Build(new[] { Tape("plan", 1), runningSpawn }, WorkflowRunStatus.Running);
        var work = running.Map!.Steps.Single(s => s.Label == "Work");
        work.Status.ShouldBe(ExecutionStepStatus.Running);
        work.Detail.ShouldBe("1 of 2", "live progress while agents run");
        running.Map!.Steps.Single(s => s.Label == "Review").Status.ShouldBe(ExecutionStepStatus.Queued);

        var failed = Build(new[] { Tape("plan", 1), Tape("spawn", 2, agentCount: 1) }, WorkflowRunStatus.Failure);
        failed.Map!.Steps.Single(s => s.Label == "Review").Status.ShouldBe(ExecutionStepStatus.Failed);
        failed.Map!.Steps.Single(s => s.Label == "Deliver").Status.ShouldBe(ExecutionStepStatus.Skipped);
    }

    [Fact]
    public void The_acceptance_verdict_overrides_the_review_stage()
    {
        var passed = Build(new[] { Tape("plan", 1), Tape("spawn", 2, agentCount: 1) }, WorkflowRunStatus.Running, facts: new RoomTurnFacts { AcceptancePassed = true });
        passed.Map!.Steps.Single(s => s.Label == "Review").Status.ShouldBe(ExecutionStepStatus.Done, "an explicit passed grade marks review done even mid-run");

        // Discriminating: a still-RUNNING turn whose work is done would show Review = Running without the grade; an explicit
        // failed grade overrides it to Failed — isolating the override (not the run status).
        var rejected = Build(new[] { Tape("plan", 1), Tape("spawn", 2, agentCount: 1) }, WorkflowRunStatus.Running, facts: new RoomTurnFacts { AcceptancePassed = false });
        rejected.Map!.Steps.Single(s => s.Label == "Review").Status.ShouldBe(ExecutionStepStatus.Failed);
    }

    [Fact]
    public void The_work_stage_folds_resolve_agents_not_just_spawn_and_retry()
    {
        var resolve = new RunPhase
        {
            Id = "decision-2", Label = "Resolve conflict", Kind = SupervisorDecisionKinds.Resolve, Status = PhaseStatus.Active,
            Order = SupervisorPhaseSource.OrderBase + 2, SourceKey = SupervisorPhaseSource.Key,
            Agents = new[] { new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Running) } },
            Metrics = new PhaseMetrics { AgentCount = 1 },
            StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
        };

        var n = Build(new[] { Tape("plan", 1), resolve }, WorkflowRunStatus.Running);

        n.Map!.Steps.Single(s => s.Label == "Work").Status.ShouldBe(ExecutionStepStatus.Running, "a resolver agent folds into Work (StagesAgents = Spawn|Retry|Resolve), not Pending");
    }

    [Fact]
    public void A_needs_review_agent_counts_as_terminal_not_active()
    {
        var spawn = new RunPhase
        {
            Id = "decision-2", Label = "Spawn 1 agent", Kind = SupervisorDecisionKinds.Spawn, Status = PhaseStatus.Active,
            Order = SupervisorPhaseSource.OrderBase + 2, SourceKey = SupervisorPhaseSource.Key,
            Agents = new[] { new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.NeedsReview) } },
            Metrics = new PhaseMetrics { AgentCount = 1 },
            StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
        };

        var n = Build(new[] { Tape("plan", 1), spawn }, WorkflowRunStatus.Suspended);

        n.Map!.Steps.Single(s => s.Label == "Work").Status.ShouldBe(ExecutionStepStatus.Done, "NeedsReview is terminal — Work isn't stuck Running");
    }

    [Fact]
    public void Stat_rows_surface_subtasks_files_tools_and_reasoning_from_the_facts()
    {
        var facts = new RoomTurnFacts
        {
            Subtasks = new[] { "Trace DI registration", "Analyze the template store" },
            ChangedFiles = new[] { "a.cs", "b.cs", "c.cs" },
            Additions = 148,
            Deletions = 32,
            ToolCalls = 14,
            ReasoningCount = 5,
        };

        var stats = Build(new[] { Tape("plan", 1) }, facts: facts).Blocks.OfType<StatBlock>().ToList();

        var subtasks = stats.Single(s => s.Kind == "subtasks");
        subtasks.Label.ShouldBe("Planned 2 subtasks");
        subtasks.Items.Select(i => i.Text).ShouldBe(new[] { "Trace DI registration", "Analyze the template store" });

        var files = stats.Single(s => s.Kind == "files");
        files.Label.ShouldBe("Changed 3 files");
        files.Detail.ShouldBe("+148 −32", "the captured diff line stat, pinned including the U+2212 minus");
        files.Items.Count.ShouldBe(3);

        stats.Single(s => s.Kind == "tools").Label.ShouldBe("14 tool calls");
        stats.Single(s => s.Kind == "reasoning").Detail.ShouldBe("5 steps");
    }

    [Fact]
    public void A_files_row_without_a_captured_diff_stat_omits_the_plus_minus()
    {
        var facts = new RoomTurnFacts { ChangedFiles = new[] { "only.cs" } };   // no Additions/Deletions captured

        var files = Build(new[] { Tape("plan", 1) }, facts: facts).Blocks.OfType<StatBlock>().Single(s => s.Kind == "files");
        files.Label.ShouldBe("Changed 1 file", "singular");
        files.Detail.ShouldBeNull("the diff +/- is a graceful gap — the row just omits it");
    }

    [Fact]
    public void A_delivery_card_is_emitted_from_the_facts()
    {
        var facts = new RoomTurnFacts { Delivery = new RoomDelivery { Title = "Rename run agent", Reference = "#128", BranchHead = "feat/run-agent", BranchBase = "main", Url = "https://x/pr/128" } };

        var d = Build(new[] { Tape("plan", 1) }, facts: facts).Blocks.OfType<DeliveryBlock>().ShouldHaveSingleItem();
        d.Title.ShouldBe("Rename run agent");
        d.Reference.ShouldBe("#128");
        d.BranchHead.ShouldBe("feat/run-agent");
        d.BranchBase.ShouldBe("main");
        d.Url.ShouldBe("https://x/pr/128");
    }

    [Fact]
    public void An_auth_failure_gets_a_titled_diagnostic_with_a_fix_credentials_action()
    {
        var facts = new RoomTurnFacts { RawError = "Authentication Error — the API key was rejected (HTTP 401)" };

        var diag = Build(Array.Empty<RunPhase>(), WorkflowRunStatus.Failure, facts: facts).Blocks.OfType<DiagnosticBlock>().ShouldHaveSingleItem();
        diag.Title.ShouldBe("Authentication failed");
        diag.Actions.ShouldContain(a => a.Kind == RoomActionKind.FixCredentials);
        diag.RawDetail.ShouldNotBeNull("the raw 401 error is kept behind 'Show raw error'");
    }

    [Fact]
    public void A_non_auth_failure_humanizes_the_engine_error()
    {
        var diag = Build(Array.Empty<RunPhase>(), WorkflowRunStatus.Failure, error: "Node 'sup' failed: the agent could not compile the project")
            .Blocks.OfType<DiagnosticBlock>().ShouldHaveSingleItem();

        diag.Title.ShouldBeNull();
        diag.Text.ShouldBe("the agent could not compile the project", "the 'Node x failed:' engine prefix is stripped to the real cause");
    }

    [Fact]
    public void A_bare_engine_failure_falls_back_to_a_plain_sentence()
    {
        Build(Array.Empty<RunPhase>(), WorkflowRunStatus.Failure, error: "Node 'sup' failed")
            .Blocks.OfType<DiagnosticBlock>().ShouldHaveSingleItem().Text.ShouldBe("This turn ended with an error.");
    }

    [Fact]
    public void A_cancelled_turn_reads_as_cancelled()
    {
        Build(Array.Empty<RunPhase>(), WorkflowRunStatus.Cancelled)
            .Blocks.OfType<DiagnosticBlock>().ShouldHaveSingleItem().Text.ShouldBe("This turn was cancelled.");
    }

    [Fact]
    public void A_failed_phases_own_detail_is_preferred_over_the_run_error()
    {
        Build(new[] { Tape("merge", 1, status: PhaseStatus.Failed, summary: "merge conflict in auth.ts") }, WorkflowRunStatus.Failure, error: "Node 'sup' failed: generic wrapper")
            .Blocks.OfType<DiagnosticBlock>().ShouldHaveSingleItem().Text.ShouldBe("merge conflict in auth.ts");
    }

    [Fact]
    public void A_single_agent_run_uses_the_structural_node_as_the_map()
    {
        var n = Build(new[] { Structural("agent", "Run the agent", order: 1, agentCount: 1) });

        n.Map!.Steps.ShouldHaveSingleItem().Label.ShouldBe("Run the agent");
        n.Blocks.ShouldBeEmpty("no facts → no stat rows; no narrative-line / agent-card chatter in the body");
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

        var n = RoomNarrative.Build("turn-1", 7, new[] { Tape("plan", 1) }, WorkflowRunStatus.Suspended, null, new[] { decision }, RoomTurnFacts.Empty);

        n.Blocks.OfType<DecisionBlock>().ShouldHaveSingleItem().ShouldBe(decision);
    }

    [Fact]
    public void An_empty_run_has_no_map_and_no_lead()
    {
        var n = Build(Array.Empty<RunPhase>());

        n.Map.ShouldBeNull();
        n.Blocks.ShouldBeEmpty();
        n.Summary.ShouldBeNull("no model summary → no generic lead; the status word conveys completion");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static RoomNarrative.TurnNarrative Build(IReadOnlyList<RunPhase> phases, WorkflowRunStatus status = WorkflowRunStatus.Success, string? error = null, RoomTurnFacts? facts = null) =>
        RoomNarrative.Build("turn-1", 7, phases, status, error, Array.Empty<DecisionBlock>(), facts ?? RoomTurnFacts.Empty);

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
