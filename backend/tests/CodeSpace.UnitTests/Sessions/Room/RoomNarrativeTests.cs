using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Nodes;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;
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
        n.Map!.Steps.Select(s => s.Label).ShouldBe(new[] { "Start", "Plan", "Work", "Review", "Deliver" }, "the DYNAMIC map — a Start head, Plan, the work (a flat plan collapses to one Work step), Review, Deliver");
        n.Map!.Steps.Select(s => s.Status).ShouldBe(new[] { ExecutionStepStatus.Done, ExecutionStepStatus.Done, ExecutionStepStatus.Done, ExecutionStepStatus.Done, ExecutionStepStatus.Done });
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
    public void Turn_level_stats_surface_files_and_tools_after_the_rounds()
    {
        // Subtasks now live PER ROUND (not a flat stat), and the Reasoning stat is dropped (folded into the live indicator);
        // the turn-level Files-changed + Tools rows remain, emitted after the per-round blocks.
        var facts = new RoomTurnFacts
        {
            ChangedFiles = new[] { "a.cs", "b.cs", "c.cs" },
            Additions = 148,
            Deletions = 32,
            ToolCalls = 14,
            ReasoningCount = 5,
        };

        var stats = Build(new[] { Tape("plan", 1) }, facts: facts).Blocks.OfType<StatBlock>().ToList();

        var files = stats.Single(s => s.Kind == "files");
        files.Label.ShouldBe("Files changed");
        files.Detail.ShouldBe("+148 −32 · 3 files", "the captured diff line stat (U+2212 minus) plus the file count");
        files.Items.Count.ShouldBe(3);

        var tools = stats.Single(s => s.Kind == "tools");
        tools.Label.ShouldBe("Tools");
        tools.Detail.ShouldBe("14 calls", "no histogram captured → just the count");

        stats.ShouldNotContain(s => s.Kind == "reasoning", "the Reasoning stat is dropped — folded into the live indicator");
    }

    [Fact]
    public void A_files_row_without_a_captured_diff_stat_omits_the_plus_minus()
    {
        var facts = new RoomTurnFacts { ChangedFiles = new[] { "only.cs" } };   // no Additions/Deletions captured

        var files = Build(new[] { Tape("plan", 1) }, facts: facts).Blocks.OfType<StatBlock>().Single(s => s.Kind == "files");
        files.Label.ShouldBe("Files changed");
        files.Detail.ShouldBe("1 file", "the diff +/- is a graceful gap — the detail is just the (singular) file count, no +X −Y");
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

    [Fact]
    public void A_supervisor_turn_emits_one_agent_group_with_a_card_per_spawned_agent()
    {
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var spawn = new RunPhase
        {
            Id = "decision-2", Label = "Spawn 2 agents", Kind = SupervisorDecisionKinds.Spawn, Status = PhaseStatus.Succeeded,
            Order = SupervisorPhaseSource.OrderBase + 2, SourceKey = SupervisorPhaseSource.Key,
            Agents = new[]
            {
                new PhaseAgentRef { AgentRunId = a1, Status = nameof(AgentRunStatus.Succeeded), Role = "CLI command", InputTokens = 100, OutputTokens = 50, Model = "opus", FilesChanged = 3, ToolCount = 6, DurationMs = 41_000 },
                new PhaseAgentRef { AgentRunId = a2, Status = nameof(AgentRunStatus.Succeeded), Role = "Docs & help", FilesChanged = 2, ToolCount = 3, DurationMs = 29_000 },
            },
            Metrics = new PhaseMetrics { AgentCount = 2 },
            StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
        };

        var facts = new RoomTurnFacts { AgentSummaries = new Dictionary<Guid, string> { [a1] = "Renamed the command and registered the alias." } };

        var group = Build(new[] { Tape("plan", 1), spawn, Tape("stop", 3, summary: "Done.") }, facts: facts).Blocks.OfType<AgentGroupBlock>().Single();

        group.Title.ShouldBe("Agents", "a terminal turn reads 'Agents'; a live turn reads 'Work'");
        group.Agents.Count.ShouldBe(2);

        var card = group.Agents.Single(c => c.AgentRunId == a1);
        card.Label.ShouldBe("CLI command", "the model-authored role is the card name");
        card.FilesChanged.ShouldBe(3);
        card.ToolCount.ShouldBe(6);
        card.DurationMs.ShouldBe(41_000);
        card.Tokens.ShouldBe(150, "input + output");
        card.Summary.ShouldBe("Renamed the command and registered the alias.", "the agent's own result takeaway");
        group.Agents.Single(c => c.AgentRunId == a2).Summary.ShouldBeNull("no summary captured for this agent");
    }

    [Fact]
    public void A_map_fanout_run_humanizes_node_labels_and_groups_its_agents()
    {
        var n = Build(new[]
        {
            Structural("start", "start", 1),
            Structural("planner", "planner", 2),
            Structural("fan", "Fan out", 3, agentCount: 3),
        }, WorkflowRunStatus.Running);

        n.Map!.Steps.Select(s => s.Label).ShouldBe(new[] { "Start", "Plan", "Work" }, "technical node labels are humanized for the reader (Fan out → Work)");

        var group = n.Blocks.OfType<AgentGroupBlock>().Single();
        group.Title.ShouldBe("Work", "a live run reads 'Work'");
        group.Agents.Count.ShouldBe(3, "the fan-out branch agents surface as cards even though it's not a supervisor spawn");
    }

    [Fact]
    public void A_live_turn_labels_the_agent_group_Work()
    {
        var spawn = new RunPhase
        {
            Id = "decision-2", Label = "Spawn", Kind = SupervisorDecisionKinds.Spawn, Status = PhaseStatus.Active,
            Order = SupervisorPhaseSource.OrderBase + 2, SourceKey = SupervisorPhaseSource.Key,
            Agents = new[]
            {
                new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Running) },
                new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Queued) },
            },
            Metrics = new PhaseMetrics { AgentCount = 2 }, StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
        };

        Build(new[] { Tape("plan", 1), spawn }, WorkflowRunStatus.Running).Blocks.OfType<AgentGroupBlock>().Single().Title.ShouldBe("Work");
    }

    [Fact]
    public void Without_a_stop_summary_the_lead_is_composed_from_the_agents_result_summaries()
    {
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var spawn = new RunPhase
        {
            Id = "decision-2", Label = "Spawn 2 agents", Kind = SupervisorDecisionKinds.Spawn, Status = PhaseStatus.Succeeded,
            Order = SupervisorPhaseSource.OrderBase + 2, SourceKey = SupervisorPhaseSource.Key,
            Agents = new[]
            {
                new PhaseAgentRef { AgentRunId = a1, Status = nameof(AgentRunStatus.Succeeded) },
                new PhaseAgentRef { AgentRunId = a2, Status = nameof(AgentRunStatus.Succeeded) },
            },
            Metrics = new PhaseMetrics { AgentCount = 2 }, StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
        };

        var facts = new RoomTurnFacts { AgentSummaries = new Dictionary<Guid, string> { [a1] = "Renamed the command", [a2] = "Made --repo optional." } };

        // No Stop phase → no stop summary → the reply lead is stitched from the agents' own summaries, in spawn order.
        Build(new[] { Tape("plan", 1), spawn }, facts: facts).Summary.ShouldBe("Renamed the command. Made --repo optional.");
    }

    [Fact]
    public void A_single_agent_turn_with_no_supervisor_leads_with_that_agents_own_summary()
    {
        var a1 = Guid.NewGuid();

        // A plain agent turn has NO supervisor tape, so ComposeLead (which walks tape agents) sees nothing; the reply
        // still isn't voiceless — it leads with the sole agent's own summary.
        var solo = new RoomTurnFacts { AgentSummaries = new Dictionary<Guid, string> { [a1] = "Printed PONG." } };
        Build(Array.Empty<RunPhase>(), facts: solo).Summary.ShouldBe("Printed PONG.", "no supervisor tape → the reply leads with the sole agent's own summary");

        // The sole-agent fallback fires ONLY for exactly one summary — two agents with no tape have no single voice.
        var two = new RoomTurnFacts { AgentSummaries = new Dictionary<Guid, string> { [a1] = "A", [Guid.NewGuid()] = "B" } };
        Build(Array.Empty<RunPhase>(), facts: two).Summary.ShouldBeNull("two agents with no tape → no sole-agent lead (ComposeLead needs the tape)");
    }

    [Fact]
    public void With_no_summaries_at_all_the_lead_falls_back_to_a_factual_recap()
    {
        // No stop summary, no agent summaries (e.g. agents hit provider errors) → the reply still isn't voiceless.
        var facts = new RoomTurnFacts { Subtasks = new[] { "a", "b", "c", "d", "e" }, ChangedFiles = new[] { "x.cs", "y.cs" } };

        Build(new[] { Tape("plan", 1) }, facts: facts).Summary.ShouldBe("Worked through 5 subtasks and changed 2 files.");
    }

    [Fact]
    public void Tools_row_summarizes_to_the_call_total_and_expands_to_the_per_tool_breakdown()
    {
        var facts = new RoomTurnFacts
        {
            ToolCalls = 14,
            ToolHistogram = new[] { new ToolKindCount("Read", 6), new ToolKindCount("WebSearch", 5), new ToolKindCount("Write", 3) },
        };

        var tools = Build(new[] { Tape("plan", 1) }, facts: facts).Blocks.OfType<StatBlock>().Single(s => s.Kind == "tools");
        tools.Detail.ShouldBe("14 calls", "collapsed: just the total, no inline histogram");
        tools.Items.Select(i => (i.Text, i.Detail)).ShouldBe(new[] { ("Read", "6"), ("WebSearch", "5"), ("Write", "3") }, "expanded: the per-tool breakdown");
    }

    [Fact]
    public void Agent_cards_carry_their_own_files_and_the_final_answer_attributes_a_file_to_its_producer()
    {
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();

        var spawn = new RunPhase
        {
            Id = "decision-2", Label = "Spawn 2 agents", Kind = SupervisorDecisionKinds.Spawn, Status = PhaseStatus.Succeeded,
            Order = SupervisorPhaseSource.OrderBase + 2, SourceKey = SupervisorPhaseSource.Key,
            Agents = new[]
            {
                new PhaseAgentRef { AgentRunId = a1, Status = nameof(AgentRunStatus.Succeeded), AssignedSubtask = "Research" },
                new PhaseAgentRef { AgentRunId = a2, Status = nameof(AgentRunStatus.Succeeded), AssignedSubtask = "Synthesize" },
            },
            Metrics = new PhaseMetrics { AgentCount = 2 }, StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
        };

        // Only the research agent (a1) produced a file; the final answer lists it. The synthesis agent (a2) produced none.
        var facts = new RoomTurnFacts
        {
            AgentFiles = new Dictionary<Guid, IReadOnlyList<string>> { [a1] = new[] { "report.md" } },
            FinalAnswer = new RoomFinalAnswer { Text = "Done.", Attachments = new[] { new RoomAttachment(AnswerAttachmentKind.FileLink, "report.md", null, null, null) } },
        };

        var n = Build(new[] { Tape("plan", 1), spawn }, WorkflowRunStatus.Success, facts: facts);

        var cards = n.Blocks.OfType<AgentGroupBlock>().SelectMany(g => g.Agents).ToList();
        cards.Single(c => c.AgentRunId == a1).ChangedFiles.ShouldBe(new[] { "report.md" }, "each agent carries its OWN files");
        cards.Single(c => c.AgentRunId == a2).ChangedFiles.ShouldBeEmpty("the agent that produced nothing shows no files");

        var file = n.Blocks.OfType<FinalAnswerBlock>().Single().Attachments!.Single(a => a.Kind == AnswerAttachmentKind.FileLink);
        file.AgentRunId.ShouldBe(a1, "the RESULT file is attributed to its producing agent, not the final answer wholesale");
        file.Producer.ShouldBe("Research");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    // ─── the plan checklist (triad S2b) ─────────────────────────────────────────

    [Fact]
    public void A_checklist_leads_the_turn_and_maps_the_full_contract()
    {
        var checklist = Checklist(
            Item("s1", "First", state: WorkPlanItemStates.Completed, kind: "research"),
            Item("s2", "Second", state: WorkPlanItemStates.InProgress, dependsOn: new[] { "s1" },
                acceptance: new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } },
                criteria: new[] { "covers edge cases" }, agentRunId: Guid.NewGuid(), attempts: 2));

        var n = Build(new[] { Tape("plan", 1), Tape("spawn", 2, agentCount: 2) }, WorkflowRunStatus.Running, facts: new RoomTurnFacts { Checklist = checklist });

        var block = n.Blocks[0].ShouldBeOfType<PlanChecklistBlock>("the checklist LEADS the turn — the whole-plan tracker before the round narrative");
        block.Label.ShouldBe("Plan");
        block.Version.ShouldBe(2);
        block.HasPriorVersions.ShouldBeTrue("v2 implies a superseded v1");
        block.Detail.ShouldBe("2 items · 1 done · 1 running", "the item count plus every non-zero non-pending state");

        var first = block.Items[0];
        first.Ordinal.ShouldBe(1);
        first.Kind.ShouldBe("research");
        first.State.ShouldBe(WorkPlanItemStates.Completed);
        first.DependsOn.ShouldBeEmpty();
        first.AcceptanceLabel.ShouldBeNull("no objective contract → no chip");

        var second = block.Items[1];
        second.DependsOn.ShouldBe(new[] { 1 }, "dependency IDS become 1-based ordinals — the reader never sees plan-local ids");
        second.AcceptanceLabel.ShouldBe("sh check.sh");
        second.AcceptanceKind.ShouldBe(nameof(BenchmarkGradingKind.TestsPass), "an unspecified oracle kind defaults to TestsPass");
        second.AcceptanceCriteria.ShouldBe(new[] { "covers edge cases" });
        second.Attempts.ShouldBe(2);
        second.AgentRunId.ShouldNotBeNull();
    }

    [Fact]
    public void A_checklist_suppresses_the_per_round_plan_stat_rows()
    {
        var facts = new RoomTurnFacts
        {
            Checklist = Checklist(Item("s1", "First", state: WorkPlanItemStates.Pending)),
            Rounds = new[] { new RoomRound { Index = 1, Subtasks = new[] { "First" } } },
        };

        var withChecklist = Build(new[] { Tape("plan", 1) }, WorkflowRunStatus.Running, facts: facts);
        withChecklist.Blocks.OfType<StatBlock>().Where(b => b.Kind == "subtasks").ShouldBeEmpty("the checklist subsumes the plan rows — titles never render twice");
        withChecklist.Blocks.OfType<PlanChecklistBlock>().Count().ShouldBe(1);

        // The byte-identity floor: a plan-less run (no work_plan row) projects exactly as before.
        var without = Build(new[] { Tape("plan", 1) }, WorkflowRunStatus.Running, facts: new RoomTurnFacts { Rounds = facts.Rounds });
        without.Blocks.OfType<StatBlock>().Count(b => b.Kind == "subtasks").ShouldBe(1, "no checklist → the legacy per-round plan row");
        without.Blocks.OfType<PlanChecklistBlock>().ShouldBeEmpty();
    }

    [Fact]
    public void A_checklist_suppresses_the_authored_phase_plan_rows_too()
    {
        // The L4 model-authored-phase path (the OTHER suppression branch): an authored round's own plan row
        // must also yield to the checklist, while its agent group still renders.
        var authored = new RunPhase
        {
            Id = "phase-0", Label = "调研与分析", Kind = SupervisorPhaseSource.AuthoredPhaseKind, Status = PhaseStatus.Succeeded,
            Order = SupervisorPhaseSource.OrderBase + 10, SourceKey = SupervisorPhaseSource.Key,
            Agents = new[] { new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Succeeded), AssignedSubtask = "First" } },
            Metrics = new PhaseMetrics { AgentCount = 1 },
        };

        var facts = new RoomTurnFacts { Checklist = Checklist(Item("s1", "First", state: WorkPlanItemStates.Completed)) };

        var n = Build(new[] { Tape("plan", 1), authored }, WorkflowRunStatus.Success, facts: facts);

        n.Blocks.OfType<StatBlock>().Where(b => b.Kind == "subtasks").ShouldBeEmpty("the authored round's plan row yields to the checklist");
        n.Blocks.OfType<PlanChecklistBlock>().Count().ShouldBe(1);
        n.Blocks.OfType<AgentGroupBlock>().Count().ShouldBe(1, "the phase's agent cards still render");
    }

    [Fact]
    public void A_supervisor_retry_emits_a_retried_step_after_the_agent_group_on_the_authored_path()
    {
        // The goal's branch: a phased (authored) round whose Research phase carries a failed original + its retry.
        var authored = new RunPhase
        {
            Id = "phase-0", Label = "Research", Kind = SupervisorPhaseSource.AuthoredPhaseKind, Status = PhaseStatus.Failed,
            Order = SupervisorPhaseSource.OrderBase + 10, SourceKey = SupervisorPhaseSource.Key,
            Agents = new[]
            {
                new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Failed), AssignedSubtask = "Analyze repos" },
                new PhaseAgentRef { AgentRunId = Guid.NewGuid(), Status = nameof(AgentRunStatus.Succeeded), AssignedSubtask = "Analyze repos" },
            },
            Metrics = new PhaseMetrics { AgentCount = 2, FailedCount = 1, SucceededCount = 1 },
        };

        var facts = new RoomTurnFacts { RetrySteps = new[] { new RoomRetryStep(3, "Supervisor retried a subtask") } };

        var n = Build(new[] { authored }, WorkflowRunStatus.Success, facts: facts);
        var kinds = n.Blocks.ToList();

        n.Blocks.OfType<NarrativeStepBlock>().ShouldContain(s => s.Text == "Supervisor retried a subtask" && s.Tone == NarrativeTone.Info,
            "the retry beat renders as a step even on the authored-phase path (the branch the goal hits)");

        var groupIdx = kinds.FindIndex(b => b is AgentGroupBlock);
        var retryIdx = kinds.FindIndex(b => b is NarrativeStepBlock st && st.Text == "Supervisor retried a subtask");
        retryIdx.ShouldBeGreaterThan(groupIdx, "the retry step reads AFTER the agent cards (the failed original + retry are already visible above)");
    }

    [Fact]
    public void No_retry_steps_render_no_retried_narrative_line()
    {
        var n = Build(new[] { Tape("plan", 1), Tape("spawn", 2, agentCount: 1) }, WorkflowRunStatus.Success);

        n.Blocks.OfType<NarrativeStepBlock>().ShouldNotContain(s => s.Text.Contains("retried"), "no retries → no retry step (byte-identity floor)");
    }

    [Fact]
    public void Checklist_questions_render_with_the_recommended_option_flagged()
    {
        var checklist = Checklist(Item("s1", "First", state: WorkPlanItemStates.Pending)) with
        {
            Status = WorkPlanStatuses.Authored,
            Questions = new[]
            {
                new WorkPlanQuestion
                {
                    Id = "q1", Question = "Which direction?", AllowFreeText = true, RecommendedOptionId = "a",
                    Options = new[] { new WorkPlanQuestionOption { Id = "a", Label = "Fast" }, new WorkPlanQuestionOption { Id = "b", Label = "Thorough" } },
                },
            },
            Assumptions = new[] { "assumed the default branch" },
        };

        var block = Build(new[] { Tape("plan", 1) }, facts: new RoomTurnFacts { Checklist = checklist }).Blocks.OfType<PlanChecklistBlock>().Single();

        block.Assumptions.ShouldBe(new[] { "assumed the default branch" });
        var q = block.Questions.Single();
        q.Question.ShouldBe("Which direction?");
        q.AllowFreeText.ShouldBeTrue();
        q.Options.Single(o => o.Id == "a").Recommended.ShouldBeTrue("the planner's default is flagged, not a separate field the frontend must join");
        q.Options.Single(o => o.Id == "b").Recommended.ShouldBeFalse();
    }

    [Fact]
    public void An_all_pending_checklist_details_just_the_item_count()
    {
        var checklist = Checklist(Item("s1", "First", state: WorkPlanItemStates.Pending), Item("s2", "Second", state: WorkPlanItemStates.Pending)) with { Version = 1 };

        var block = Build(new[] { Tape("plan", 1) }, WorkflowRunStatus.Running, facts: new RoomTurnFacts { Checklist = checklist }).Blocks.OfType<PlanChecklistBlock>().Single();

        block.Detail.ShouldBe("2 items", "nothing started → no state segments");
        block.HasPriorVersions.ShouldBeFalse();
    }

    [Fact]
    public void A_duplicate_item_id_renders_both_lines_instead_of_killing_the_room()
    {
        // The supervisor's plan validator tolerates duplicate subtask ids (a degenerate flat plan) — the room
        // mapper must survive it: both lines render, and a dependency on the dup id resolves first-wins.
        var checklist = Checklist(
            Item("a", "First copy", state: WorkPlanItemStates.Completed),
            Item("a", "Second copy", state: WorkPlanItemStates.Pending),
            Item("b", "Dependent", state: WorkPlanItemStates.Pending, dependsOn: new[] { "a" }));

        var block = Build(new[] { Tape("plan", 1) }, facts: new RoomTurnFacts { Checklist = checklist }).Blocks.OfType<PlanChecklistBlock>().Single();

        block.Items.Count.ShouldBe(3);
        block.Items.Select(i => i.Ordinal).ShouldBe(new[] { 1, 2, 3 });
        block.Items[2].DependsOn.ShouldBe(new[] { 1 }, "a dependency on a duplicated id resolves to its FIRST occurrence");
    }

    private static WorkPlanChecklist Checklist(params WorkPlanChecklistItem[] items) => new()
    {
        PlanId = Guid.NewGuid(),
        WorkflowRunId = Guid.NewGuid(),
        Version = 2,
        Status = WorkPlanStatuses.Authored,
        OriginKind = WorkPlanOrigins.Supervisor,
        Goal = "ship it",
        Items = items,
    };

    private static WorkPlanChecklistItem Item(string id, string title, string state, string? kind = null, string[]? dependsOn = null, SupervisorAcceptanceSpec? acceptance = null, string[]? criteria = null, Guid? agentRunId = null, int attempts = 0) => new()
    {
        Item = new WorkPlanItem { Id = id, Title = title, Instruction = $"do {id}", Kind = kind, DependsOn = dependsOn, Acceptance = acceptance, AcceptanceCriteria = criteria },
        State = state,
        AgentRunId = agentRunId,
        Attempts = attempts,
    };

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
