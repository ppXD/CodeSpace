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
using Npgsql;
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
///
/// <para>A run that ENDS on its own — succeeds, or fails, executor-error included — used to keep them too: only a stop
/// closed them, so a question its run had finished without was deferred forever and stayed in the Room. The run's own
/// terminal write now closes them in the same transaction, so an answer is either seen by that write or refused
/// saying the run ended. Drives the REAL <see cref="IAgentRunService.CompleteAsync(AgentRunOwnerToken, AgentRunResult, CancellationToken)"/>
/// through both terminal writers (the worker's owner token and the legacy run-id writer).</para>
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

    // ─── A run that ends on its own closes its unanswered decisions too ─────────────

    [Theory]
    [InlineData(AgentRunStatus.Succeeded, true)]
    [InlineData(AgentRunStatus.Succeeded, false)]
    [InlineData(AgentRunStatus.Failed, true)]
    [InlineData(AgentRunStatus.Failed, false)]
    public async Task A_run_that_ends_with_its_decision_unanswered_closes_it_out_of_the_queue_and_the_Room(AgentRunStatus ending, bool owned)
    {
        var (teamId, userId) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned);
        var decisionId = await SeedAgentDecisionAsync(teamId, agent.RunId);

        (await ListPendingForRunAsync(runId, teamId)).ShouldContain(d => d.Id == decisionId, "precondition: the Room shows the parked question while its run lives");

        await EndAgentAsync(agent, ending);

        (await ListPendingForRunAsync(runId, teamId)).ShouldBeEmpty("an ended run's question leaves the Room with it — nobody's answer reaches a run that is over");
        (await ListPendingAsync(teamId)).ShouldNotContain(d => d.Id == decisionId, "and the team queue, where a human-required question would otherwise wait forever");

        var row = await LedgerRowAsync(decisionId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Expired);
        row.Error.ShouldBe(StoppedRunDecisions.EndedError);

        var answer = await AnswerAsync(decisionId, teamId, userId);
        answer.Outcome.ShouldBe(DecisionAnswerOutcome.AlreadyResolved, "a late answer is refused, not accepted into a run that is over");
        answer.Message.ShouldBe(StoppedRunDecisions.EndedError, "and told the run ended — not that it was stopped, nor that someone else answered");

        (await LandedAsync(agent.RunId)).ShouldBe(Landing(ending, decisionId), "a would-be success that left its question open is held for review naming it; a failure stays a failure");
    }

    [Fact]
    public async Task An_answer_that_lands_before_the_run_ends_is_kept_and_the_run_is_not_held_for_it()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned: true);
        var decisionId = await SeedAgentDecisionAsync(teamId, agent.RunId);

        (await AnswerAsync(decisionId, teamId, userId)).Outcome.ShouldBe(DecisionAnswerOutcome.Answered);
        await EndAgentAsync(agent, AgentRunStatus.Succeeded);

        var row = await LedgerRowAsync(decisionId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Succeeded, "the end closes only an UNANSWERED decision — the answer that won stays the decision's answer");
        row.ResultJson.ShouldNotBeNull().ShouldContain("\"a\"", customMessage: "with the option the person chose");
        (await LandedAsync(agent.RunId)).ShouldBe(Landing(AgentRunStatus.Succeeded, openDecisionId: null), "the end saw the answer, so nothing is left open to hold the run for");
    }

    [Theory]
    [InlineData(AgentRunStatus.Succeeded)]
    [InlineData(AgentRunStatus.Failed)]
    public async Task An_answer_whose_write_loses_to_the_run_ending_is_refused_saying_the_run_ended(AgentRunStatus ending)
    {
        // The answer read the decision still open, then the run's end landed before the answer's own write: the
        // answer's CAS moves nothing — and says why.
        var (teamId, userId) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned: true);
        var decisionId = await SeedAgentDecisionAsync(teamId, agent.RunId);

        var endBeforeWrite = new BeforeLedgerWriteFault(() => EndAgentAsync(agent, ending));
        var answer = await AnswerThroughAsync(endBeforeWrite, decisionId, teamId, userId);

        endBeforeWrite.Fired.ShouldBeTrue("precondition: the run ended between the answer's read and its write");
        answer.Outcome.ShouldBe(DecisionAnswerOutcome.AlreadyResolved, "the answer lost the row to the run's end");
        answer.Message.ShouldBe(StoppedRunDecisions.EndedError, "and is told the run ended");
        (await LedgerRowAsync(decisionId)).Status.ShouldBe(ToolCallLedgerStatus.Expired);
        (await LandedAsync(agent.RunId)).ShouldBe(Landing(ending, decisionId));
    }

    [Theory]
    [InlineData(AgentRunStatus.Succeeded)]
    [InlineData(AgentRunStatus.Failed)]
    public async Task An_answer_issued_while_the_run_is_ending_waits_for_the_end_and_is_refused_saying_the_run_ended(AgentRunStatus ending)
    {
        // The exact window a two-step end leaves open: the end has read the decision open (and, for a success, held the
        // run for it) but not yet closed it. The answer issued there must queue behind the end — never slip in ahead of
        // it and be told "answered" by a run that has already recorded the question as unanswered.
        var (teamId, userId) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned: true);
        var decisionId = await SeedAgentDecisionAsync(teamId, agent.RunId);

        var answerDuringEnd = new AnswerDuringEnd(_fixture, () => AnswerAsync(decisionId, teamId, userId));
        await EndAgentThroughAsync(answerDuringEnd, agent, ending);

        answerDuringEnd.Issued.ShouldBeTrue("precondition: the end closed the run's decisions, and the answer was issued just before it did");
        answerDuringEnd.WaitingBeforeTheAnswer.ShouldBeFalse("precondition: nothing was waiting on the end before the answer was issued");
        answerDuringEnd.WaitedBehindTheEnd.ShouldBeTrue("the answer must wait on the end's hold on the decision, not be accepted ahead of it");

        var answer = await answerDuringEnd.Answer!.WaitAsync(AnswerDuringEnd.Bound);
        answer.Outcome.ShouldBe(DecisionAnswerOutcome.AlreadyResolved, "once the end committed, the waiting answer found the decision closed");
        answer.Message.ShouldBe(StoppedRunDecisions.EndedError);
        (await LedgerRowAsync(decisionId)).Status.ShouldBe(ToolCallLedgerStatus.Expired);
        (await LandedAsync(agent.RunId)).ShouldBe(Landing(ending, decisionId));
    }

    [Theory]
    [InlineData(AgentRunStatus.Succeeded)]
    [InlineData(AgentRunStatus.Failed)]
    public async Task An_answer_racing_the_run_ending_is_either_seen_by_the_end_or_refused_never_both(AgentRunStatus ending)
    {
        var (teamId, userId) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);

        for (var i = 0; i < 10; i++)
        {
            var agent = await SeedLiveAgentAsync(teamId, runId, owned: true);
            var decisionId = await SeedAgentDecisionAsync(teamId, agent.RunId);

            var answer = AnswerAsync(decisionId, teamId, userId);
            var end = EndAgentAsync(agent, ending);
            await Task.WhenAll(answer, end);

            var answered = (await answer).Outcome == DecisionAnswerOutcome.Answered;

            (await LedgerRowAsync(decisionId)).Status.ShouldBe(answered ? ToolCallLedgerStatus.Succeeded : ToolCallLedgerStatus.Expired, $"round {i}: the decision carries the answer exactly when the answerer was told Answered");
            if (!answered) (await answer).Message.ShouldBe(StoppedRunDecisions.EndedError, $"round {i}: a refused answer is told the run ended");
            (await LandedAsync(agent.RunId)).ShouldBe(Landing(ending, answered ? null : decisionId), $"round {i}: the run is held for the question exactly when its end closed it unanswered");
        }
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
        using var scope = BeginScopeThrough(fault);
        return await scope.Resolve<IDecisionAnswerService>().AnswerAsync(decisionId, new[] { "a" }, null, teamId, userId, CancellationToken.None);
    }

    /// <summary>End the run the way its worker does: the terminal result lands through the run's own terminal writer — the owner token when a worker holds the run, else the legacy run-id writer.</summary>
    private async Task EndAgentAsync(LiveAgent agent, AgentRunStatus ending)
    {
        using var scope = _fixture.BeginScope();
        await EndAsync(scope.Resolve<IAgentRunService>(), agent, ending);
    }

    /// <summary>End the run through a scope whose database commands pass <paramref name="fault"/> — the seam that lands an answer at an exact point of the end.</summary>
    private async Task EndAgentThroughAsync(IInterceptor fault, LiveAgent agent, AgentRunStatus ending)
    {
        using var scope = BeginScopeThrough(fault);
        await EndAsync(scope.Resolve<IAgentRunService>(), agent, ending);
    }

    private static Task EndAsync(IAgentRunService runs, LiveAgent agent, AgentRunStatus ending) =>
        agent.Owner is { } owner ? runs.CompleteAsync(owner, Ending(ending), CancellationToken.None) : runs.CompleteAsync(agent.RunId, Ending(ending), CancellationToken.None);

    /// <summary>The terminal result a worker hands in: the harness's own success, or the executor's generic failure.</summary>
    private static AgentRunResult Ending(AgentRunStatus ending) => ending == AgentRunStatus.Succeeded
        ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "did the work" }
        : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = AgentRunExecutor.GenericExecutorExitReason, Error = "the executor faulted" };

    /// <summary>A scope whose database commands pass <paramref name="interceptor"/>.</summary>
    private ILifetimeScope BeginScopeThrough(IInterceptor interceptor)
    {
        DbContextOptions<CodeSpaceDbContext> production;
        using (var probe = _fixture.BeginScope())
            production = probe.Resolve<DbContextOptions<CodeSpaceDbContext>>();

        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>(production).AddInterceptors(interceptor).Options;

        return _fixture.BeginScope(b => b.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance());
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

    /// <summary>
    /// Issues an answer once, just before the run's end closes the run's decisions — its only write to the ledger, made
    /// after it has read them open and written its terminal — and holds the end there until that answer is either seen
    /// WAITING behind the end's own database session or has finished without waiting. Finishing first is the defect:
    /// the answer was accepted by a run whose end had already recorded its question as unanswered.
    /// </summary>
    private sealed class AnswerDuringEnd : DbCommandInterceptor
    {
        public static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

        private readonly PostgresFixture _fixture;
        private readonly Func<Task<AnswerDecisionResult>> _answer;
        private int _fired;

        public AnswerDuringEnd(PostgresFixture fixture, Func<Task<AnswerDecisionResult>> answer)
        {
            _fixture = fixture;
            _answer = answer;
        }

        public Task<AnswerDecisionResult>? Answer { get; private set; }

        public bool Issued => Answer is not null;

        public bool WaitingBeforeTheAnswer { get; private set; }

        public bool WaitedBehindTheEnd { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.StartsWith("UPDATE tool_call_ledger", StringComparison.Ordinal) || Interlocked.Exchange(ref _fired, 1) == 1) return result;

            var endSession = ((NpgsqlConnection)command.Connection!).ProcessID;

            WaitingBeforeTheAnswer = await AnyoneWaitingBehindAsync(endSession).ConfigureAwait(false);
            Answer = Task.Run(_answer);
            WaitedBehindTheEnd = await WaitingBehindOrDoneAsync(endSession).ConfigureAwait(false);

            return result;
        }

        /// <summary>The named signal, bounded: true once a session is blocked behind the end's (the answer waiting on its hold), false once the answer finished without ever waiting.</summary>
        private async Task<bool> WaitingBehindOrDoneAsync(int endSession)
        {
            var deadline = DateTimeOffset.UtcNow + Bound;

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await AnyoneWaitingBehindAsync(endSession).ConfigureAwait(false)) return true;
                if (Answer!.IsCompleted) return false;

                await Task.Delay(25).ConfigureAwait(false);
            }

            throw new TimeoutException($"Within {Bound.TotalSeconds}s the answer neither finished nor was seen waiting behind the run's end (no session lists backend {endSession} in pg_blocking_pids). Look in pg_stat_activity for the answer's 'UPDATE tool_call_ledger' and what it waits on.");
        }

        private async Task<bool> AnyoneWaitingBehindAsync(int endSession)
        {
            using var scope = _fixture.BeginScope();
            var waiting = await scope.Resolve<CodeSpaceDbContext>().Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE {endSession} = ANY(pg_blocking_pids(pid))").ToListAsync().ConfigureAwait(false);

            return waiting.Single() > 0;
        }
    }

    private async Task<IReadOnlyList<PendingDecision>> ListPendingAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IDecisionQueueService>().ListPendingAsync(teamId, CancellationToken.None);
    }

    /// <summary>The Room's read: the questions one workflow run is parked on.</summary>
    private async Task<IReadOnlyList<PendingDecision>> ListPendingForRunAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IDecisionQueueService>().ListPendingForRunAsync(runId, teamId, CancellationToken.None);
    }

    /// <summary>Where the run landed: its status, and the decision its result names as left unanswered, if any.</summary>
    private async Task<(AgentRunStatus Status, Guid? OpenDecisionId)> LandedAsync(Guid agentId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentId);

        return (run.Status, JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!.PendingDecisionId);
    }

    /// <summary>Where a run must land given the decision its end found open: a would-be success is held NeedsReview naming it (the completion contract); every other ending is its own final word and names none.</summary>
    private static (AgentRunStatus Status, Guid? OpenDecisionId) Landing(AgentRunStatus ending, Guid? openDecisionId) =>
        ending == AgentRunStatus.Succeeded && openDecisionId is not null ? (AgentRunStatus.NeedsReview, openDecisionId) : (ending, null);

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

    /// <summary>A live agent run of <paramref name="workflowRunId"/>, held by a worker's owner token (<paramref name="owned"/>, the production writer) or by nobody (the legacy run-id writer).</summary>
    private async Task<LiveAgent> SeedLiveAgentAsync(Guid teamId, Guid workflowRunId, bool owned)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var agent = new LiveAgent(Guid.NewGuid(), owned ? Guid.NewGuid() : null);
        var now = DateTimeOffset.UtcNow;

        db.AgentRun.Add(new AgentRun { Id = agent.RunId, TeamId = teamId, WorkflowRunId = workflowRunId, Harness = "codex-cli", Status = AgentRunStatus.Running, StartedAt = now, HeartbeatAt = now, LeaseExpiresAt = now + AgentRunLiveness.Window, OwnerId = agent.OwnerId, FenceEpoch = 1 });

        await db.SaveChangesAsync();
        return agent;
    }

    /// <summary>A live agent run and the worker holding it, if one does.</summary>
    private sealed record LiveAgent(Guid RunId, Guid? OwnerId)
    {
        public AgentRunOwnerToken? Owner => OwnerId is { } ownerId ? new AgentRunOwnerToken(RunId, ownerId, 1) : null;
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
        var runId = await SeedWorkflowRunAsync(teamId);   // the wait's run FK is not an EF navigation, so the run commits first

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var waitId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = waitId, RunId = runId, NodeId = "decide", IterationKey = string.Empty, WaitKind = WorkflowWaitKinds.Decision,
            Token = Guid.NewGuid().ToString("N"), Status = WorkflowWaitStatuses.Pending, CreatedAt = now,
            PayloadJson = JsonSerializer.Serialize(Envelope(now.AddHours(1), DecisionResumeBackends.WorkflowWait, agentRunId: null, runId), Json),
        });
        await db.SaveChangesAsync();

        return (runId, waitId);
    }

    /// <summary>A Suspended manual workflow run — the run a Room shows, whose agents' questions it reads.</summary>
    private async Task<Guid> SeedWorkflowRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
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

        await db.SaveChangesAsync();
        return runId;
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
