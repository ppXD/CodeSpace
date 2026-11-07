using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Decisions;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Enums;
using MediatR;
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
/// through both terminal writers (the worker's owner token and the legacy run-id writer), and the REAL reconciler's
/// recovery of a run from its spool.</para>
///
/// <para>Every writer that locks more than one of a run's decisions takes them in id order — the end, a stop's close, a
/// recovery — and the decision sweep holds none past its own statement, so none of them deadlocks another. The races
/// are staged with real row locks and read back from <c>pg_blocking_pids</c>, never with sleeps.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class StoppedRunDecisionFlowTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>How long any staged wait here may take before it fails, naming what it waited for.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    private readonly PostgresFixture _fixture;
    private readonly List<string> _spoolDirs = new();

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

        var answerDuringEnd = new IssueWhileHeld<AnswerDecisionResult>(_fixture, command => IsLedgerWriteOf(command, agent.RunId), afterExecution: false, () => AnswerAsync(decisionId, teamId, userId));
        await EndAgentThroughAsync(agent, ending, answerDuringEnd);

        answerDuringEnd.Issued.ShouldBeTrue("precondition: the end closed the run's decisions, and the answer was issued just before it did");
        answerDuringEnd.WaitingBeforeTheStep.ShouldBeFalse("precondition: nothing was waiting on the end before the answer was issued");
        answerDuringEnd.WaitedBehindTheHeld.ShouldBeTrue("the answer must wait on the end's hold on the decision, not be accepted ahead of it");

        var answer = await answerDuringEnd.Step!.WaitAsync(Bound);
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

    // ─── Every writer of a run's decisions takes them in one order ────────────────

    [Theory]
    [InlineData(false)]   // the end queues on the held decision first — pins the order a stop's close locks them in
    [InlineData(true)]    // the stop queues first — pins the order the end's own lock takes them in
    public async Task A_stop_and_an_end_racing_for_one_runs_decisions_take_them_in_one_order_and_never_deadlock(bool stopQueuesFirst)
    {
        // Two decisions whose id order is the reverse of every order a scan meets them in, the lower one held by a third
        // writer so both racers queue on it. A racer locking in scan order takes the higher one first; once the hold goes,
        // each holds one and waits on the other: 40P01. A stop that lost swallowed it and left the run's questions open on
        // a finished run; an end that lost became an executor-error in place of its real result.
        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned: true);
        var (lower, higher) = await SeedDecisionsHigherIdFirstAsync(teamId, agent.RunId);

        await using var hold = await HoldDecisionAsync(lower);
        (await BlockedSessionsAsync()).ShouldBe(0, "precondition: nothing in this database waits on a lock yet");

        var (end, stop) = stopQueuesFirst ? await StopThenEndAsync(agent) : await EndThenStopAsync(agent);
        await hold.ReleaseAsync();

        (await stop.WaitAsync(Bound)).ShouldBeTrue("the stop won the run");
        (await Record.ExceptionAsync(() => end.WaitAsync(Bound))).ShouldBeOfType<AgentRunOwnershipLostException>("the end lost the run to the stop — a deadlock (40P01) here means the two took the decisions in different orders");

        foreach (var decisionId in new[] { lower, higher })
        {
            var row = await LedgerRowAsync(decisionId);
            row.Status.ShouldBe(ToolCallLedgerStatus.Expired, "the stop closed the run's questions — a close lost to a deadlock leaves them open on a finished run");
            row.Error.ShouldBe(StoppedRunDecisions.ExpiredError);
        }
    }

    [Fact]
    public async Task A_decision_sweep_passing_over_a_run_as_it_ends_holds_nothing_the_end_waits_on()
    {
        // The sweep walks overdue decisions by deadline; the end locks one run's by id. With the id order the reverse of the
        // deadline order, a sweep holding each row it touched until its tick committed had the first; the end took the
        // second and waited on the first; the sweep reached the second: 40P01, with the end its victim.
        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned: true);
        var (lower, higher) = await SeedOverdueDecisionsHigherIdDueFirstAsync(teamId, agent.RunId);

        var transactions = new TransactionCounter();
        var endAfterFirstRow = new IssueWhileHeld<bool>(_fixture, command => IsLedgerWriteOf(command, higher), afterExecution: true, async () =>
        {
            await EndAgentThroughAsync(agent, AgentRunStatus.Succeeded, transactions);
            return true;
        });

        (await Record.ExceptionAsync(() => SweepDecisionsThroughAsync(endAfterFirstRow))).ShouldBeNull("the sweep finishes its tick");

        endAfterFirstRow.Issued.ShouldBeTrue("precondition: the end ran while the sweep stood just past the first of the run's overdue decisions");
        endAfterFirstRow.WaitedBehindTheHeld.ShouldBeFalse("the sweep holds no decision past its own write, so the end never waits on it");
        (await Record.ExceptionAsync(() => endAfterFirstRow.Step!.WaitAsync(Bound))).ShouldBeNull("the end lands its real result — no deadlock, no executor-error");
        transactions.Started.ShouldBe(1, "on its first attempt: a second one means it lost a deadlock to the sweep and was written again");
        (await LandedAsync(agent.RunId)).ShouldBe(Landing(AgentRunStatus.Succeeded, higher), "held for review naming the older of its open questions");

        foreach (var decisionId in new[] { lower, higher })
            (await LedgerRowAsync(decisionId)).Status.ShouldBe(ToolCallLedgerStatus.Expired, "and both close with it");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_end_cut_off_between_its_terminal_write_and_its_commit_is_written_once_more_and_lands(bool owned)
    {
        // A connection lost there rolls the whole terminal back, where an autocommitted UPDATE used to have landed. The run
        // still stands where the end's CAS expects it, so writing it once more cannot overwrite anyone's terminal.
        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned);
        var decisionId = await SeedAgentDecisionAsync(teamId, agent.RunId);

        var cutOff = new TransientFaultAfterTerminalWrite();
        var transactions = new TransactionCounter();

        (await Record.ExceptionAsync(() => EndAgentThroughAsync(agent, AgentRunStatus.Succeeded, cutOff, transactions))).ShouldBeNull("the end is written once more and lands");

        cutOff.Fired.ShouldBeTrue("precondition: the connection was lost after the terminal UPDATE, before its commit");
        transactions.Started.ShouldBe(2, "the terminal was written exactly once more");
        (await LandedAsync(agent.RunId)).ShouldBe(Landing(AgentRunStatus.Succeeded, decisionId), "with the contract checked again on the second attempt");
        (await LedgerRowAsync(decisionId)).Status.ShouldBe(ToolCallLedgerStatus.Expired);
    }

    [Fact]
    public async Task An_end_behind_a_decision_a_hung_session_holds_fails_fast_and_is_written_once_more()
    {
        // A session holding one of the run's decisions and hung used to hold the end for Npgsql's whole 30 s command timeout,
        // and then again for its one retry. The end's transaction bounds each lock wait, so the wait ends as a lock timeout —
        // 55P03, Npgsql's "not now" — well inside that, and the end is written once more, landing once the hold is gone.
        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned: true);
        var decisionId = await SeedAgentDecisionAsync(teamId, agent.RunId);

        await using var hold = await HoldDecisionAsync(decisionId);
        var lockWait = new DecisionLockFailure(agent.RunId, hold.ReleaseAsync);
        var transactions = new TransactionCounter();

        (await Record.ExceptionAsync(() => EndAgentThroughAsync(agent, AgentRunStatus.Succeeded, lockWait, transactions))).ShouldBeNull("the end is written once more, and lands once the hold is gone");

        lockWait.SqlState.ShouldBe(PostgresErrorCodes.LockNotAvailable, "the first attempt's wait on the held decision ended as a lock timeout, not a command timeout");
        lockWait.WaitedFor.ShouldNotBeNull().ShouldBeLessThan(TimeSpan.FromSeconds(30), "well inside the command timeout");
        transactions.Started.ShouldBe(2, "the terminal was written exactly once more");
        (await LandedAsync(agent.RunId)).ShouldBe(Landing(AgentRunStatus.Succeeded, decisionId));
        (await LedgerRowAsync(decisionId)).Status.ShouldBe(ToolCallLedgerStatus.Expired);
    }

    [Fact]
    public async Task A_recovery_behind_a_decision_a_hung_session_holds_fails_fast_and_is_left_for_the_next_tick()
    {
        // The spool recovery's transaction bounds its lock waits the same way: a hung holder costs the sweep one lock
        // timeout, not a command timeout, and the run stays Running for the next tick.
        if (OperatingSystem.IsWindows()) return;

        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agentId = await SeedRecoverableAgentAsync(teamId, runId);
        var decisionId = await SeedAgentDecisionAsync(teamId, agentId);

        await using (var hold = await HoldDecisionAsync(decisionId))
        {
            var lockWait = new DecisionLockFailure(agentId, () => Task.CompletedTask);

            await ReconcileThroughAsync(lockWait);

            lockWait.SqlState.ShouldBe(PostgresErrorCodes.LockNotAvailable, "the recovery's wait on the held decision ended as a lock timeout, not a command timeout");
            lockWait.WaitedFor.ShouldNotBeNull().ShouldBeLessThan(TimeSpan.FromSeconds(30), "well inside the command timeout");
            (await AgentStatusAsync(agentId)).ShouldBe(AgentRunStatus.Running, "nothing landed: the run is the next tick's");
        }

        await ReconcileThroughAsync(new TransactionCounter());

        (await LandedAsync(agentId)).ShouldBe(Landing(AgentRunStatus.Succeeded, decisionId), "the next tick recovers it once the hold is gone");
    }

    [Fact]
    public async Task A_stop_refuses_to_join_a_callers_transaction()
    {
        // A stop takes the run's row and then its decisions — the reverse of the order the run's end locks them in — and that
        // is safe only because each is an autocommitted statement of its own, so the stop never holds the one while it waits
        // on the other. Inside a caller's transaction it would.
        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned: true);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            await using var ambient = await db.Database.BeginTransactionAsync();

            await Should.ThrowAsync<InvalidOperationException>(() => scope.Resolve<IAgentRunService>().CancelRunningAsync(agent.RunId, "stopped by the operator", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None));

            (await db.AgentRun.AsNoTracking().Where(r => r.Id == agent.RunId).Select(r => r.Status).SingleAsync()).ShouldBe(AgentRunStatus.Running, "refused before it wrote anything — read inside the caller's transaction, which would see the stop's own uncommitted flip");
        }

        (await CancelAgentAsync(agent.RunId)).ShouldBeTrue("the same stop lands outside a transaction");
    }

    [Fact]
    public async Task An_abandon_refuses_to_join_a_callers_transaction()
    {
        // The reconciler's abandon takes the run's row and then its decisions, as a stop does, and relies on autocommit the
        // same way. The whole reconcile runs inside the caller's transaction here; its wait recovery refuses that outright
        // too, so the pass itself is expected to throw — what matters is whether the abandon wrote.
        var (teamId, _) = await SeedTeamAsync();
        var agentId = await SeedRunningAgentAsync(teamId, livenessAgo: TimeSpan.FromDays(3650));   // its lease lapsed first: the sweep reaches it first

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            await using var ambient = await db.Database.BeginTransactionAsync();

            await Record.ExceptionAsync(() => scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None));

            (await db.AgentRun.AsNoTracking().Where(r => r.Id == agentId).Select(r => r.Status).SingleAsync()).ShouldBe(AgentRunStatus.Running, "the abandon refused before it wrote anything — read inside the caller's transaction, which would see its own uncommitted flip");
        }

        await ReconcileThroughAsync(new TransactionCounter());

        (await AgentStatusAsync(agentId)).ShouldBe(AgentRunStatus.Failed, "the same abandon lands outside a transaction");
    }

    [Fact]
    public async Task An_answer_issued_while_a_recovered_run_is_landing_waits_for_it_and_is_refused_saying_the_run_ended()
    {
        // The spool recovery's mirror of the end's race: its contract check, its CAS and its close are one transaction entered
        // with the run's decisions locked, so an answer is never told "answered" by a run recovered as waiting on that very
        // question, and the close is never a separate write whose failure is swallowed.
        if (OperatingSystem.IsWindows()) return;

        var (teamId, userId) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agentId = await SeedRecoverableAgentAsync(teamId, runId);
        var decisionId = await SeedAgentDecisionAsync(teamId, agentId);

        var answerDuringRecovery = new IssueWhileHeld<AnswerDecisionResult>(_fixture, command => IsLedgerWriteOf(command, agentId), afterExecution: false, () => AnswerAsync(decisionId, teamId, userId));
        await ReconcileThroughAsync(answerDuringRecovery);

        answerDuringRecovery.Issued.ShouldBeTrue("precondition: the recovery closed the run's decisions, and the answer was issued just before it did");
        answerDuringRecovery.WaitingBeforeTheStep.ShouldBeFalse("precondition: nothing was waiting on the recovery before the answer was issued");
        answerDuringRecovery.WaitedBehindTheHeld.ShouldBeTrue("the answer must wait on the recovery's hold on the decision, not be accepted ahead of it");

        var answer = await answerDuringRecovery.Step!.WaitAsync(Bound);
        answer.Outcome.ShouldBe(DecisionAnswerOutcome.AlreadyResolved, "once the recovery committed, the waiting answer found the decision closed");
        answer.Message.ShouldBe(StoppedRunDecisions.EndedError);
        (await LedgerRowAsync(decisionId)).Status.ShouldBe(ToolCallLedgerStatus.Expired);
        (await LandedAsync(agentId)).ShouldBe(Landing(AgentRunStatus.Succeeded, decisionId), "recovered from a clean exit with its question open: held for review naming it");
    }

    // ─── A run that has ended takes no new question, and no question parks on it ───

    [Theory]
    [InlineData(AgentRunStatus.Succeeded, true)]
    [InlineData(AgentRunStatus.Succeeded, false)]
    [InlineData(AgentRunStatus.Failed, true)]
    [InlineData(AgentRunStatus.Failed, false)]
    [InlineData(AgentRunStatus.NeedsReview, true)]
    [InlineData(AgentRunStatus.NeedsReview, false)]
    public async Task A_new_claim_on_a_run_that_has_ended_is_refused_and_records_nothing(AgentRunStatus ending, bool owned)
    {
        // A run's own end keeps its fence epoch, so a decision.request already past the authority guard still owned the run
        // by epoch: it claimed a row, parked it, and left a finished run with an open question in the queue.
        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned);

        await EndAgentAsync(agent, ending);
        (await AgentStatusAsync(agent.RunId)).ShouldBe(ending, "precondition: the run landed where its writer put it, at the epoch the claim below holds");

        await Should.ThrowAsync<AgentRunEndedException>(() => ClaimDecisionAsync(teamId, agent.RunId));

        (await DecisionRowsAsync(agent.RunId)).ShouldBeEmpty("a run that has ended records no new question — so none reaches the queue");
    }

    [Theory]
    [InlineData(TerminalPath.Cancel)]
    [InlineData(TerminalPath.Abandon)]
    [InlineData(TerminalPath.Recovery)]
    public async Task A_new_claim_on_a_run_a_stop_ended_is_refused_as_ended_not_as_lost_ownership(TerminalPath path)
    {
        // A stop, an abandon and a recovery bump the fence epoch as they end the run, so the caller's epoch is stale as well
        // as the run ended — and the run's end is the answer: the fence checked first reported it as lost ownership, which
        // the handler tells the agent to retry.
        if (path == TerminalPath.Recovery && OperatingSystem.IsWindows()) return;

        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedAgentToEndAsync(path, teamId, runId);

        await EndThroughAsync(path, agent);   // CancelRunningAsync / the reconciler's abandon and recovery: the production writers of each ending
        (await AgentStatusAsync(agent.RunId)).ShouldNotBe(AgentRunStatus.Running, "precondition: the run ended");

        await Should.ThrowAsync<AgentRunEndedException>(() => ClaimDecisionAsync(teamId, agent.RunId));

        (await DecisionRowsAsync(agent.RunId)).ShouldBeEmpty("and nothing was recorded");
    }

    [Fact]
    public async Task A_claim_waiting_behind_the_runs_end_is_refused_once_the_end_commits()
    {
        // The claim reads the run under a share lock, so one issued while the end holds the run waits for the end — and
        // must then read the run the end left, not the live one it waited behind.
        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned: true);

        var claimDuringEnd = new IssueWhileHeld<Exception?>(_fixture, command => IsLedgerWriteOf(command, agent.RunId), afterExecution: false, () => Record.ExceptionAsync(() => ClaimDecisionAsync(teamId, agent.RunId)));
        await EndAgentThroughAsync(agent, AgentRunStatus.Succeeded, claimDuringEnd);

        claimDuringEnd.Issued.ShouldBeTrue("precondition: the end had flipped the run and was closing its decisions when the claim was issued");
        claimDuringEnd.WaitingBeforeTheStep.ShouldBeFalse("precondition: nothing was waiting on the end before the claim was issued");
        claimDuringEnd.WaitedBehindTheHeld.ShouldBeTrue("the claim's read of the run waits on the end's hold on it");

        (await claimDuringEnd.Step!.WaitAsync(Bound)).ShouldBeOfType<AgentRunEndedException>("once the end committed, the claim read the run as ended");
        (await DecisionRowsAsync(agent.RunId)).ShouldBeEmpty();
        (await LandedAsync(agent.RunId)).ShouldBe(Landing(AgentRunStatus.Succeeded, openDecisionId: null), "the end landed its own result, untouched by the refused claim");
    }

    /// <summary>Each way an agent run ends: its worker's completion, an operator's stop, the reconciler's abandon of a run whose worker is gone, and its recovery of one that exited while nobody watched.</summary>
    public enum TerminalPath
    {
        Completion,
        Cancel,
        Abandon,
        Recovery,
    }

    [Theory]
    [InlineData(TerminalPath.Completion)]
    [InlineData(TerminalPath.Cancel)]
    [InlineData(TerminalPath.Abandon)]
    [InlineData(TerminalPath.Recovery)]
    public async Task A_decision_claimed_before_its_run_ends_closes_with_the_run_and_never_parks_after_it(TerminalPath path)
    {
        // Claimed before the end, parked after it: every end closed only the parked questions, so the claimed one survived
        // Pending — and its park then landed on the finished run.
        if (path == TerminalPath.Recovery && OperatingSystem.IsWindows()) return;

        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedAgentToEndAsync(path, teamId, runId);

        var claim = await ClaimDecisionAsync(teamId, agent.RunId);
        claim.Outcome.ShouldBe(ToolCallClaimOutcome.Proceed, "precondition: the question was claimed while the run lived");

        await EndThroughAsync(path, agent);

        (await ParkAsync(claim.LedgerId, teamId)).ShouldBeFalse("a park after the end finds nothing left to park");

        var row = await LedgerRowAsync(claim.LedgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Expired, "the run's end closed the claimed question along with the parked ones");
        row.Error.ShouldBe(path is TerminalPath.Cancel or TerminalPath.Abandon ? StoppedRunDecisions.ExpiredError : StoppedRunDecisions.EndedError);
        row.ApprovalToken.ShouldBeNull("and the park never stamped it");
        (await ListPendingAsync(teamId)).ShouldNotContain(d => d.Id == claim.LedgerId);
    }

    [Fact]
    public async Task A_decision_whose_close_the_stop_lost_is_still_never_parked_on_the_stopped_run()
    {
        // A stop closes its run's questions in a statement of its own after the run is stopped, and a failure there is
        // swallowed — the run is stopped either way. A question claimed before the stop must not then park on it.
        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned: true);
        var claim = await ClaimDecisionAsync(teamId, agent.RunId);

        var lostClose = new LostDecisionClose(agent.RunId);
        (await CancelAgentThroughAsync(agent.RunId, lostClose)).ShouldBeTrue("precondition: the stop won the run");
        lostClose.Fired.ShouldBeTrue("precondition: the stop's close of the run's questions was lost");
        (await LedgerRowAsync(claim.LedgerId)).Status.ShouldBe(ToolCallLedgerStatus.Pending, "precondition: so the claimed question is still Pending");

        await Should.ThrowAsync<AgentRunEndedException>(() => ParkAsync(claim.LedgerId, teamId));

        var row = await LedgerRowAsync(claim.LedgerId);
        row.Status.ShouldBe(ToolCallLedgerStatus.Pending, "the park reads the run itself, so it refuses a stopped run even when the close never ran");
        row.ApprovalToken.ShouldBeNull();
        (await ListPendingAsync(teamId)).ShouldNotContain(d => d.Id == claim.LedgerId);
    }

    [Theory]
    [InlineData(TerminalPath.Completion)]
    [InlineData(TerminalPath.Cancel)]
    public async Task A_decision_asked_as_its_run_ends_never_stays_open_on_the_ended_run(TerminalPath path)
    {
        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);

        for (var i = 0; i < 10; i++)
        {
            var agent = await SeedLiveAgentAsync(teamId, runId, owned: true);

            var ask = AskAsync(teamId, agent.RunId);
            var end = EndThroughAsync(path, agent);
            var askFault = await Record.ExceptionAsync(() => ask);
            var endFault = await Record.ExceptionAsync(() => end);

            endFault.ShouldBeNull($"round {i}: the end lands — a deadlock (40P01) here means the question and the end took their locks in different orders");
            if (askFault is not null) askFault.ShouldBeOfType<AgentRunEndedException>($"round {i}: the question was either asked before the end or refused as ended — never a deadlock");
            (await DecisionRowsAsync(agent.RunId)).ShouldNotContain(d => d.Status == ToolCallLedgerStatus.Pending || d.Status == ToolCallLedgerStatus.AwaitingApproval, $"round {i}: no question stays open on a run that has ended");
        }
    }

    [Fact]
    public async Task A_park_racing_the_runs_end_waits_for_the_end_and_never_deadlocks()
    {
        // The end locks the run's questions, then the run. A park that took the run first and then its question would hold
        // what the end waits for while waiting on what the end holds: 40P01. The park reads the run without locking it, so it
        // only ever waits on its own question — behind the end — and finds it closed.
        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned: true);
        var claim = await ClaimDecisionAsync(teamId, agent.RunId);

        var parkDuringEnd = new IssueWhileHeld<bool>(_fixture, command => IsRunFlipOf(command, agent.RunId), afterExecution: false, () => ParkAsync(claim.LedgerId, teamId));
        var transactions = new TransactionCounter();
        await EndAgentThroughAsync(agent, AgentRunStatus.Succeeded, parkDuringEnd, transactions);

        parkDuringEnd.Issued.ShouldBeTrue("precondition: the end held the run's questions and was about to write the run itself when the park was issued");
        parkDuringEnd.WaitingBeforeTheStep.ShouldBeFalse("precondition: nothing was waiting on the end before the park was issued");
        parkDuringEnd.WaitedBehindTheHeld.ShouldBeTrue("the park waits on the end's hold on its question");

        (await parkDuringEnd.Step!.WaitAsync(Bound)).ShouldBeFalse("once the end committed, the park found its question closed — no deadlock surfaced on its side");
        transactions.Started.ShouldBe(1, "the end landed on its first attempt: a second one means it lost a deadlock to the park and was written again");
        (await LandedAsync(agent.RunId)).ShouldBe(Landing(AgentRunStatus.Succeeded, claim.LedgerId), "the end saw the claimed question open and held the run for it");
        (await LedgerRowAsync(claim.LedgerId)).Status.ShouldBe(ToolCallLedgerStatus.Expired);
    }

    /// <summary>A call the run made before it ended: a write whose result was recorded, or a question it claimed and never parked.</summary>
    public enum MadeBeforeTheEnd
    {
        RecordedWrite,
        ClaimedQuestion,
    }

    [Theory]
    [InlineData(MadeBeforeTheEnd.RecordedWrite)]
    [InlineData(MadeBeforeTheEnd.ClaimedQuestion)]
    public async Task A_call_made_before_the_run_ended_still_replays_after_it(MadeBeforeTheEnd made)
    {
        // A run that has ended takes no NEW call — but an identical re-call of one it already made replays that call's
        // terminal instead of being refused, so the answer a call got never changes under it.
        var (teamId, _) = await SeedTeamAsync();
        var runId = await SeedWorkflowRunAsync(teamId);
        var agent = await SeedLiveAgentAsync(teamId, runId, owned: true);
        var toolKind = made == MadeBeforeTheEnd.RecordedWrite ? "git.open_pr" : DecisionToolKinds.DecisionRequest;
        var key = $"{toolKind}:{Guid.NewGuid():N}";

        var first = await ClaimAsync(teamId, agent.RunId, toolKind, key);
        if (made == MadeBeforeTheEnd.RecordedWrite) await RecordSucceededAsync(first.LedgerId, teamId);

        await EndAgentAsync(agent, AgentRunStatus.Succeeded);

        var replay = await ClaimAsync(teamId, agent.RunId, toolKind, key);

        replay.Outcome.ShouldBe(ToolCallClaimOutcome.Duplicate, "the existing call replays — the run's end refuses only a new one");
        replay.LedgerId.ShouldBe(first.LedgerId);
        replay.PriorStatus.ShouldBe(made == MadeBeforeTheEnd.RecordedWrite ? ToolCallLedgerStatus.Succeeded : ToolCallLedgerStatus.Expired);
        replay.PriorError.ShouldBe(made == MadeBeforeTheEnd.RecordedWrite ? null : StoppedRunDecisions.EndedError, "a question the end closed replays that the run ended");
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

    /// <summary>End the run through a scope whose database commands and transactions pass <paramref name="interceptors"/> — the seam that lands a racing step or a fault at an exact point of the end.</summary>
    private async Task EndAgentThroughAsync(LiveAgent agent, AgentRunStatus ending, params IInterceptor[] interceptors)
    {
        using var scope = BeginScopeThrough(interceptors);
        await EndAsync(scope.Resolve<IAgentRunService>(), agent, ending);
    }

    /// <summary>One tick of the decision sweep, dispatched as its recurring job dispatches it — through the mediator, so the sweep's transaction boundary is the production one.</summary>
    private async Task SweepDecisionsThroughAsync(IInterceptor interceptor)
    {
        using var scope = BeginScopeThrough(interceptor);
        await scope.Resolve<IMediator>().Send(new ExpireStaleDecisionsCommand());
    }

    /// <summary>One pass of the agent-run reconciler through a scope whose commands pass <paramref name="interceptor"/>.</summary>
    private async Task ReconcileThroughAsync(IInterceptor interceptor)
    {
        using var scope = BeginScopeThrough(interceptor);
        await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);
    }

    /// <summary>Stop the run through a scope whose database commands pass <paramref name="interceptor"/>.</summary>
    private async Task<bool> CancelAgentThroughAsync(Guid agentId, IInterceptor interceptor)
    {
        using var scope = BeginScopeThrough(interceptor);
        return await scope.Resolve<IAgentRunService>().CancelRunningAsync(agentId, "stopped by the operator", AgentRunAbandonCause.OperatorCancelled, CancellationToken.None);
    }

    /// <summary>The ledger claim the MCP handler makes for a call, at the attempt's own fence epoch (1, its claim's).</summary>
    private async Task<ToolCallClaim> ClaimAsync(Guid teamId, Guid agentId, string toolKind, string key)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IToolCallLedgerService>().TryClaimAsync(agentId, teamId, toolKind, key, new string('0', 64), 1, CancellationToken.None);
    }

    /// <summary>A fresh <c>decision.request</c>'s claim: its row stays Pending until the handler parks it.</summary>
    private Task<ToolCallClaim> ClaimDecisionAsync(Guid teamId, Guid agentId) => ClaimAsync(teamId, agentId, DecisionToolKinds.DecisionRequest, $"{DecisionToolKinds.DecisionRequest}:{Guid.NewGuid():N}");

    /// <summary>The handler's park of a claimed question: the real CAS Pending → AwaitingApproval.</summary>
    private async Task<bool> ParkAsync(Guid ledgerId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IToolCallLedgerService>().TryBeginApprovalAsync(ledgerId, teamId, new ToolCallApprovalPark { Token = Guid.NewGuid().ToString("N"), DeadlineAt = DateTimeOffset.UtcNow.AddHours(1) }, CancellationToken.None);
    }

    /// <summary>Ask a question as the handler does: claim it, then park what was claimed.</summary>
    private async Task AskAsync(Guid teamId, Guid agentId)
    {
        var claim = await ClaimDecisionAsync(teamId, agentId);

        if (claim.Outcome == ToolCallClaimOutcome.Proceed) await ParkAsync(claim.LedgerId, teamId);
    }

    /// <summary>A side-effecting call's own recorded result.</summary>
    private async Task RecordSucceededAsync(Guid ledgerId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IToolCallLedgerService>().RecordTerminalAsync(ledgerId, teamId, ToolCallLedgerStatus.Succeeded, """{"content":[{"type":"text","text":"opened"}],"isError":false}""", null, CancellationToken.None);
    }

    /// <summary>The card a parked question's handler posted, recorded on its row.</summary>
    private async Task RecordCardAsync(Guid ledgerId, Guid teamId, Guid messageId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IToolCallLedgerService>().SetApprovalMessageAsync(ledgerId, teamId, messageId, CancellationToken.None);
    }

    private async Task<List<ToolCallLedger>> DecisionRowsAsync(Guid agentId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking().Where(l => l.AgentRunId == agentId && l.ToolKind == DecisionToolKinds.DecisionRequest).ToListAsync();
    }

    /// <summary>A live agent run for <paramref name="path"/> to end, at fence epoch 1: one its worker holds, for a completion or a stop; one whose worker is gone, for an abandon; one that exited cleanly while nobody watched, for a recovery.</summary>
    private async Task<LiveAgent> SeedAgentToEndAsync(TerminalPath path, Guid teamId, Guid workflowRunId) => path switch
    {
        TerminalPath.Abandon => new LiveAgent(await SeedRunningAgentAsync(teamId, livenessAgo: TimeSpan.FromDays(3650), fenceEpoch: 1), null),   // legacy claim, then its worker died: the sweep reaches its lapsed lease first
        TerminalPath.Recovery => new LiveAgent(await SeedRecoverableAgentAsync(teamId, workflowRunId, fenceEpoch: 1), null),                    // legacy claim, then its agent exited unwatched
        _ => await SeedLiveAgentAsync(teamId, workflowRunId, owned: true),                                                                        // ClaimOwnershipAsync's live run
    };

    /// <summary>End the run along <paramref name="path"/>, through the production writer of that ending.</summary>
    private async Task EndThroughAsync(TerminalPath path, LiveAgent agent)
    {
        if (path == TerminalPath.Completion) await EndAgentAsync(agent, AgentRunStatus.Succeeded);
        else if (path == TerminalPath.Cancel) (await CancelAgentAsync(agent.RunId)).ShouldBeTrue("precondition: the stop won the run");
        else await ReconcileUntilEndedAsync(agent.RunId);
    }

    /// <summary>Reconcile until the run this test owns has ended — a pass is bounded and deployment-wide, so it may reach other tests' stale runs first.</summary>
    private async Task ReconcileUntilEndedAsync(Guid agentId)
    {
        for (var pass = 0; pass < 10 && await AgentStatusAsync(agentId) == AgentRunStatus.Running; pass++)
            await ReconcileThroughAsync(new TransactionCounter());

        (await AgentStatusAsync(agentId)).ShouldNotBe(AgentRunStatus.Running, $"the reconciler did not end run {agentId} within 10 passes — look in agent_run for its lease and runner handle");
    }

    /// <summary>Fails the scope's close of <c>agentRunId</c>'s questions, the step a stop swallows once its run is stopped.</summary>
    private sealed class LostDecisionClose : DbCommandInterceptor
    {
        private readonly Guid _agentRunId;

        public LostDecisionClose(Guid agentRunId) { _agentRunId = agentRunId; }

        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) => Lose(command, result);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) => Lose(command, result);

        private ValueTask<T> Lose<T>(DbCommand command, T result)
        {
            if (!IsLedgerWriteOf(command, _agentRunId)) return ValueTask.FromResult(result);

            Fired = true;

            throw new InvalidOperationException("the connection was lost as the stop closed the run's questions");
        }
    }

    /// <summary>The end queues on the held decision first: its lock waits on the hold; then the stop wins the run and its close queues too.</summary>
    private async Task<(Task End, Task<bool> Stop)> EndThenStopAsync(LiveAgent agent)
    {
        var end = EndAgentAsync(agent, AgentRunStatus.Succeeded);
        await WaitForBlockedSessionsAsync(1, "the end's lock on the run's decisions waiting on the held one");

        var stop = CancelAgentAsync(agent.RunId);
        await WaitForBlockedSessionsAsync(2, "the stop's close of the run's decisions waiting on the held one");

        return (end, stop);
    }

    /// <summary>The stop queues first: the end is held just before it locks the run's decisions while the stop wins the run and its close queues on the hold; then the end's lock queues behind that close.</summary>
    private async Task<(Task End, Task<bool> Stop)> StopThenEndAsync(LiveAgent agent)
    {
        Task<bool>? stop = null;

        var stopBeforeTheEndLocks = new BeforeReader(command => IsDecisionLockOf(command, agent.RunId), async () =>
        {
            stop = CancelAgentAsync(agent.RunId);
            await WaitForBlockedSessionsAsync(1, "the stop's close of the run's decisions waiting on the held one");
        });

        var end = EndAgentThroughAsync(agent, AgentRunStatus.Succeeded, stopBeforeTheEndLocks);
        await WaitForBlockedSessionsAsync(2, "the end's lock on the run's decisions waiting behind the stop's close");

        return (end, stop!);
    }

    /// <summary>Hold <paramref name="decisionId"/>'s row lock in a transaction of a third session, as another writer part-way through the run's decisions would.</summary>
    private async Task<DecisionHold> HoldDecisionAsync(Guid decisionId)
    {
        var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();

        await using (var command = new NpgsqlCommand("SELECT id FROM tool_call_ledger WHERE id = @id FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("id", decisionId);
            await command.ExecuteNonQueryAsync();
        }

        return new DecisionHold(connection, transaction);
    }

    /// <summary>A third session's hold on one decision row: released on demand, and on disposal whatever happened.</summary>
    private sealed class DecisionHold : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;

        public DecisionHold(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public async Task ReleaseAsync() => await _transaction.RollbackAsync();

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    /// <summary>How many sessions in this database wait on a lock right now.</summary>
    private async Task<int> BlockedSessionsAsync()
    {
        using var scope = _fixture.BeginScope();
        var blocked = await scope.Resolve<CodeSpaceDbContext>().Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND cardinality(pg_blocking_pids(pid)) > 0").ToListAsync();

        return blocked.Single();
    }

    /// <summary>The named signal, bounded: at least <paramref name="count"/> sessions in this database waiting on a lock.</summary>
    private async Task WaitForBlockedSessionsAsync(int count, string signal)
    {
        var deadline = DateTimeOffset.UtcNow + Bound;

        while (await BlockedSessionsAsync() < count)
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Within {Bound.TotalSeconds}s there was no sign of {signal}: fewer than {count} sessions of this database are blocked in pg_blocking_pids. Look in pg_stat_activity for what the racers are doing.");

            await Task.Delay(25);
        }
    }

    /// <summary>A write to the ledger carrying <paramref name="id"/>: a run's decisions closing, or one decision's row moving.</summary>
    private static bool IsLedgerWriteOf(DbCommand command, Guid id) => command.CommandText.Contains("UPDATE tool_call_ledger", StringComparison.Ordinal) && CarriesParameter(command, id);

    /// <summary>The end's lock on the run's decisions, taken before it reads which of them are open.</summary>
    private static bool IsDecisionLockOf(DbCommand command, Guid agentRunId) => command.CommandText.Contains("FROM tool_call_ledger", StringComparison.Ordinal) && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal) && CarriesParameter(command, agentRunId);

    /// <summary>The end's terminal write of the run's own row — taken after it has locked the run's decisions.</summary>
    private static bool IsRunFlipOf(DbCommand command, Guid agentRunId) => command.CommandText.Contains("UPDATE agent_run", StringComparison.Ordinal) && CarriesParameter(command, agentRunId);

    private static bool CarriesParameter(DbCommand command, Guid value) => command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == value);

    private static Task EndAsync(IAgentRunService runs, LiveAgent agent, AgentRunStatus ending) =>
        agent.Owner is { } owner ? runs.CompleteAsync(owner, Ending(ending), CancellationToken.None) : runs.CompleteAsync(agent.RunId, Ending(ending), CancellationToken.None);

    /// <summary>The terminal result a worker hands in: the harness's own success, the executor's stalled-agent hold for review, or its generic failure.</summary>
    private static AgentRunResult Ending(AgentRunStatus ending) => ending switch
    {
        AgentRunStatus.Succeeded => new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "did the work" },
        AgentRunStatus.NeedsReview => new AgentRunResult { Status = AgentRunStatus.NeedsReview, CompletionDisposition = CompletionDisposition.Blocked, ExitReason = AgentAcceptanceContract.StalledExitReason, Error = "the agent produced no output for the idle window" },
        _ => new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = AgentRunExecutor.GenericExecutorExitReason, Error = "the executor faulted" },
    };

    /// <summary>A scope whose database commands and transactions pass <paramref name="interceptors"/>.</summary>
    private ILifetimeScope BeginScopeThrough(params IInterceptor[] interceptors)
    {
        DbContextOptions<CodeSpaceDbContext> production;
        using (var probe = _fixture.BeginScope())
            production = probe.Resolve<DbContextOptions<CodeSpaceDbContext>>();

        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>(production).AddInterceptors(interceptors).Options;

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
    /// Issues a racing step once, at the command of the intercepted scope that <c>at</c> picks — just before it runs, or just
    /// after — and holds that scope there until the step is either seen WAITING behind the scope's own database session or
    /// has finished without waiting. Which of the two happened is the verdict: a step that finished first got past whatever
    /// the held session had locked, or found nothing locked at all. Both kinds of command are watched: the close of a run's
    /// decisions returns the rows it closed, so it runs as a reader.
    /// </summary>
    private sealed class IssueWhileHeld<T> : DbCommandInterceptor
    {
        private readonly PostgresFixture _fixture;
        private readonly Func<DbCommand, bool> _at;
        private readonly bool _afterExecution;
        private readonly Func<Task<T>> _step;
        private int _fired;

        public IssueWhileHeld(PostgresFixture fixture, Func<DbCommand, bool> at, bool afterExecution, Func<Task<T>> step)
        {
            _fixture = fixture;
            _at = at;
            _afterExecution = afterExecution;
            _step = step;
        }

        public Task<T>? Step { get; private set; }

        public bool Issued => Step is not null;

        public bool WaitingBeforeTheStep { get; private set; }

        public bool WaitedBehindTheHeld { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_afterExecution) await HoldAsync(command).ConfigureAwait(false);

            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (_afterExecution) await HoldAsync(command).ConfigureAwait(false);

            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!_afterExecution) await HoldAsync(command).ConfigureAwait(false);

            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (_afterExecution) await HoldAsync(command).ConfigureAwait(false);

            return result;
        }

        private async Task HoldAsync(DbCommand command)
        {
            if (!_at(command) || Interlocked.Exchange(ref _fired, 1) == 1) return;

            var held = ((NpgsqlConnection)command.Connection!).ProcessID;

            WaitingBeforeTheStep = await AnyoneWaitingBehindAsync(held).ConfigureAwait(false);
            Step = Task.Run(_step);
            WaitedBehindTheHeld = await WaitingBehindOrDoneAsync(held).ConfigureAwait(false);
        }

        /// <summary>The named signal, bounded: true once a session is blocked behind the held one (the step waiting on its hold), false once the step finished without ever waiting.</summary>
        private async Task<bool> WaitingBehindOrDoneAsync(int held)
        {
            var deadline = DateTimeOffset.UtcNow + Bound;

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await AnyoneWaitingBehindAsync(held).ConfigureAwait(false)) return true;
                if (Step!.IsCompleted) return false;

                await Task.Delay(25).ConfigureAwait(false);
            }

            throw new TimeoutException($"Within {Bound.TotalSeconds}s the racing step neither finished nor was seen waiting behind the held session (no session lists backend {held} in pg_blocking_pids). Look in pg_stat_activity for what the step waits on.");
        }

        private async Task<bool> AnyoneWaitingBehindAsync(int held)
        {
            using var scope = _fixture.BeginScope();
            var waiting = await scope.Resolve<CodeSpaceDbContext>().Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE {held} = ANY(pg_blocking_pids(pid))").ToListAsync().ConfigureAwait(false);

            return waiting.Single() > 0;
        }
    }

    /// <summary>Runs <c>before</c> once, just before the first reader command of its scope that <c>at</c> picks.</summary>
    private sealed class BeforeReader : DbCommandInterceptor
    {
        private readonly Func<DbCommand, bool> _at;
        private readonly Func<Task> _before;
        private int _fired;

        public BeforeReader(Func<DbCommand, bool> at, Func<Task> before)
        {
            _at = at;
            _before = before;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (_at(command) && Interlocked.Exchange(ref _fired, 1) == 0) await _before().ConfigureAwait(false);

            return result;
        }
    }

    /// <summary>The first failure of the scope's lock on <c>agentRunId</c>'s decisions — its SQLSTATE and how long the lock waited — with <c>then</c> run once it is recorded.</summary>
    private sealed class DecisionLockFailure : DbCommandInterceptor
    {
        private readonly Guid _agentRunId;
        private readonly Func<Task> _then;
        private int _fired;

        public DecisionLockFailure(Guid agentRunId, Func<Task> then)
        {
            _agentRunId = agentRunId;
            _then = then;
        }

        public string? SqlState { get; private set; }

        public TimeSpan? WaitedFor { get; private set; }

        public override async Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!IsDecisionLockOf(command, _agentRunId) || Interlocked.Exchange(ref _fired, 1) == 1) return;

            SqlState = (eventData.Exception as PostgresException)?.SqlState ?? eventData.Exception.GetType().Name;
            WaitedFor = eventData.Duration;

            await _then().ConfigureAwait(false);
        }
    }

    /// <summary>Counts the transactions its scope starts: a terminal written once more shows as a second one.</summary>
    private sealed class TransactionCounter : DbTransactionInterceptor
    {
        private int _started;

        public int Started => _started;

        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _started);

            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Loses the connection once, right after the terminal UPDATE ran and before its transaction commits, with a fault the terminal writer treats as transient.</summary>
    private sealed class TransientFaultAfterTerminalWrite : DbCommandInterceptor
    {
        private int _fired;

        public bool Fired => _fired == 1;

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("agent_run", StringComparison.Ordinal) || !command.CommandText.Contains("completed_at", StringComparison.Ordinal) || Interlocked.Exchange(ref _fired, 1) == 1) return ValueTask.FromResult(result);

            throw new TimeoutException("the connection was lost after the terminal UPDATE, before its commit");
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

    private async Task<AgentRunStatus> AgentStatusAsync(Guid agentId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.Id == agentId).Select(r => r.Status).SingleAsync();
    }

    private async Task<ToolCallLedger> LedgerRowAsync(Guid ledgerId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.AsNoTracking().SingleAsync(l => l.Id == ledgerId);
    }

    // ─── Seeding ──────────────────────────────────────────────────────────────────

    /// <summary>A Running agent run; <paramref name="livenessAgo"/> past the window makes it one the reconciler abandons (the heartbeat and lease lapse together). <paramref name="fenceEpoch"/> 1 is what the legacy claim (<c>MarkRunningAsync</c>) stamps on a Queued run.</summary>
    private async Task<Guid> SeedRunningAgentAsync(Guid teamId, TimeSpan livenessAgo, long fenceEpoch = 0)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var agentId = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow - livenessAgo;

        db.AgentRun.Add(new AgentRun { Id = agentId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, StartedAt = stamp, HeartbeatAt = stamp, LeaseExpiresAt = stamp + AgentRunLiveness.Window, FenceEpoch = fenceEpoch });

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
        var ledgerId = Guid.NewGuid();

        return await SeedAgentDecisionAsync(teamId, agentId, ledgerId, $"decision.request:{ledgerId:N}", DateTimeOffset.UtcNow.AddHours(1));
    }

    /// <summary>As <see cref="SeedAgentDecisionAsync(Guid, Guid)"/>, with the row's id, idempotency key and deadline chosen — the orders a writer can meet a run's decisions in.</summary>
    private async Task<Guid> SeedAgentDecisionAsync(Guid teamId, Guid agentId, Guid ledgerId, string idempotencyKey, DateTimeOffset deadline)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = ledgerId, TeamId = teamId, AgentRunId = agentId, ToolKind = DecisionToolKinds.DecisionRequest,
            IdempotencyKey = idempotencyKey, InputHash = new string('0', 64),
            Status = ToolCallLedgerStatus.AwaitingApproval, ApprovalDeadlineAt = deadline,
            DecisionEnvelopeJson = JsonSerializer.Serialize(Envelope(deadline, DecisionResumeBackends.ToolLedger, agentId, workflowRunId: null), Json),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return ledgerId;
    }

    /// <summary>Two parked decisions of one run, the HIGHER id seeded first and given the earlier key, so it comes first in every order a scan meets them in (physical, idempotency key, created) and second only by id. Separate saves, because one save inserts in key order.</summary>
    private async Task<(Guid Lower, Guid Higher)> SeedDecisionsHigherIdFirstAsync(Guid teamId, Guid agentId)
    {
        var (lower, higher) = OrderedIds();
        var deadline = DateTimeOffset.UtcNow.AddHours(1);

        await SeedAgentDecisionAsync(teamId, agentId, higher, $"decision.request:a-{higher:N}", deadline);
        await SeedAgentDecisionAsync(teamId, agentId, lower, $"decision.request:b-{lower:N}", deadline);

        return (lower, higher);
    }

    /// <summary>Two overdue, human-required decisions of one run whose id order is the reverse of their deadline order: the higher id fell due first, so the decision sweep reaches it first.</summary>
    private async Task<(Guid Lower, Guid Higher)> SeedOverdueDecisionsHigherIdDueFirstAsync(Guid teamId, Guid agentId)
    {
        var (lower, higher) = OrderedIds();
        var longAgo = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);   // due ahead of anything else overdue in this shared database

        await SeedAgentDecisionAsync(teamId, agentId, higher, $"decision.request:{higher:N}", longAgo);
        await SeedAgentDecisionAsync(teamId, agentId, lower, $"decision.request:{lower:N}", longAgo.AddSeconds(1));

        return (lower, higher);
    }

    /// <summary>Two fresh ids in the order PostgreSQL compares uuids: byte by byte, as their canonical text reads.</summary>
    private static (Guid Lower, Guid Higher) OrderedIds()
    {
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());

        return string.CompareOrdinal(first.ToString(), second.ToString()) < 0 ? (first, second) : (second, first);
    }

    /// <summary>A Running agent run of <paramref name="workflowRunId"/> whose worker is gone and whose process exited cleanly while nobody watched — a lapsed lease, a dead pid, a clean exit marker in its spool: the run the reconciler recovers rather than abandons.</summary>
    private async Task<Guid> SeedRecoverableAgentAsync(Guid teamId, Guid workflowRunId, long fenceEpoch = 0)
    {
        var spoolDir = Path.Combine(Path.GetTempPath(), "cs-decision-recover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spoolDir);
        _spoolDirs.Add(spoolDir);
        await File.WriteAllTextAsync(Path.Combine(spoolDir, "exit"), "0");

        var handle = new SandboxHandle { Kind = "local", ProcessId = DeadPid(), SpoolDirectory = spoolDir, Deadline = DateTimeOffset.UtcNow.AddHours(1) };
        var agentId = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.AgentRun.Add(new AgentRun { Id = agentId, TeamId = teamId, WorkflowRunId = workflowRunId, Harness = "codex-cli", Status = AgentRunStatus.Running, StartedAt = stamp, HeartbeatAt = stamp, LeaseExpiresAt = stamp + AgentRunLiveness.Window, RunnerHandleJson = JsonSerializer.Serialize(handle, AgentJson.Options), FenceEpoch = fenceEpoch });

        await db.SaveChangesAsync();
        return agentId;
    }

    /// <summary>A pid guaranteed dead: start a trivial process, let it exit, return its reaped pid.</summary>
    private static int DeadPid()
    {
        using var process = Process.Start(new ProcessStartInfo { FileName = "/bin/sh", ArgumentList = { "-c", "exit 0" }, UseShellExecute = false })!;
        process.WaitForExit();

        return process.Id;
    }

    public void Dispose()
    {
        foreach (var dir in _spoolDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
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
