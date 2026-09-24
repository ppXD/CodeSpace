using System.Text.Json;
using Autofac;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Continue on a run that ended while steps were PARKED. The terminal teardown closes every pending wait without an
/// answer, so Continue must re-run each parked step the way a first run would — re-parking a FRESH wait — and never
/// hand it the closed wait's stored REQUEST (its own agent task, its own approval prompt) as if that were the answer.
/// Before the fix a capped <c>agent.run</c> read its own task as a result and failed "cumulative spend is missing"
/// (a verdict every later Continue replayed, so the run could never be continued), and an approval read
/// <c>approved=false</c> with no approver and took the rejection path. One test per replay reader (the top-level walk,
/// a map branch, a loop pass) through the operator stop's teardown, and one through the engine's own teardown when a
/// run lands Failure beside a parked step — plus one where the stop's teardown lands only after the Continue has
/// re-parked every step, and must leave the revived run's fresh waits, agent and child alone, and one where it lands
/// before the continued walk starts, and the agent it kills must not answer the continued step.
///
/// <para>Fidelity (Rule 12) 🟢 high for everything under test: the REAL <c>WorkflowService</c> cancel (terminal flip plus
/// its post-commit teardown) and the REAL engine terminal cleanup, which are what close the waits; the REAL Continue for
/// both a stopped and a failed run; the REAL engine replay over real Postgres; the REAL <c>agent.run</c> /
/// <c>flow.wait_approval</c> / <c>flow.map</c> / <c>flow.loop</c> nodes. Only the binary-less harness never runs — the
/// agent tests take the job client record-only through <c>ManualExecution()</c>, so the executor dispatch is recorded, never run.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ContinueParkedRunFlowTests
{
    private readonly PostgresFixture _fixture;

    public ContinueParkedRunFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Continuing_a_run_stopped_while_parked_re_parks_each_step_instead_of_replaying_its_request()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, CappedAgentBesideApprovalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var manual = ResolveJobClient().ManualExecution();   // record the executor dispatch; the binary-less harness must not run

        // ── Both steps park in one wave: the capped agent on its AgentRun wait, the approval on its Approval wait. ──
        await RunEngineAsync(runId);

        var parked = await WaitsAsync(runId);
        parked.Count(w => w.Status == WorkflowWaitStatuses.Pending).ShouldBe(2, "precondition: both steps parked");
        var firstAgentWait = parked.Single(w => w.WaitKind == WorkflowWaitKinds.AgentRun);
        var firstApprovalWait = parked.Single(w => w.WaitKind == WorkflowWaitKinds.Approval);

        // ── The operator stops the parked run through the real cancel path, then continues it. ──
        await CancelAsync(runId, teamId);

        var closed = await WaitsAsync(runId);
        closed.ShouldAllBe(w => w.Status == WorkflowWaitStatuses.Discarded, "the stop's teardown closed every pending wait unanswered — Discarded, never the Resolved a real answer writes");
        closed.Single(w => w.WaitKind == WorkflowWaitKinds.Approval).PayloadJson!.ShouldContain("ship it?", customMessage: "the closed wait keeps the request it was parked on");

        await ContinueAsync(runId, teamId);
        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, customMessage: $"both steps re-parked on fresh waits — a Failure here means a step consumed its own request as the answer. Records: {await StepVerdictsAsync(db, runId)}");

            (await FailureMessagesAsync(db, runId)).ShouldNotContain(m => m.Contains("cumulative spend is missing"), "the capped agent never read its own task as a priced result");

            var agentTerminals = await StepTerminalRecordsAsync(db, runId, "agent", "");
            agentTerminals.ShouldBeEmpty("the agent re-staged instead of settling on a replayed request");

            var approvalTerminals = await StepTerminalRecordsAsync(db, runId, "approval", "");
            approvalTerminals.ShouldBeEmpty("the approval re-parked instead of completing with approved=false and no approver");

            var pending = await db.WorkflowRunWait.AsNoTracking().Where(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending).ToListAsync();
            pending.Count.ShouldBe(2, "each step parked a fresh wait, exactly as a first run would");

            var agentWait = pending.Single(w => w.WaitKind == WorkflowWaitKinds.AgentRun);
            agentWait.Id.ShouldNotBe(firstAgentWait.Id, "a fresh AgentRun wait, not the closed one re-read");
            agentWait.Token.ShouldNotBe(firstAgentWait.Token, "the agent re-staged a NEW agent run — the stopped one stays cancelled");

            var approvalWait = pending.Single(w => w.WaitKind == WorkflowWaitKinds.Approval);
            approvalWait.Id.ShouldNotBe(firstApprovalWait.Id, "a fresh Approval wait, not the closed one re-read");

            (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == Guid.Parse(firstAgentWait.Token))).Status
                .ShouldBe(AgentRunStatus.Cancelled, "the stop's kill-wave cancelled the first agent; Continue never revived it");
            (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == Guid.Parse(agentWait.Token))).Status
                .ShouldBe(AgentRunStatus.Queued, "the re-staged agent is queued for its own execution");

            IReadOnlyList<string> approvalHistory = await db.WorkflowRunRecord.AsNoTracking()
                .Where(r => r.RunId == runId && ((r.NodeId == "approval" && r.RecordType == WorkflowRunRecordTypes.NodeSuspended) || r.RecordType == WorkflowRunRecordTypes.RunCancelled))
                .OrderBy(r => r.Sequence)
                .Select(r => r.RecordType)
                .ToListAsync();
            approvalHistory.ShouldBe(new[] { WorkflowRunRecordTypes.NodeSuspended, WorkflowRunRecordTypes.RunCancelled, WorkflowRunRecordTypes.NodeSuspended },
                "the ledger keeps that the first wait existed and the stop closed it: park, cancel, re-park");
        }

        // ── The fresh approval wait is live: a real answer resolves it and the step completes with THAT answer. ──
        (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue("the re-parked approval accepts a genuine decision");
        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            var approval = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "approval" && n.IterationKey == "");
            approval.Status.ShouldBe(NodeStatus.Success);

            var outputs = JsonDocument.Parse(approval.OutputsJson).RootElement;
            outputs.GetProperty("approved").GetBoolean().ShouldBeTrue("the step completed with the operator's real answer");
            outputs.GetProperty("by").GetString().ShouldBe(userId.ToString(), "the answer names its approver — never the blank one a replayed request carried");
        }
    }

    [Fact]
    public async Task Continuing_a_map_stopped_while_its_branch_agent_was_parked_re_stages_that_branch_agent()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, MapOverCappedAgentDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a"] }""");

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);

        var firstBranchWait = (await WaitsAsync(runId)).Single(w => w.IterationKey == "map#0" && w.WaitKind == WorkflowWaitKinds.AgentRun);
        firstBranchWait.Status.ShouldBe(WorkflowWaitStatuses.Pending, "precondition: the branch agent parked");

        await CancelAsync(runId, teamId);

        (await WaitsAsync(runId)).Single(w => w.Id == firstBranchWait.Id).Status.ShouldBe(WorkflowWaitStatuses.Discarded, "the stop closed the branch wait unanswered");

        await ContinueAsync(runId, teamId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, customMessage: $"the branch agent re-parked — a Failure means the branch read its own task as a result. Records: {await StepVerdictsAsync(db, runId)}");

        (await FailureMessagesAsync(db, runId)).ShouldNotContain(m => m.Contains("cumulative spend is missing"), "the branch agent never read its own task as a priced result");
        (await StepTerminalRecordsAsync(db, runId, "agent", "map#0")).ShouldBeEmpty("the branch agent re-staged instead of settling on a replayed request");

        var branchWait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.IterationKey == "map#0" && w.Status == WorkflowWaitStatuses.Pending);
        branchWait.WaitKind.ShouldBe(WorkflowWaitKinds.AgentRun);
        branchWait.Token.ShouldNotBe(firstBranchWait.Token, "the branch staged a NEW agent run on a fresh wait");
    }

    [Fact]
    public async Task Continuing_a_loop_stopped_while_its_body_approval_was_parked_re_parks_that_pass()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, LoopOverApprovalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        var firstPassWait = (await WaitsAsync(runId)).Single(w => w.IterationKey == "loop#0");
        firstPassWait.Status.ShouldBe(WorkflowWaitStatuses.Pending, "precondition: the pass parked on its approval");

        await CancelAsync(runId, teamId);

        (await WaitsAsync(runId)).Single(w => w.Id == firstPassWait.Id).Status.ShouldBe(WorkflowWaitStatuses.Discarded, "the stop closed the pass's wait unanswered");

        await ContinueAsync(runId, teamId);
        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, customMessage: $"the pass re-parked on its approval — Success means the gate read its own prompt as a rejection and the loop ran on. Records: {await StepVerdictsAsync(db, runId)}");

            (await StepTerminalRecordsAsync(db, runId, "gate", "loop#0")).ShouldBeEmpty("the gate re-parked instead of completing with approved=false and no approver");

            var passWait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.IterationKey == "loop#0" && w.Status == WorkflowWaitStatuses.Pending);
            passWait.WaitKind.ShouldBe(WorkflowWaitKinds.Approval);
            passWait.Id.ShouldNotBe(firstPassWait.Id, "the pass parked a fresh wait");
        }

        (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue("the re-parked pass accepts a genuine decision");
        await RunEngineAsync(runId);

        using var final = _fixture.BeginScope();
        var fdb = final.Resolve<CodeSpaceDbContext>();

        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success, "the answered pass let the loop finish");

        var gate = await fdb.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "gate" && n.IterationKey == "loop#0");
        JsonDocument.Parse(gate.OutputsJson).RootElement.GetProperty("approved").GetBoolean().ShouldBeTrue("the pass completed with the operator's real answer");
    }

    [Fact]
    public async Task Continuing_a_run_that_failed_beside_a_parked_approval_re_parks_that_approval()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ApprovalBesideFailureDefinition(flakyKey: Guid.NewGuid().ToString("N")));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // One wave: the approval parks while its sibling fails with no error edge, so the ENGINE lands the run Failure and
        // its own terminal cleanup — not the operator stop's — closes the approval's wait.
        await RunEngineAsync(runId);

        var firstWait = (await WaitsAsync(runId)).Single(w => w.WaitKind == WorkflowWaitKinds.Approval);

        using (var verify = _fixture.BeginScope())
            (await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Failure, "precondition: the sibling's failure landed the run");

        firstWait.Status.ShouldBe(WorkflowWaitStatuses.Discarded, "the engine's terminal cleanup closed the parked approval unanswered");

        await ContinueAsync(runId, teamId);   // Failure → re-run the failed sibling in place
        await RunEngineAsync(runId);

        using var final = _fixture.BeginScope();
        var db = final.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, customMessage: $"the approval re-parked — Success means it read its own prompt as a rejection and the run ran on. Records: {await StepVerdictsAsync(db, runId)}");

        (await StepTerminalRecordsAsync(db, runId, "approval", "")).ShouldBeEmpty("the approval re-parked instead of completing with approved=false and no approver");

        var approvalWait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending);
        approvalWait.WaitKind.ShouldBe(WorkflowWaitKinds.Approval);
        approvalWait.Id.ShouldNotBe(firstWait.Id, "the approval parked a fresh wait");
    }

    [Fact]
    public async Task A_stop_teardown_landing_after_a_continue_ends_the_stopped_attempts_work_and_leaves_the_revived_runs_alone()
    {
        // Stop then an immediate Continue. The stop's teardown runs after its commit, so it can still be in flight — here
        // it is held and lands only once the continued walk has re-parked every step on a fresh wait, staged a fresh
        // agent and a fresh child run. The revived run's work is not its to end; the stopped attempt's agent and child
        // still are — they were live when the stop committed, and nothing else would stop them under a live parent.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var childWorkflowId = await CreateWorkflowAsync(teamId, userId, ChildDefinition());
        var workflowId = await CreateWorkflowAsync(teamId, userId, AgentApprovalAndChildDefinition(childWorkflowId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var manual = ResolveJobClient().ManualExecution();   // record the executor + child dispatches; nothing runs

        await RunEngineAsync(runId);
        var stoppedParks = (await WaitsAsync(runId)).Where(w => w.Status == WorkflowWaitStatuses.Pending).ToList();
        stoppedParks.Count.ShouldBe(3, "precondition: all three steps parked");
        var stoppedAgentId = Guid.Parse(stoppedParks.Single(w => w.WaitKind == WorkflowWaitKinds.AgentRun).Token);
        var stoppedChildId = Guid.Parse(stoppedParks.Single(w => w.WaitKind == WorkflowWaitKinds.Subworkflow).Token);

        using var stop = await WorkflowsTestSeed.StopWithTeardownHeldAsync(_fixture, runId, teamId);
        await ContinueAsync(runId, teamId);
        await RunEngineAsync(runId);

        var fresh = (await WaitsAsync(runId)).Where(w => w.Status == WorkflowWaitStatuses.Pending).ToList();
        fresh.Count.ShouldBe(3, "precondition: the continued walk re-parked every step on a fresh wait");

        await stop.Resolve<IPostCommitActions>().RunAllAsync(CancellationToken.None);   // the stop's teardown lands only now

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var freshIds = fresh.Select(w => w.Id).ToList();
        (await db.WorkflowRunWait.AsNoTracking().Where(w => freshIds.Contains(w.Id)).Select(w => w.Status).ToListAsync())
            .ShouldAllBe(status => status == WorkflowWaitStatuses.Pending, "the late teardown left every fresh wait of the revived run open");

        var agentId = Guid.Parse(fresh.Single(w => w.WaitKind == WorkflowWaitKinds.AgentRun).Token);
        (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentId)).Status
            .ShouldBe(AgentRunStatus.Queued, "the stopped generation's kill-wave did not cancel the revived run's agent");

        var childRunId = Guid.Parse(fresh.Single(w => w.WaitKind == WorkflowWaitKinds.Subworkflow).Token);
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == childRunId)).Status
            .ShouldNotBe(WorkflowRunStatus.Cancelled, "nor did it cancel the revived run's child run");

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, "the revived run is still parked on its fresh waits");

        (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == stoppedAgentId)).Status
            .ShouldBe(AgentRunStatus.Cancelled, "the stopped attempt's agent was live when the stop committed — the late teardown still ends it");
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == stoppedChildId)).Status
            .ShouldBe(WorkflowRunStatus.Cancelled, "and cancels the stopped attempt's staged child run");
    }

    [Fact]
    public async Task A_stopped_attempts_agent_killed_after_a_continue_never_answers_the_continued_step()
    {
        // Stop, then a Continue before the stop's teardown ran. The teardown still kills the stopped attempt's agent, and
        // the kill ends the agent Cancelled — a result its completion hands back through the step's wait. That wait is
        // still open here: the continued walk has not re-parked the step yet, and the run-wide close of the stop's waits
        // stands down once a Continue has followed it. Answered by the kill, it is the answer the continued walk replays,
        // and the step fails "Agent run did not succeed" the moment the operator continues it.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, CappedAgentBesideApprovalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var manual = ResolveJobClient().ManualExecution();

        await RunEngineAsync(runId);
        var stoppedAgentWait = (await WaitsAsync(runId)).Single(w => w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending);
        var stoppedAgentId = Guid.Parse(stoppedAgentWait.Token);

        using (var stop = await WorkflowsTestSeed.StopWithTeardownHeldAsync(_fixture, runId, teamId))
        {
            await ContinueAsync(runId, teamId);
            await stop.Resolve<IPostCommitActions>().RunAllAsync(CancellationToken.None);   // the teardown lands before the continued walk starts
        }

        await NotifyAgentEndedAsync(stoppedAgentId);   // the killed agent's completion, as its executor or the agent reconciler delivers it
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == stoppedAgentId)).Status.ShouldBe(AgentRunStatus.Cancelled, "precondition: the late teardown killed the stopped attempt's agent");

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, customMessage: $"the continued step parked again — a Failure means it took the killed agent's end as its answer. Records: {await StepVerdictsAsync(db, runId)}");

        (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending)).Token
            .ShouldNotBe(stoppedAgentWait.Token, "the continued step staged a fresh agent");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private async Task NotifyAgentEndedAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentRunCompletionNotifier>().NotifyCompletedAsync(agentRunId, CancellationToken.None);
    }

    private async Task CancelAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None))!.Cancelled.ShouldBeTrue("the parked run was stopped");
    }

    private async Task ContinueAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IWorkflowService>().ContinueRunAsync(runId, teamId, CancellationToken.None)).ShouldBeTrue("a run that ended with steps parked continues in place");
    }

    private async Task<bool> ApproveAsync(Guid runId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new ResumeRunCommand { RunId = runId, Approved = true, Comment = "ship it" });
    }

    private async Task<IReadOnlyList<Core.Persistence.Entities.WorkflowRunWait>> WaitsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(w => w.RunId == runId).ToListAsync();
    }

    /// <summary>The node.completed / node.failed / attempt.failed records one step wrote under one iteration key — the verdicts a replayed request produces.</summary>
    private static async Task<IReadOnlyList<string>> StepTerminalRecordsAsync(CodeSpaceDbContext db, Guid runId, string nodeId, string iterationKey) =>
        await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.NodeId == nodeId && r.IterationKey == iterationKey && TerminalRecordTypes.Contains(r.RecordType))
            .Select(r => r.RecordType)
            .ToListAsync();

    private static async Task<IReadOnlyList<string>> FailureMessagesAsync(CodeSpaceDbContext db, Guid runId) =>
        await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && (r.RecordType == WorkflowRunRecordTypes.NodeFailed || r.RecordType == WorkflowRunRecordTypes.AttemptFailed || r.RecordType == WorkflowRunRecordTypes.RunFailed))
            .Select(r => r.PayloadJson)
            .ToListAsync();

    /// <summary>Every step verdict on the run, for a failure message that names what the replay actually produced.</summary>
    private static async Task<string> StepVerdictsAsync(CodeSpaceDbContext db, Guid runId)
    {
        var rows = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && (TerminalRecordTypes.Contains(r.RecordType) || r.RecordType == WorkflowRunRecordTypes.RunFailed))
            .OrderBy(r => r.Sequence)
            .Select(r => new { r.RecordType, r.NodeId, r.IterationKey, r.PayloadJson })
            .ToListAsync();

        return string.Join(" | ", rows.Select(r => $"{r.RecordType} {r.NodeId}[{r.IterationKey}] {r.PayloadJson}"));
    }

    private static readonly string[] TerminalRecordTypes = { WorkflowRunRecordTypes.NodeCompleted, WorkflowRunRecordTypes.NodeFailed, WorkflowRunRecordTypes.AttemptFailed };

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "continue-parked-" + Guid.NewGuid().ToString("N")[..6],
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

    private const string CappedAgentConfig = """{"goal":"Fix the failing billing tests","harness":"codex-cli","model":"gpt-5.3-codex","runnerKind":"local","readOnly":true,"maxCostUsd":1}""";

    // manual → { agent (agent.run, $1 cap) , approval (flow.wait_approval) } → terminal. Both steps park in the same wave.
    private static WorkflowDefinition CappedAgentBesideApprovalDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agent", TypeKey = "agent.run", Config = WorkflowsTestSeed.Json(CappedAgentConfig), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "approval", TypeKey = "flow.wait_approval", Config = WorkflowsTestSeed.Json("""{ "prompt": "ship it?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "agent" },
            new() { From = "start", To = "approval" },
            new() { From = "agent", To = "end" },
            new() { From = "approval", To = "end" },
        },
    };

    // manual → { agent (agent.run, $1 cap) , approval (flow.wait_approval) , sub (flow.subworkflow) } → terminal. All three park in one wave:
    // an AgentRun wait with a staged agent, an Approval wait, and a Subworkflow wait with a staged child run.
    private static WorkflowDefinition AgentApprovalAndChildDefinition(Guid childWorkflowId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agent", TypeKey = "agent.run", Config = WorkflowsTestSeed.Json(CappedAgentConfig), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "approval", TypeKey = "flow.wait_approval", Config = WorkflowsTestSeed.Json("""{ "prompt": "ship it?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sub", TypeKey = "flow.subworkflow", Config = WorkflowsTestSeed.Json($$"""{"workflowId":"{{childWorkflowId}}"}"""), Inputs = WorkflowsTestSeed.Json("""{"inputs":{"x":"v"}}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "agent" },
            new() { From = "start", To = "approval" },
            new() { From = "start", To = "sub" },
            new() { From = "agent", To = "end" },
            new() { From = "approval", To = "end" },
            new() { From = "sub", To = "end" },
        },
    };

    // manual → terminal: the child a flow.subworkflow step stages.
    private static WorkflowDefinition ChildDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } },
    };

    // manual → { boom (fails once, no error edge) , approval (flow.wait_approval) } → terminal. Both run in one wave, which settles in
    // drain (edge) order: boom's edge comes first, so its failure — not the approval's park — lands the run, with the wait already staged.
    private static WorkflowDefinition ApprovalBesideFailureDefinition(string flakyKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "approval", TypeKey = "flow.wait_approval", Config = WorkflowsTestSeed.Json("""{ "prompt": "ship it?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "boom", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{ "key": "{{flakyKey}}", "failTimes": 1 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "boom" },
            new() { From = "start", To = "approval" },
            new() { From = "approval", To = "end" },
            new() { From = "boom", To = "end" },
        },
    };

    // manual → map(items={{trigger.things}}; body: ms → agent[agent.run, $1 cap]) → terminal. Each branch parks an AgentRun wait.
    private static WorkflowDefinition MapOverCappedAgentDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agent", TypeKey = "agent.run", ParentId = "map", Config = WorkflowsTestSeed.Json(CappedAgentConfig), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "agent" },
        },
    };

    // manual → loop(maxIterations 1; body: ls → gate[flow.wait_approval]) → terminal. The single pass parks on its approval.
    private static WorkflowDefinition LoopOverApprovalDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "loop", TypeKey = "flow.loop", Config = WorkflowsTestSeed.Json("""{ "maxIterations": 1 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "gate", TypeKey = "flow.wait_approval", ParentId = "loop", Config = WorkflowsTestSeed.Json("""{ "prompt": "go?" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "loop" },
            new() { From = "loop", To = "end" },
            new() { From = "ls", To = "gate" },
        },
    };
}
