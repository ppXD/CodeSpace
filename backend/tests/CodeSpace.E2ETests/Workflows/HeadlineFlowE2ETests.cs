using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// THE headline intelligent flow, driven top-to-bottom as ONE engine run — the join a verified self-review
/// found had NEVER executed end-to-end at any tier (MapAgentResumeFlowTests hand-fabricated the agent result
/// with AutoExecute=false + SimulateAgentCompletionAsync; the planner half had zero integration coverage):
///
/// <para><c>trigger.manual</c> → <c>llm.complete(responseSchema)</c> PLANNER (emits <c>json.subtasks</c>) →
/// <c>flow.map(items = {{nodes.planner.outputs.json.subtasks}})</c> whose body is a REAL <c>agent.run</c> node
/// that ACTUALLY EXECUTES (real <see cref="IAgentRunExecutor"/> → real <c>LocalProcessRunner</c> → a fake-CLI
/// agent process → real ParseEvent/BuildResult → real completion → natural resume) → a SYNTHESIZER node that
/// REDUCES <c>{{nodes.map.outputs.results}}</c> into a single combined output.</para>
///
/// <para>Unlike MapAgentResumeFlowTests, the branch agents are NOT hand-completed: the in-memory job client's
/// AutoExecute is ON, so the engine's executor dispatch runs the PRODUCTION <see cref="AgentRunExecutor"/>,
/// which spawns the per-branch <see cref="SubtaskAwareFakeCli"/> through the real runner and folds a real
/// <c>AgentRunResult</c> from the real event stream. The assertion is the deterministic composition of the
/// THREE subtasks as ACTUALLY produced by the THREE real agent executions + the reduce — so a break in ANY
/// seam (planner JSON → map binding → branch execution → reduce → synthesize) fails this test.</para>
///
/// <para><b>Fidelity (Rule 12) — HIGH tier on the spine.</b> The engine (CAS suspend/resume, the wait-for-all
/// map barrier, RehydrateMapResults), real Postgres, the AgentRunService state machine, the real
/// AgentRunExecutor, the real LocalProcessRunner SPAWNING A REAL OS PROCESS, the harness's real
/// ParseEvent/BuildResult, and the reduce are ALL real. Two things are faked, at HONEST boundaries, both
/// documented: (1) the LLM is faked at the <c>IStructuredLLMClient</c> seam
/// (<see cref="DeterministicPlannerLlmClient"/>) — the planner node still routes through the real
/// structured-output path; (2) the CLI's INTELLIGENCE is faked at the binary itself
/// (<see cref="SubtaskAwareFakeCli"/> stands in for codex/claude so no API key / network is needed) — the
/// executor still drives it through the entire production execution pipeline. The fake-CLI's event format is a
/// mirror of the documented <c>codex exec --json</c> shapes, pinned by the Rule-12.5 drift detector
/// <see cref="SubtaskAwareFakeCliDriftTests"/>.</para>
///
/// <para>POSIX-only: the fake CLI is a <c>/bin/sh</c> script the runner spawns. Skipped on Windows (Rule 12.1),
/// where the agent-execution suites don't run anyway.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public class HeadlineFlowE2ETests
{
    private readonly PostgresFixture _fixture;

    public HeadlineFlowE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Planner_fans_out_to_real_agents_and_the_synthesizer_composes_their_actual_results()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        // The CLI's intelligence is faked at the binary; everything the executor/runner/harness does to drive
        // it is real. One script serves all three branches — the per-branch differentiation is the goal arg.
        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, HeadlineFlowDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // the executor dispatch runs the REAL AgentRunExecutor + real runner + fake CLI

        // ── Pass 1: planner emits subtasks, the map fans out 3 REAL agent.run branches, each parks + dispatches
        //    its real executor job; the run suspends. ──
        await RunEngineAsync(runId);

        // ── Drain the deferred chain: 3 real executor jobs spawn the fake CLI through the real runner, each
        //    completes for real, the completion notifier resumes, the last branch advances the map → synthesize. ──
        await jobClient.WaitForPendingAsync();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the whole planner→map→real-agents→synthesizer flow must reach Success; if not, inspect the failed WorkflowRunNode rows + the AgentRun.error for this runId");

        // ── EVIDENCE the agents REALLY ran (not fabricated): one real AgentRun row per subtask, each Succeeded,
        //    with a real summary the executor's BuildResult folded from the real fake-CLI event stream. ──
        var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
        agentRuns.Count.ShouldBe(DeterministicPlannerLlmClient.Subtasks.Count, "one real AgentRun executed per planned subtask");
        agentRuns.ShouldAllBe(r => r.Status == AgentRunStatus.Succeeded, "every branch agent actually executed to Succeeded via the real executor + runner");
        agentRuns.ShouldAllBe(r => r.ResultJson != null, "each run persisted a real folded AgentRunResult — not a fabricated stand-in");

        // Real harness events were streamed off the real process pipe and persisted per run.
        var runs = verify.Resolve<IAgentRunService>();
        foreach (var agentRun in agentRuns)
        {
            var events = await runs.GetEventsAsync(agentRun.Id, teamId, 0, CancellationToken.None);
            events.ShouldContain(e => e.Kind == AgentEventKind.Completed,
                customMessage: $"agent run {agentRun.Id} should carry the real task_complete event the fake CLI emitted + the harness parsed");
        }

        // ── THE COMPOSED ASSERTION: the synthesizer's single combined output is the deterministic composition of
        //    the THREE subtasks as PRODUCED BY THE THREE REAL AGENT EXECUTIONS, ordered by element index. A break
        //    in planner JSON / map binding / branch execution / reduce / synthesize all fail HERE. ──
        var expectedCombined = string.Join(
            " | ",
            DeterministicPlannerLlmClient.Subtasks.Select(s => SubtaskAwareFakeCli.ExpectedSummaryFor($"Work on {s}")));

        var outputs = JsonDocument.Parse(run.OutputsJson).RootElement;
        outputs.GetProperty("combined").GetString().ShouldBe(expectedCombined,
            customMessage: "the synthesizer composed results[0..2].summary in element order — exactly the real fake-CLI-derived summaries the executor folded for 'Work on alpha/beta/gamma'");

        // The reduce's own bookkeeping is also real: 3 fanned out, 0 failed.
        var mapNode = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "");
        var mapOut = JsonDocument.Parse(mapNode.OutputsJson!).RootElement;
        mapOut.GetProperty("count").GetInt32().ShouldBe(DeterministicPlannerLlmClient.Subtasks.Count);
        mapOut.GetProperty("failed").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Map_fan_out_keeps_each_real_branch_agents_batched_event_log_complete_and_uncontaminated()
    {
        if (OperatingSystem.IsWindows()) return;

        // D1 under genuine engine fan-out: the planner fans out to 3 REAL agent.run branches that each stream a
        // high-volume (60-line) per-branch sequence through the production executor → real LocalProcessRunner →
        // batched BufferedEventWriter, into the shared agent_run_event table. (The in-memory job client drains the
        // branch executors SEQUENTIALLY, so this is not a true-concurrency test — that's pinned at the service tier
        // by AgentRunServiceTests' Task.WhenAll cases; THIS drives D1 through the REAL map→agent→writer path.) Each
        // branch's per-run event log MUST read back complete, in emission order, and tagged with ONLY its own goal
        // — a run-id mis-binding on a flush or a buffer leaked across the per-branch writers surfaces here as a
        // missing, reordered, or cross-contaminated line.
        using var cli = new HighVolumeSubtaskFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, HeadlineFlowDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the planner→map→3 real high-volume agents→synthesizer flow must reach Success");

        var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
        agentRuns.Count.ShouldBe(DeterministicPlannerLlmClient.Subtasks.Count, "one real AgentRun per branch");

        var runs = verify.Resolve<IAgentRunService>();
        var seenGoals = new HashSet<string>();

        foreach (var agentRun in agentRuns)
        {
            var events = await runs.GetEventsAsync(agentRun.Id, teamId, 0, CancellationToken.None);
            var messages = events.Where(e => e.Kind == AgentEventKind.AssistantMessage).Select(e => e.Text!).ToList();

            messages.Count.ShouldBe(HighVolumeSubtaskFakeCli.LineCount, $"branch {agentRun.Id} streamed ALL {HighVolumeSubtaskFakeCli.LineCount} lines through the batched writer — none lost");

            var goals = messages.Select(m => m[..m.LastIndexOf('#')]).Distinct().ToList();
            goals.Count.ShouldBe(1, $"every line in branch {agentRun.Id} carries the SAME goal tag — no sibling branch's line leaked in");

            var goal = goals[0];
            messages.ShouldBe(HighVolumeSubtaskFakeCli.ExpectedLinesFor(goal), $"branch {agentRun.Id} reads back its full ordered sequence (#001..#{HighVolumeSubtaskFakeCli.LineCount:D3})");
            events.Select(e => e.Sequence).SequenceEqual(events.Select(e => e.Sequence).OrderBy(s => s)).ShouldBeTrue("per-run sequence strictly ascending within this branch's interleaved slice of the global serial space");
            events.ShouldContain(e => e.Kind == AgentEventKind.Completed, $"branch {agentRun.Id} carries its real task_complete");

            seenGoals.Add(goal);
        }

        seenGoals.ShouldBe(DeterministicPlannerLlmClient.Subtasks.Select(s => $"Work on {s}").ToHashSet(), ignoreOrder: true,
            customMessage: "the 3 branches produced 3 DISTINCT, uncontaminated logs — one per planned subtask");
    }

    [Fact]
    public async Task Nested_map_over_real_agents_keeps_every_leaf_event_log_complete_and_uncontaminated()
    {
        if (OperatingSystem.IsWindows()) return;

        // D1 deepest catch-net (the user's "超級複雜 workflows + 節點恢復"): an OUTER flow.map over ["o0","o1"]
        // whose body is an INNER flow.map over ["{{item}}::j0","{{item}}::j1"] whose leaf is a REAL agent.run —
        // FOUR durable agent tails ("o0::j0" … "o1::j1") writing into the shared agent_run_event table while the
        // engine parks + resumes each leaf and re-walks the nested barriers. (Branch executors drain sequentially
        // via the in-memory job queue; this exercises the NESTED resume + per-leaf isolation through the real path,
        // not raw write-concurrency.) Each leaf's per-run event log MUST read back complete, ordered, and tagged
        // with ONLY its own (outer::inner) coordinate — a resume-driven duplicate-append or a run-id mis-binding
        // across the nested fan-out would surface here.
        using var cli = new HighVolumeSubtaskFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, NestedAgentMapDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["o0", "o1"] }""");

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the nested map(outer)×map(inner)×real-agent flow must reach Success through the parks + resumes");

        var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
        agentRuns.Count.ShouldBe(4, "2 outer × 2 inner = 4 real leaf agents executed");

        var runs = verify.Resolve<IAgentRunService>();
        var seenLeaves = new HashSet<string>();

        foreach (var agentRun in agentRuns)
        {
            var events = await runs.GetEventsAsync(agentRun.Id, teamId, 0, CancellationToken.None);
            var messages = events.Where(e => e.Kind == AgentEventKind.AssistantMessage).Select(e => e.Text!).ToList();

            messages.Count.ShouldBe(HighVolumeSubtaskFakeCli.LineCount, $"leaf {agentRun.Id} streamed ALL {HighVolumeSubtaskFakeCli.LineCount} lines — none lost through the nested resume re-walks");

            var leaves = messages.Select(m => m[..m.LastIndexOf('#')]).Distinct().ToList();
            leaves.Count.ShouldBe(1, $"every line in leaf {agentRun.Id} carries the SAME (outer::inner) coordinate — no sibling leaf's line leaked across the nested fan-out");

            var leaf = leaves[0];
            messages.ShouldBe(HighVolumeSubtaskFakeCli.ExpectedLinesFor(leaf), $"leaf {leaf} reads back its full ordered sequence");
            events.Select(e => e.Sequence).SequenceEqual(events.Select(e => e.Sequence).OrderBy(s => s)).ShouldBeTrue("per-leaf sequence strictly ascending within its interleaved slice of the global serial space, across the nested re-walks");
            seenLeaves.Add(leaf);
        }

        seenLeaves.ShouldBe(new HashSet<string> { "o0::j0", "o0::j1", "o1::j0", "o1::j1" }, ignoreOrder: true,
            customMessage: "all four nested leaves produced DISTINCT, complete, uncontaminated logs");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    // manual → outer map(items={{trigger.things}}) → end; outer body: mso → inner map(items=["{{item}}::j0","{{item}}::j1"])
    //   → outerTerm(emit inner results); inner body: mst → leaf(REAL agent.run, goal="{{item}}" = the leaf coordinate).
    private static WorkflowDefinition NestedAgentMapDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "outer", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "mso", TypeKey = "flow.map_start", ParentId = "outer", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "inner", TypeKey = "flow.map", ParentId = "outer", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": [ "{{item}}::j0", "{{item}}::j1" ] }""") },
            new() { Id = "mst", TypeKey = "flow.map_start", ParentId = "inner", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "leaf", TypeKey = "agent.run", ParentId = "inner",
                    Config = WorkflowsTestSeed.Json("""{"goal":"{{item}}","harness":"codex-cli","model":"gpt-5.3-codex","runnerKind":"local","readOnly":true}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "outerTerm", TypeKey = JsonEmitNode.Key, ParentId = "outer", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "results": "{{nodes.inner.outputs.results}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.outer.outputs.count}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "outer" },
            new() { From = "outer", To = "end" },
            new() { From = "mso", To = "inner" },
            new() { From = "inner", To = "outerTerm" },
            new() { From = "mst", To = "leaf" },
        },
    };

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "headline-e2e-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    // manual → planner(llm.complete, responseSchema → json.subtasks) → map(items = {{planner.subtasks}};
    //   body: ms → agent[REAL agent.run running the fake codex CLI, read-only, no repo]) → synth(combine results).
    // The synthesizer REDUCES the per-branch summaries into ONE string via multi-placeholder composition — the
    // production VariableResolver path for {{nodes.map.outputs.results[i].summary}} (array indexing into the reduce).
    private static WorkflowDefinition HeadlineFlowDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },

            // PLANNER: a responseSchema forces structured output, surfaced on json; routed through the
            // DeterministicPlannerLlmClient (provider tag) at the IStructuredLLMClient seam.
            new() { Id = "planner", TypeKey = "llm.complete",
                    Config = WorkflowsTestSeed.Json($$"""
                        {
                          "provider": "{{DeterministicPlannerLlmClient.ProviderTag}}",
                          "responseSchema": { "type": "object", "properties": { "subtasks": { "type": "array", "items": { "type": "string" } } }, "required": ["subtasks"] }
                        }
                        """),
                    Inputs = WorkflowsTestSeed.Json("""{ "userPrompt": "Break the work into subtasks." }""") },

            // MAP: fan out over the planner's typed subtasks array.
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": "{{nodes.planner.outputs.json.subtasks}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },

            // BRANCH AGENT: a REAL agent.run whose goal carries this branch's {{item}} — the fake CLI derives its
            // summary from that goal, so each branch's results[i] is its OWN element's real output.
            new() { Id = "agent", TypeKey = "agent.run", ParentId = "map",
                    Config = WorkflowsTestSeed.Json("""{"goal":"Work on {{item}}","harness":"codex-cli","model":"gpt-5.3-codex","runnerKind":"local","readOnly":true}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },

            // SYNTHESIZER: reduce results[] into ONE combined string (multi-placeholder composition, element-ordered).
            new() { Id = "synth", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""
                        { "combined": "{{nodes.map.outputs.results[0].summary}} | {{nodes.map.outputs.results[1].summary}} | {{nodes.map.outputs.results[2].summary}}" }
                        """) },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "planner" },
            new() { From = "planner", To = "map" },
            new() { From = "map", To = "synth" },
            new() { From = "ms", To = "agent" },
        },
    };
}
