using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
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
    public void A_spawn_with_per_agent_roles_carries_each_agents_role_and_assigned_subtask_title()
    {
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();

        // The plan PAYLOAD carries the subtask decomposition (id + title); the spawn PAYLOAD carries the fan-out order
        // (subtaskIds) + the model-authored per-agent dispatch roles (agents[]). The join is positional: subtaskIds[i] ↔
        // agentRunIds[i], so each agent gets its own subtask title + role.
        var plan = Decision(1, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded,
            payloadJson: """{"subtasks":[{"id":"sa","title":"Trace DI registration","instruction":"..."},{"id":"sb","title":"Analyze template store","instruction":"..."}]}""");
        var spawn = Decision(2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $$"""{"agentCount":2,"agentRunIds":["{{agentA}}","{{agentB}}"]}""",
            payloadJson: """{"subtaskIds":["sa","sb"],"agents":[{"subtaskId":"sa","role":"Tracer"},{"subtaskId":"sb","role":"Analyst"}]}""");

        var statuses = new Dictionary<Guid, AgentRunStatus> { [agentA] = AgentRunStatus.Running, [agentB] = AgentRunStatus.Running };

        var spawnPhase = SupervisorPhaseSource.ProjectDecisions(new[] { plan, spawn }, statuses).Single(p => p.Kind == SupervisorDecisionKinds.Spawn);

        var a = spawnPhase.Agents.Single(x => x.AgentRunId == agentA);
        a.Role.ShouldBe("Tracer");
        a.AssignedSubtask.ShouldBe("Trace DI registration");

        var b = spawnPhase.Agents.Single(x => x.AgentRunId == agentB);
        b.Role.ShouldBe("Analyst");
        b.AssignedSubtask.ShouldBe("Analyze template store");
    }

    [Fact]
    public void A_spawned_agents_result_summary_surfaces_on_its_ref()
    {
        var agentA = Guid.NewGuid();

        var plan = Decision(1, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded,
            payloadJson: """{"subtasks":[{"id":"sa","title":"Do it","instruction":"..."}]}""");
        var spawn = Decision(2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $$"""{"agentCount":1,"agentRunIds":["{{agentA}}"],"agentResults":[{"agentRunId":"{{agentA}}","status":"Succeeded","summary":"Added the login endpoint."}]}""",
            payloadJson: """{"subtaskIds":["sa"]}""");

        var statuses = new Dictionary<Guid, AgentRunStatus> { [agentA] = AgentRunStatus.Succeeded };

        var a = SupervisorPhaseSource.ProjectDecisions(new[] { plan, spawn }, statuses).Single(p => p.Kind == SupervisorDecisionKinds.Spawn).Agents.ShouldHaveSingleItem();

        a.Summary.ShouldBe("Added the login endpoint.", "the agent's model-authored result takeaway surfaces on its ref");
    }

    [Fact]
    public void A_homogeneous_spawn_without_an_agents_array_leaves_role_null_but_still_maps_the_subtask_title()
    {
        var agentA = Guid.NewGuid();

        var plan = Decision(1, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded,
            payloadJson: """{"subtasks":[{"id":"sa","title":"Do the thing","instruction":"..."}]}""");
        var spawn = Decision(2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $$"""{"agentCount":1,"agentRunIds":["{{agentA}}"]}""", payloadJson: """{"subtaskIds":["sa"]}""");

        var statuses = new Dictionary<Guid, AgentRunStatus> { [agentA] = AgentRunStatus.Running };

        var a = SupervisorPhaseSource.ProjectDecisions(new[] { plan, spawn }, statuses).Single(p => p.Kind == SupervisorDecisionKinds.Spawn).Agents.ShouldHaveSingleItem();

        a.Role.ShouldBeNull("a homogeneous spawn omits agents[] → no per-agent role");
        a.AssignedSubtask.ShouldBe("Do the thing", "the subtask title still joins from the plan's decomposition");
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
    public void A_retried_subtask_phase_shows_both_the_failed_original_and_the_retry()
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

        implement.Agents.Select(a => a.AgentRunId).ShouldBe(new[] { failedAgent, retryAgent }, "both attempts show — the failed original first, then the retry — so the room renders the real trajectory, not just the surviving agent");
        implement.Status.ShouldBe(PhaseStatus.Failed, "a failed attempt is present → the phase honestly reads failed (matching the run detail's failed-phase step), even though the retry ultimately recovered the subtask");
    }

    [Fact]
    public void A_multi_agent_phase_with_two_failures_and_two_retries_reads_two_failed()
    {
        var okA = Guid.NewGuid();
        var okB = Guid.NewGuid();
        var failedC = Guid.NewGuid();
        var failedD = Guid.NewGuid();
        var retryC = Guid.NewGuid();
        var retryD = Guid.NewGuid();

        var plan = Decision(1, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, outcomeJson:
            """{"planned":[],"count":4,"phases":[{"id":"research","title":"Research","subtaskIds":["sa","sb","sc","sd"]}]}""");
        var spawn = Decision(2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $$"""{"agentCount":4,"agentRunIds":["{{okA}}","{{okB}}","{{failedC}}","{{failedD}}"]}""",
            payloadJson: """{"subtaskIds":["sa","sb","sc","sd"]}""");
        var retry1 = Decision(3, SupervisorDecisionKinds.Retry, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $$"""{"agentCount":1,"agentRunIds":["{{retryC}}"]}""", payloadJson: """{"subtaskId":"sc"}""");
        var retry2 = Decision(4, SupervisorDecisionKinds.Retry, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $$"""{"agentCount":1,"agentRunIds":["{{retryD}}"]}""", payloadJson: """{"subtaskId":"sd"}""");

        var statuses = new Dictionary<Guid, AgentRunStatus>
        {
            [okA] = AgentRunStatus.Succeeded, [okB] = AgentRunStatus.Succeeded,
            [failedC] = AgentRunStatus.Failed, [failedD] = AgentRunStatus.Failed,
            [retryC] = AgentRunStatus.Succeeded, [retryD] = AgentRunStatus.Succeeded,
        };

        var research = SupervisorPhaseSource.ProjectDecisions(new[] { plan, spawn, retry1, retry2 }, statuses).Single(p => p.Kind == "phase");

        research.Agents.Count.ShouldBe(6, "the 4 spawned agents + the 2 retry agents all show — the full trajectory");
        research.Metrics.FailedCount.ShouldBe(2, "the 2 failed originals are surfaced, not hidden behind their retries");
        research.Metrics.SucceededCount.ShouldBe(4, "2 clean originals + 2 recovered retries");
        research.Status.ShouldBe(PhaseStatus.Failed, "any failed attempt makes the phase read failed");
    }

    [Fact]
    public void A_re_plan_reusing_a_subtask_id_does_not_leak_the_prior_plans_agent()
    {
        var oldAgent = Guid.NewGuid();   // plan-1's spawn for "s1" — FAILED, then abandoned by a re-plan
        var newAgent = Guid.NewGuid();   // plan-2 re-plan's fresh spawn for the SAME id "s1" — clean, no retry

        var plan1 = Decision(1, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, outcomeJson:
            """{"planned":[],"count":1,"phases":[{"id":"p","title":"Build","subtaskIds":["s1"]}]}""");
        var spawn1 = Decision(2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $$"""{"agentCount":1,"agentRunIds":["{{oldAgent}}"]}""", payloadJson: """{"subtaskIds":["s1"]}""");
        var plan2 = Decision(3, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, outcomeJson:
            """{"planned":[],"count":1,"phases":[{"id":"p","title":"Build","subtaskIds":["s1"]}]}""");
        var spawn2 = Decision(4, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded,
            outcomeJson: $$"""{"agentCount":1,"agentRunIds":["{{newAgent}}"]}""", payloadJson: """{"subtaskIds":["s1"]}""");

        var statuses = new Dictionary<Guid, AgentRunStatus> { [oldAgent] = AgentRunStatus.Failed, [newAgent] = AgentRunStatus.Succeeded };

        var build = SupervisorPhaseSource.ProjectDecisions(new[] { plan1, spawn1, plan2, spawn2 }, statuses).Single(p => p.Kind == "phase");

        build.Agents.Select(a => a.AgentRunId).ShouldBe(new[] { newAgent }, "the latest plan's phase shows ONLY its own spawn — a reused subtask id does not leak the superseded prior-plan agent");
        build.Status.ShouldBe(PhaseStatus.Succeeded, "the current plan's subtask succeeded cleanly → not falsely failed by the abandoned prior attempt");
    }

    [Fact]
    public void Spawn_child_agents_carry_the_folded_model_and_token_rollup()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var spawn = Decision(2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded,
            outcomeJson: SpawnOutcome(new[] { a, b },
                new SupervisorAgentResult { AgentRunId = a, Status = "Succeeded", Model = "claude-opus-4", InputTokens = 12000, OutputTokens = 3400 },
                new SupervisorAgentResult { AgentRunId = b, Status = "Succeeded", Model = "  ", InputTokens = 500, OutputTokens = 100 }));

        var statuses = new Dictionary<Guid, AgentRunStatus> { [a] = AgentRunStatus.Succeeded, [b] = AgentRunStatus.Succeeded };

        var agents = SupervisorPhaseSource.ProjectDecisions(new[] { spawn }, statuses).Single().Agents;

        var refA = agents.Single(x => x.AgentRunId == a);
        refA.Model.ShouldBe("claude-opus-4");
        refA.InputTokens.ShouldBe(12000);
        refA.OutputTokens.ShouldBe(3400);

        var refB = agents.Single(x => x.AgentRunId == b);
        refB.Model.ShouldBeNull("a blank model reads as null — no chip");
        refB.InputTokens.ShouldBe(500, "tokens still surface even when the model is unknown");
    }

    [Fact]
    public void Spawn_child_agents_carry_the_priced_cost_and_changed_file_count()
    {
        var a = Guid.NewGuid();

        // A PRICED model in the compact (claude-opus-4-8 = $5/$25 per M) + changed files: 1M in + 1M out → $30; two files → 2.
        var spawn = Decision(2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded,
            outcomeJson: SpawnOutcome(new[] { a },
                new SupervisorAgentResult { AgentRunId = a, Status = "Succeeded", Model = "claude-opus-4-8", InputTokens = 1_000_000, OutputTokens = 1_000_000, ChangedFiles = new[] { "src/x.cs", "src/y.cs" } }));

        var refA = SupervisorPhaseSource.ProjectDecisions(new[] { spawn }, new Dictionary<Guid, AgentRunStatus> { [a] = AgentRunStatus.Succeeded }).Single().Agents.Single();

        refA.CostUsd.ShouldBe(30m, "the supervisor source prices the compact's model × tokens — the SAME pricing the node source uses");
        refA.FilesChanged.ShouldBe(2, "the git-truth changed-file count off the folded compact");
    }

    [Fact]
    public void A_staged_agent_with_no_folded_result_has_a_null_rollup()
    {
        var a = Guid.NewGuid();

        // Staged (in agentRunIds) but absent from agentResults — e.g. a still-running spawn fold. The rollup stays null.
        var spawn = Decision(2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Running, outcomeJson: SpawnOutcome(new[] { a }));

        var statuses = new Dictionary<Guid, AgentRunStatus> { [a] = AgentRunStatus.Running };

        var only = SupervisorPhaseSource.ProjectDecisions(new[] { spawn }, statuses).Single().Agents.Single();

        only.Status.ShouldBe(nameof(AgentRunStatus.Running));
        only.Model.ShouldBeNull();
        only.InputTokens.ShouldBeNull();
        only.OutputTokens.ShouldBeNull();
        only.CostUsd.ShouldBeNull("no compact → no priced cost");
        only.FilesChanged.ShouldBeNull("no compact → unknown file count");
    }

    [Fact]
    public void Spawn_child_agents_carry_the_live_duration_and_tool_count_extras()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var spawn = Decision(2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded, outcomeJson: SpawnOutcome(new[] { a, b }));

        var statuses = new Dictionary<Guid, AgentRunStatus> { [a] = AgentRunStatus.Succeeded, [b] = AgentRunStatus.Running };

        // The read half supplies the live extras (the figures that don't fold into the ledger).
        var extras = new Dictionary<Guid, AgentRunExtras>
        {
            [a] = new AgentRunExtras { DurationMs = 137_000, ToolCount = 16 },
            [b] = new AgentRunExtras { DurationMs = 42_000, ToolCount = 0 },
        };

        var agents = SupervisorPhaseSource.ProjectDecisions(new[] { spawn }, statuses, extras).Single().Agents;

        var refA = agents.Single(x => x.AgentRunId == a);
        refA.DurationMs.ShouldBe(137_000);
        refA.ToolCount.ShouldBe(16);

        var refB = agents.Single(x => x.AgentRunId == b);
        refB.DurationMs.ShouldBe(42_000, "a still-running agent carries its live-elapsed duration");
        refB.ToolCount.ShouldBe(0, "0 is a real 'made none' for a supervisor agent — not null");
    }

    [Fact]
    public void Child_agents_have_a_null_duration_and_tool_count_when_no_extras_are_supplied()
    {
        var a = Guid.NewGuid();
        var spawn = Decision(2, SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded, outcomeJson: SpawnOutcome(new[] { a }));
        var statuses = new Dictionary<Guid, AgentRunStatus> { [a] = AgentRunStatus.Succeeded };

        // No extras map (the default overload) — a non-supervisor agent, or before the read half supplies them: the columns stay null.
        var only = SupervisorPhaseSource.ProjectDecisions(new[] { spawn }, statuses).Single().Agents.Single();

        only.DurationMs.ShouldBeNull();
        only.ToolCount.ShouldBeNull();
    }

    /// <summary>A spawn outcome staging the given ids, with the folded <c>agentResults</c> compacts — serialized with the persisted-contract options the source's reader expects.</summary>
    private static string SpawnOutcome(IReadOnlyCollection<Guid> stagedIds, params SupervisorAgentResult[] results) =>
        JsonSerializer.Serialize(new { agentCount = stagedIds.Count, agentRunIds = stagedIds.Select(i => i.ToString()), agentResults = results }, AgentJson.Options);

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
