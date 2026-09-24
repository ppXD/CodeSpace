using System.Data.Common;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Decisions;

/// <summary>
/// 🟢 Integration (high fidelity, Rule 12): a decision raised by a run that then stopped. An agent-grain
/// <c>decision.request</c> parks as an AwaitingApproval ledger row, and neither a cancel nor the reconciler's abandon
/// used to touch it, so the stopped run's question stayed in the team queue and could still be "answered" — for a
/// human-required decision, forever, since the reaper only ever defers those. The stop now closes it Expired in the
/// same single-winner CAS discipline the answer uses, so an answer racing the stop is either applied first or refused,
/// never accepted into a void. Drives the REAL <see cref="IAgentRunService.CancelRunningAsync"/>, the REAL reconciler
/// abandon, the REAL <see cref="DecisionQueueService"/> / <see cref="DecisionAnswerService"/> and the REAL workflow
/// stop, over real Postgres.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class StoppedRunDecisionFlowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _fixture;

    public StoppedRunDecisionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_cancelled_agent_runs_pending_decision_leaves_the_queue_and_refuses_an_answer()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var agentId = await SeedRunningAgentAsync(teamId, livenessAgo: TimeSpan.Zero);
        var decisionId = await SeedAgentDecisionAsync(teamId, agentId);

        (await CancelAgentAsync(agentId)).ShouldBeTrue("precondition: the running agent was cancelled");

        (await ListPendingAsync(teamId)).ShouldNotContain(d => d.Id == decisionId, "a stopped run's question is no longer anyone's to answer");

        var answer = await AnswerAsync(decisionId, teamId, userId);
        answer.Outcome.ShouldBe(DecisionAnswerOutcome.AlreadyResolved, "the answer is refused, not accepted into a run that is gone");
        answer.Message.ShouldBe(StoppedRunDecisions.ExpiredError, "and says why");

        var row = await LedgerRowAsync(decisionId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Expired);
        row.Error.ShouldBe(StoppedRunDecisions.ExpiredError);
    }

    [Fact]
    public async Task An_abandoned_agent_runs_pending_decision_leaves_the_queue()
    {
        var (teamId, _) = await SeedTeamAsync();
        var agentId = await SeedRunningAgentAsync(teamId, livenessAgo: TimeSpan.FromMinutes(20));   // worker gone: stale heartbeat, lapsed lease
        var decisionId = await SeedAgentDecisionAsync(teamId, agentId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentId)).Status
                .ShouldBe(AgentRunStatus.Failed, "precondition: the reconciler abandoned the run");

        (await ListPendingAsync(teamId)).ShouldNotContain(d => d.Id == decisionId, "an abandoned run's question leaves the queue with it");
        (await LedgerRowAsync(decisionId)).Status.ShouldBe(ToolCallLedgerStatus.Expired);
    }

    [Fact]
    public async Task An_answer_that_lands_before_the_cancel_is_kept()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var agentId = await SeedRunningAgentAsync(teamId, livenessAgo: TimeSpan.Zero);
        var decisionId = await SeedAgentDecisionAsync(teamId, agentId);

        (await AnswerAsync(decisionId, teamId, userId)).Outcome.ShouldBe(DecisionAnswerOutcome.Answered);
        (await CancelAgentAsync(agentId)).ShouldBeTrue();

        var row = await LedgerRowAsync(decisionId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the stop closes only an UNANSWERED decision — the answer that won stays the decision's answer");
        row.ResultJson.ShouldNotBeNull().ShouldContain("\"a\"", customMessage: "with the option the person chose");
    }

    [Fact]
    public async Task An_answer_racing_the_cancel_is_either_applied_or_refused_never_accepted_into_a_void()
    {
        var (teamId, userId) = await SeedTeamAsync();

        for (var i = 0; i < 10; i++)
        {
            var agentId = await SeedRunningAgentAsync(teamId, livenessAgo: TimeSpan.Zero);
            var decisionId = await SeedAgentDecisionAsync(teamId, agentId);

            var answer = AnswerAsync(decisionId, teamId, userId);
            var cancel = CancelAgentAsync(agentId);
            await Task.WhenAll(answer, cancel);

            var row = await LedgerRowAsync(decisionId);

            if ((await answer).Outcome == DecisionAnswerOutcome.Answered)
                row.Status.ShouldBe(ToolCallLedgerStatus.Succeeded, $"round {i}: the answerer was told Answered, so the decision must carry that answer");
            else
                row.Status.ShouldBe(ToolCallLedgerStatus.Expired, $"round {i}: a refused answer means the stop closed the decision first");
        }
    }

    [Fact]
    public async Task An_answer_whose_write_loses_to_the_stop_is_refused_with_the_reason()
    {
        // The exact race: the answer read the decision still open, then the stop's expiry landed before the answer's own
        // write. The answer's CAS moves nothing — and says why, rather than a bare "already resolved".
        var (teamId, userId) = await SeedTeamAsync();
        var agentId = await SeedRunningAgentAsync(teamId, livenessAgo: TimeSpan.Zero);
        var decisionId = await SeedAgentDecisionAsync(teamId, agentId);

        var stopBeforeWrite = new BeforeLedgerWriteFault(() => CancelAgentAsync(agentId));
        var answer = await AnswerThroughAsync(stopBeforeWrite, decisionId, teamId, userId);

        stopBeforeWrite.Fired.ShouldBeTrue("precondition: the stop landed between the answer's read and its write");
        answer.Outcome.ShouldBe(DecisionAnswerOutcome.AlreadyResolved, "the answer lost the row to the stop");
        answer.Message.ShouldBe(StoppedRunDecisions.ExpiredError, "and is told the agent run was stopped");
        (await LedgerRowAsync(decisionId)).Status.ShouldBe(ToolCallLedgerStatus.Expired);
    }

    [Fact]
    public async Task A_stopped_runs_node_decision_refuses_an_answer_saying_it_reopens_on_continue()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var (runId, waitId) = await SeedRunWithNodeDecisionAsync(teamId);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None))!.Cancelled.ShouldBeTrue();

        var answer = await AnswerAsync(waitId, teamId, userId);

        answer.Outcome.ShouldBe(DecisionAnswerOutcome.AlreadyResolved);
        answer.Message.ShouldBe(WorkflowService.EndedRunQuestionMessage, "the Room says the run was stopped and that Continue re-opens the question, not that someone already answered it");
    }

    // ─── Drive the real services ──────────────────────────────────────────────────

    private async Task<bool> CancelAgentAsync(Guid agentId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentRunService>().CancelRunningAsync(agentId, "stopped by the operator", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None);
    }

    private async Task<AnswerDecisionResult> AnswerAsync(Guid decisionId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IDecisionAnswerService>().AnswerAsync(decisionId, new[] { "a" }, null, teamId, userId, CancellationToken.None);
    }

    /// <summary>Answer through a scope whose database commands pass <paramref name="fault"/> — the seam that lands the stop at an exact point of the answer.</summary>
    private async Task<AnswerDecisionResult> AnswerThroughAsync(IInterceptor fault, Guid decisionId, Guid teamId, Guid userId)
    {
        DbContextOptions<CodeSpaceDbContext> production;
        using (var probe = _fixture.BeginScope())
            production = probe.Resolve<DbContextOptions<CodeSpaceDbContext>>();

        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>(production).AddInterceptors(fault).Options;

        using var scope = _fixture.BeginScope(b => b.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance());
        return await scope.Resolve<IDecisionAnswerService>().AnswerAsync(decisionId, new[] { "a" }, null, teamId, userId, CancellationToken.None);
    }

    /// <summary>Runs <c>beforeWrite</c> once, just before the scope's first write to the ledger — the answer's own CAS — executes.</summary>
    private sealed class BeforeLedgerWriteFault : DbCommandInterceptor
    {
        private readonly Func<Task> _beforeWrite;
        private int _fired;

        public BeforeLedgerWriteFault(Func<Task> beforeWrite) { _beforeWrite = beforeWrite; }

        public bool Fired => _fired == 1;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE tool_call_ledger", StringComparison.Ordinal) && Interlocked.Exchange(ref _fired, 1) == 0)
                await _beforeWrite().ConfigureAwait(false);

            return result;
        }
    }

    private async Task<IReadOnlyList<PendingDecision>> ListPendingAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IDecisionQueueService>().ListPendingAsync(teamId, CancellationToken.None);
    }

    private async Task<ToolCallLedger> LedgerRowAsync(Guid ledgerId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
    }

    // ─── Seeding ──────────────────────────────────────────────────────────────────

    /// <summary>A Running agent run; <paramref name="livenessAgo"/> past the window makes it one the reconciler abandons (the heartbeat and lease lapse together).</summary>
    private async Task<Guid> SeedRunningAgentAsync(Guid teamId, TimeSpan livenessAgo)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var agentId = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow - livenessAgo;

        db.AgentRun.Add(new AgentRun { Id = agentId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, StartedAt = stamp, HeartbeatAt = stamp, LeaseExpiresAt = stamp + AgentRunLiveness.Window });

        await db.SaveChangesAsync();
        return agentId;
    }

    /// <summary>A parked, human-required agent-grain decision the agent raised mid-run — exactly the row the reaper only ever defers.</summary>
    private async Task<Guid> SeedAgentDecisionAsync(Guid teamId, Guid agentId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var ledgerId = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddHours(1);

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = ledgerId, TeamId = teamId, AgentRunId = agentId, ToolKind = DecisionToolKinds.DecisionRequest,
            IdempotencyKey = $"decision.request:{ledgerId:N}", InputHash = new string('0', 64),
            Status = ToolCallLedgerStatus.AwaitingApproval, ApprovalDeadlineAt = deadline,
            DecisionEnvelopeJson = JsonSerializer.Serialize(Envelope(deadline, DecisionResumeBackends.ToolLedger, agentId, workflowRunId: null), Json),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return ledgerId;
    }

    /// <summary>A Suspended run parked on a flow.decision wait (its envelope stashed on the wait, as the node writes it).</summary>
    private async Task<(Guid RunId, Guid WaitId)> SeedRunWithNodeDecisionAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var waitId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Manual, ActorType = "user", ActorId = SystemUsers.SeederId,
            NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Manual,
            Status = WorkflowRunStatus.Suspended, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();   // the wait's run FK is not an EF navigation, so the run commits first

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = waitId, RunId = runId, NodeId = "decide", IterationKey = string.Empty, WaitKind = WorkflowWaitKinds.Decision,
            Token = Guid.NewGuid().ToString("N"), Status = WorkflowWaitStatuses.Pending, CreatedAt = now,
            PayloadJson = JsonSerializer.Serialize(Envelope(now.AddHours(1), DecisionResumeBackends.WorkflowWait, agentRunId: null, runId), Json),
        });
        await db.SaveChangesAsync();

        return (runId, waitId);
    }

    private static DecisionRequest Envelope(DateTimeOffset deadline, string grain, Guid? agentRunId, Guid? workflowRunId) => new()
    {
        Id = Guid.NewGuid(),
        RootTraceId = Guid.NewGuid(),
        AgentRunId = agentRunId,
        WorkflowRunId = workflowRunId,
        NodeId = workflowRunId is null ? null : "decide",
        Scope = grain == DecisionResumeBackends.ToolLedger ? DecisionScopes.Agent : DecisionScopes.Node,
        RequesterType = grain == DecisionResumeBackends.ToolLedger ? DecisionRequesterTypes.Agent : DecisionRequesterTypes.WorkflowNode,
        DecisionType = DecisionTypes.ChooseOne,
        Question = "Ship the migration?",
        Options = new[] { new DecisionOption { Id = "a", Label = "Ship" }, new DecisionOption { Id = "b", Label = "Hold" } },
        RecommendedOption = "a",
        BlockingReason = "needs a human",
        RiskLevel = DecisionRiskLevels.High,
        Policy = DecisionPolicies.HumanRequired,
        TimeoutAt = deadline,
        DedupeKey = Guid.NewGuid().ToString("N"),
        ResumeBackend = grain,
    };

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"stopped-{userId:N}@test.local", Name = $"stopped-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"stopped-{teamId:N}", Name = "Stopped Run Decisions", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
