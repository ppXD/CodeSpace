using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// Closes the continuity gap the self-review found: the PLANNER'S OWN coding-path projection
/// (<c>recommendedWorkflowKind="coding"</c> ⇒ an <c>agent.code</c> body) driven end-to-end through REAL agents
/// as ONE continuous flow. Today the two halves are proven only in SEPARATE tests — <see cref="HeadlineFlowE2ETests"/>
/// fans out agent.code to real agents but on a HAND-BUILT graph the projector never produced, and
/// <see cref="PlannerProjectionFlowTests"/> drives the real command→planner→projector→engine path but only the
/// ANALYSIS body (its fake hardcoded "analysis", so it structurally never reached the agent.code branch). This
/// test joins them: the projector's OWN coding projection runs real agents.
///
/// <para>The flow, as ONE engine run: the real <c>PlanWorkflowFromTaskCommand</c> → real
/// <c>WorkflowPlanningService</c> → <c>LlmWorkflowPlanner</c> (structured fake emitting "coding") →
/// <c>WorkflowPlanProjector</c> emits a graph whose map body is <c>agent.code</c>. We persist that definition
/// UNCHANGED (save the one synth retarget below), seed a manual run, and run the engine: pass 1 suspends on the
/// projector's <c>flow.wait_approval</c> gate; we approve; pass 2 the <c>flow.map</c> fans out one
/// <c>agent.code</c> branch per planned subtask, each of which ACTUALLY EXECUTES through the real
/// <see cref="IAgentRunExecutor"/> → real <c>LocalProcessRunner</c> → the <see cref="SubtaskAwareFakeCli"/> agent
/// process → real ParseEvent/BuildResult → natural resume; the synthesizer then composes the real per-branch
/// results.</para>
///
/// <para><b>Fidelity (Rule 12) — HIGH tier.</b> The full command → handler → service → planner → projector path,
/// the real DefinitionValidator, the engine (CAS suspend/resume, the approval wait, the wait-for-all map barrier,
/// RehydrateMapResults), real Postgres, the AgentRunService state machine, the real AgentRunExecutor, the real
/// LocalProcessRunner SPAWNING A REAL OS PROCESS, and the harness's real ParseEvent/BuildResult are ALL real. Two
/// things are faked, at honest boundaries: (1) the LLM at the <c>IStructuredLLMClient</c> seam (the
/// <see cref="DeterministicTaskPlannerLlmClient"/> — the planner still routes through the real structured-output
/// path); (2) the CLI's INTELLIGENCE at the binary itself (the <see cref="SubtaskAwareFakeCli"/> stands in for
/// codex so no API key / network is needed), pinned by the Rule-12.5 drift detector
/// <see cref="SubtaskAwareFakeCliDriftTests"/>. The synthesizer is ALWAYS an <c>llm.complete</c> node even on the
/// coding path (the projector emits it that way), so it is the ONLY node we retarget to the LLM fake — the
/// <c>agent.code</c> body is left exactly as the projector emitted it and reaches the fake CLI via
/// <c>CodexHarness.CommandEnvVar</c>.</para>
///
/// <para>POSIX-only: the fake CLI is a <c>/bin/sh</c> script the runner spawns. Skipped on Windows (Rule 12.1),
/// where the agent-execution suites don't run anyway.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public class PlannerCodingFlowE2ETests
{
    private readonly PostgresFixture _fixture;

    public PlannerCodingFlowE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_planners_own_coding_projection_fans_out_to_real_agents_and_the_synthesizer_composes_their_results()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        // The CLI's intelligence is faked at the binary; everything the executor/runner/harness does to drive
        // it is real. One script serves every branch — the per-branch differentiation is the goal arg.
        using var cli = new SubtaskAwareFakeCli();

        var plannerFlagBefore = Environment.GetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, "1");

            var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

            // ── Plan via the command — the CODING-kind fake makes the projector emit an agent.code map body. ──
            var result = await PlanCodingFromTaskAsync(teamId, userId, "Improve the onboarding module");

            result.PlannerEnabled.ShouldBeTrue();
            result.Plan.ShouldNotBeNull();
            result.Definition.ShouldNotBeNull();
            result.Plan!.RecommendedWorkflowKind.ShouldBe(DeterministicTaskPlannerLlmClient.CodingKind);

            // ── LOAD-BEARING: the PLANNER projected the coding path. The map body is agent.code, not hand-built. ──
            AssertProjectedMapBodyIsAgentCode(result.Definition!);

            // ── Persist + run. Retarget ONLY the synth llm.complete to the fake provider; leave agent.code alone. ──
            var runnable = RetargetSynthToFake(result.Definition!);
            var workflowId = await CreateWorkflowAsync(teamId, userId, runnable);
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            var jobClient = ResolveJobClient();
            jobClient.Clear();
            jobClient.AutoExecute = true;   // the executor dispatch runs the REAL AgentRunExecutor + real runner + fake CLI

            // ── Pass 1: the run suspends on the projector's plan-review approval wait (before the map). ──
            await RunEngineAsync(runId);
            await AssertSuspendedOnApprovalAsync(runId);

            // ── Approve → pass 2: the map fans out agent.code branches; each parks + dispatches its real executor job. ──
            (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
            await RunEngineAsync(runId);

            // ── Drain the deferred chain: the real executor jobs spawn the fake CLI through the real runner, each
            //    completes for real, the completion notifier resumes, the last branch advances the map → synthesize. ──
            await jobClient.WaitForPendingAsync();

            await AssertCompletedThroughRealAgentsAsync(runId, teamId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, plannerFlagBefore);
        }
    }

    // ─── Assertions ──────────────────────────────────────────────────────────

    /// <summary>The projector's coding switch fired: the per-branch map body node is <c>agent.code</c> — proof the running graph is the planner's OWN coding projection, not a hand-built one.</summary>
    private static void AssertProjectedMapBodyIsAgentCode(WorkflowDefinition definition)
    {
        var body = definition.Nodes.SingleOrDefault(n => n.Id == "body");

        body.ShouldNotBeNull("the projector always emits a 'body' node inside the map");
        body!.TypeKey.ShouldBe("agent.code",
            customMessage: "the map body MUST be agent.code — if it's llm.complete, the planner's coding switch (ResolveBodyTypeKey) didn't fire and this test isn't exercising the real coding path");
        body.ParentId.ShouldBe("map", "the agent.code body must be parented to the map so the engine fans it out per subtask");
    }

    private async Task AssertSuspendedOnApprovalAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended,
            customMessage: "pass 1 must suspend on the projector's flow.wait_approval gate before fanning out to agents");

        var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId);
        wait.WaitKind.ShouldBe(WorkflowWaitKinds.Approval, "the projected graph pauses for human plan review before fanning out to coding agents");
    }

    private async Task AssertCompletedThroughRealAgentsAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the whole planner→approval→map→real-agents→synthesizer flow must reach Success; if not, inspect the failed WorkflowRunNode rows + the AgentRun.error for this runId");

        await AssertRealAgentRunsAsync(db, verify, runId, teamId);
        await AssertMapFannedOutAsync(db, runId);
        await AssertSynthComposedRealAgentResultsAsync(db, runId);
    }

    /// <summary>One real AgentRun per planned subtask, each Succeeded with a real folded result + a real Completed event, and each summary = the deterministic transform of the EXACT goal the projector baked ("title: instruction").</summary>
    private static async Task AssertRealAgentRunsAsync(CodeSpaceDbContext db, ILifetimeScope verify, Guid runId, Guid teamId)
    {
        var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();

        agentRuns.Count.ShouldBe(DeterministicTaskPlannerLlmClient.SubtaskTitles.Count, "one real AgentRun executed per planned subtask");
        agentRuns.ShouldAllBe(r => r.Status == AgentRunStatus.Succeeded, "every branch agent actually executed to Succeeded via the real executor + runner");
        agentRuns.ShouldAllBe(r => r.ResultJson != null, "each run persisted a real folded AgentRunResult — not a fabricated stand-in");

        var runs = verify.Resolve<IAgentRunService>();
        foreach (var agentRun in agentRuns)
        {
            var events = await runs.GetEventsAsync(agentRun.Id, teamId, 0, CancellationToken.None);
            events.ShouldContain(e => e.Kind == AgentEventKind.Completed,
                customMessage: $"agent run {agentRun.Id} should carry the real task_complete event the fake CLI emitted + the harness parsed");
        }

        // The summaries the executor folded MUST equal the fake CLI's transform of the planner's OWN baked goals.
        // The projector bakes goal = "{{item.title}}: {{item.instruction}}", so the resolved goal per subtask is
        // "<title>: <instruction>" — if the body's {{item.*}} refs hadn't resolved against the camelCase-baked
        // subtasks, the goal (and so the summary) would carry literal "{{item.*}}" and this set would not match.
        var actualSummaries = agentRuns.Select(r => ReadResultSummary(r.ResultJson!)).OrderBy(s => s).ToList();
        var expectedSummaries = ExpectedAgentSummaries().OrderBy(s => s).ToList();

        actualSummaries.ShouldBe(expectedSummaries,
            customMessage: "each agent's folded summary must be SubtaskAwareFakeCli.ExpectedSummaryFor(\"<title>: <instruction>\") for the planner's OWN subtasks — proving the projector's baked agent.code goal resolved + ran through the real CLI");
    }

    /// <summary>The map reduce's own bookkeeping: one branch per planned subtask, none failed.</summary>
    private static async Task AssertMapFannedOutAsync(CodeSpaceDbContext db, Guid runId)
    {
        var mapNode = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "");
        var mapOut = JsonDocument.Parse(mapNode.OutputsJson!).RootElement;

        mapOut.GetProperty("count").GetInt32().ShouldBe(DeterministicTaskPlannerLlmClient.SubtaskTitles.Count, "one agent.code branch fanned out per planned subtask");
        mapOut.GetProperty("failed").GetInt32().ShouldBe(0, "no branch failed");
    }

    /// <summary>The synth (llm.complete) reduced the real per-branch agent results: its userPrompt embeds {{nodes.map.outputs.results}} and the fake echoes "done: {prompt}", so the synth node's text output must carry every real agent summary the map produced.</summary>
    private static async Task AssertSynthComposedRealAgentResultsAsync(CodeSpaceDbContext db, Guid runId)
    {
        // The run-level OutputsJson is the terminal's mapping (empty here); the synth's reduction lives on the
        // synth node row (top-level, IterationKey == ""), exactly as PlannerProjectionFlowTests reads the body node.
        var synthNode = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "synth" && n.IterationKey == "");
        var synthText = JsonDocument.Parse(synthNode.OutputsJson!).RootElement.GetProperty("text").GetString();

        synthText.ShouldNotBeNull("the synthesizer llm.complete node produced no text output");

        foreach (var summary in ExpectedAgentSummaries())
            synthText!.ShouldContain(summary, Case.Sensitive,
                customMessage: $"the synthesizer's userPrompt is '...{{{{nodes.map.outputs.results}}}}', so its echoed output must carry every real agent summary — missing '{summary}' means a real agent result never reached the reduce");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>The deterministic summaries the real agents fold for the planner's OWN subtasks — the projector bakes goal = "title: instruction", and the fake CLI stamps SummaryPrefix in front.</summary>
    private static IEnumerable<string> ExpectedAgentSummaries() =>
        DeterministicTaskPlannerLlmClient.SubtaskTitles.Select(title =>
            SubtaskAwareFakeCli.ExpectedSummaryFor($"{title}: Do the work for {title.ToLowerInvariant()}"));

    private static string ReadResultSummary(string resultJson) =>
        JsonDocument.Parse(resultJson).RootElement.GetProperty("summary").GetString()!;

    private async Task<PlanWorkflowFromTaskResult> PlanCodingFromTaskAsync(Guid teamId, Guid userId, string taskText)
    {
        // Child-scope registry holds ONLY the CODING-kind planner fake → LlmWorkflowPlanner resolves it
        // deterministically and the projector switches onto the agent.code body.
        using var scope = _fixture.BeginScope(b =>
        {
            RegisterCaller(b, userId, teamId);
            b.RegisterInstance(new LLMClientRegistry(new ILLMClient[] { new DeterministicTaskPlannerLlmClient(DeterministicTaskPlannerLlmClient.CodingKind) })).As<ILLMClientRegistry>().SingleInstance();
        });

        return await scope.Resolve<IMediator>().Send(new PlanWorkflowFromTaskCommand { TaskText = taskText });
    }

    private async Task<bool> ApproveAsync(Guid runId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope(b => RegisterCaller(b, userId, teamId));
        return await scope.Resolve<IMediator>().Send(new ResumeRunCommand { RunId = runId, Approved = true, Comment = "go" });
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScope(b => RegisterCaller(b, userId, teamId));
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "planned-coding-" + Guid.NewGuid().ToString("N")[..6],
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

    private static void RegisterCaller(ContainerBuilder b, Guid userId, Guid teamId)
    {
        b.RegisterInstance(new TestCurrentUser(userId, "test", Roles.Admin)).As<CodeSpace.Core.Services.Identity.ICurrentUser>().SingleInstance();
        b.RegisterInstance(new TestCurrentTeam(teamId)).As<CodeSpace.Core.Services.Identity.ICurrentTeam>().SingleInstance();
    }

    /// <summary>
    /// Test-only adaptation: rewrite ONLY the synthesizer's <c>llm.complete</c> provider from the production
    /// default (<c>Anthropic</c>) to the registered fake's tag, so the engine resolves the fake (no API key). On the
    /// coding path the map body is <c>agent.code</c> — so this leaves the body exactly as the projector emitted it;
    /// only the always-llm.complete synth differs. The graph SHAPE — and the projector that built it — is untouched.
    /// </summary>
    private static WorkflowDefinition RetargetSynthToFake(WorkflowDefinition definition) => definition with
    {
        Nodes = definition.Nodes.Select(RetargetNode).ToList(),
    };

    private static NodeDefinition RetargetNode(NodeDefinition node)
    {
        if (node.TypeKey != "llm.complete") return node;

        var config = node.Config.Deserialize<Dictionary<string, JsonElement>>() ?? new();
        config["provider"] = JsonSerializer.SerializeToElement(DeterministicTaskPlannerLlmClient.ProviderTag);

        return node with { Config = JsonSerializer.SerializeToElement(config) };
    }
}
