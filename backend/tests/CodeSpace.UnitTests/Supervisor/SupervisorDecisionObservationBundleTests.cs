using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

[Trait("Category", "Unit")]
public sealed class SupervisorDecisionObservationBundleTests
{
    [Fact]
    public async Task Concurrent_callers_share_one_inflight_read_and_success_for_the_exact_scope()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var expected = new[] { Decision(teamId, runId) };
        var release = new TaskCompletionSource<IReadOnlyList<SupervisorDecisionRecord>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ledger = new ControlledDecisionLog((_, _, _) => release.Task);
        using var bundle = Bundle(ledger);

        var reads = Enumerable.Range(0, 8).Select(_ => bundle.GetForRunAsync(runId, teamId, CancellationToken.None)).ToArray();
        await ledger.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(1));

        ledger.ReadCount.ShouldBe(1, "one request-scoped in-flight task owns the full tape read");
        release.SetResult(expected);
        var results = await Task.WhenAll(reads);

        results.ShouldAllBe(result => ReferenceEquals(result, expected));
        (await bundle.GetForRunAsync(runId, teamId, CancellationToken.None)).ShouldBeSameAs(expected);
        ledger.ReadCount.ShouldBe(1, "a successful observation remains cached for this request scope");
    }

    [Fact]
    public async Task Caller_cancellation_only_cancels_its_wait_not_the_shared_request_read()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var expected = new[] { Decision(teamId, runId) };
        var release = new TaskCompletionSource<IReadOnlyList<SupervisorDecisionRecord>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ledger = new ControlledDecisionLog(async (_, _, requestToken) => await release.Task.WaitAsync(requestToken));
        using var request = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var bundle = Bundle(ledger, request.Token);

        var cancelledWaiter = bundle.GetForRunAsync(runId, teamId, caller.Token);
        var survivingWaiter = bundle.GetForRunAsync(runId, teamId, CancellationToken.None);
        await ledger.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(1));
        caller.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(cancelledWaiter);
        ledger.ObservedToken.CanBeCanceled.ShouldBeTrue("the shared DB task has an explicit request/scope owner token");
        ledger.ObservedToken.IsCancellationRequested.ShouldBeFalse();

        release.SetResult(expected);
        (await survivingWaiter).ShouldBeSameAs(expected);
        ledger.ReadCount.ShouldBe(1);
    }

    [Fact]
    public async Task Request_abort_cancels_the_shared_read_for_every_waiter()
    {
        var ledger = new ControlledDecisionLog(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Array.Empty<SupervisorDecisionRecord>();
        });
        using var request = new CancellationTokenSource();
        using var bundle = Bundle(ledger, request.Token);
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        var waiterA = bundle.GetForRunAsync(runId, teamId, CancellationToken.None);
        var waiterB = bundle.GetForRunAsync(runId, teamId, CancellationToken.None);
        await ledger.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(1));
        request.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(waiterA);
        await Should.ThrowAsync<OperationCanceledException>(waiterB);
        ledger.ReadCount.ShouldBe(1);
        ledger.ObservedToken.IsCancellationRequested.ShouldBeTrue("HTTP RequestAborted owns the shared load lifetime");
    }

    [Fact]
    public async Task Database_fault_is_shared_with_all_waiters_and_a_new_request_scope_retries()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var fault = new InvalidOperationException("decision-observation-db-fault");
        var release = new TaskCompletionSource<IReadOnlyList<SupervisorDecisionRecord>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ledger = new ControlledDecisionLog((_, _, _) => release.Task);
        using var firstRequest = Bundle(ledger);

        var waiterA = firstRequest.GetForRunAsync(runId, teamId, CancellationToken.None);
        var waiterB = firstRequest.GetForRunAsync(runId, teamId, CancellationToken.None);
        await ledger.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(1));
        release.SetException(fault);

        (await Should.ThrowAsync<InvalidOperationException>(waiterA)).ShouldBeSameAs(fault);
        (await Should.ThrowAsync<InvalidOperationException>(waiterB)).ShouldBeSameAs(fault);
        ledger.ReadCount.ShouldBe(1, "the request does not hide a failed read behind duplicate retries");

        ledger.Read = (_, _, _) => Task.FromResult<IReadOnlyList<SupervisorDecisionRecord>>(new[] { Decision(teamId, runId) });
        using var nextRequest = Bundle(ledger);
        (await nextRequest.GetForRunAsync(runId, teamId, CancellationToken.None)).Count.ShouldBe(1);
        ledger.ReadCount.ShouldBe(2, "a fault is scoped to one request bundle; the next request retries the database");
    }

    [Fact]
    public async Task Team_and_supervisor_run_are_both_part_of_the_cache_key()
    {
        var teamId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var otherRunId = Guid.NewGuid();
        var ledger = new ControlledDecisionLog((run, team, _) => Task.FromResult<IReadOnlyList<SupervisorDecisionRecord>>(new[] { Decision(team, run) }));
        using var bundle = Bundle(ledger);

        var own = await bundle.GetForRunAsync(runId, teamId, CancellationToken.None);
        var foreignScope = await bundle.GetForRunAsync(runId, otherTeamId, CancellationToken.None);
        var otherRun = await bundle.GetForRunAsync(otherRunId, teamId, CancellationToken.None);

        own.Single().TeamId.ShouldBe(teamId);
        foreignScope.Single().TeamId.ShouldBe(otherTeamId);
        otherRun.Single().SupervisorRunId.ShouldBe(otherRunId);
        ledger.ReadCount.ShouldBe(3, "no team/run pair may reuse another scope's observation tape");
    }

    [Fact]
    public async Task Disposing_a_non_http_scope_cancels_and_observes_the_shared_load()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ledger = new ControlledDecisionLog(async (_, _, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Array.Empty<SupervisorDecisionRecord>();
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        });
        var bundle = new SupervisorDecisionObservationBundle(ledger, new HttpContextAccessor());
        var read = bundle.GetForRunAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        await ledger.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(1));

        bundle.Dispose();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Should.ThrowAsync<OperationCanceledException>(read);
        ledger.ObservedToken.CanBeCanceled.ShouldBeTrue("a non-HTTP scoped load is owned by bundle disposal, never CancellationToken.None");
    }

    private static SupervisorDecisionObservationBundle Bundle(ISupervisorDecisionLog ledger, CancellationToken requestToken = default)
    {
        var context = new DefaultHttpContext();
        context.RequestAborted = requestToken;
        return new SupervisorDecisionObservationBundle(ledger, new HttpContextAccessor { HttpContext = context });
    }

    private static SupervisorDecisionRecord Decision(Guid teamId, Guid runId) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan,
        IdempotencyKey = Guid.NewGuid().ToString("N"), InputHash = "hash", PayloadJson = "{}", Status = SupervisorDecisionStatus.Succeeded,
    };

    private sealed class ControlledDecisionLog : ISupervisorDecisionLog
    {
        private int _readCount;

        public ControlledDecisionLog(Func<Guid, Guid, CancellationToken, Task<IReadOnlyList<SupervisorDecisionRecord>>> read) { Read = read; }

        public Func<Guid, Guid, CancellationToken, Task<IReadOnlyList<SupervisorDecisionRecord>>> Read { get; set; }
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount => Volatile.Read(ref _readCount);
        public CancellationToken ObservedToken { get; private set; }

        public Task<IReadOnlyList<SupervisorDecisionRecord>> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            ObservedToken = cancellationToken;
            FirstRead.TrySetResult();
            return Read(supervisorRunId, teamId, cancellationToken);
        }

        public Task<SupervisorDecisionClaim> TryClaimAsync(Guid supervisorRunId, Guid teamId, string decisionKind, string idempotencyKey, string inputHash, string payloadJson, long fenceEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryBeginExecutionAsync(Guid decisionId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordTerminalAsync(Guid decisionId, Guid teamId, SupervisorDecisionStatus status, string? outcomeJson, string? error, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SupervisorPriorDecision>> GetTerminalDecisionsAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateOutcomeAsync(Guid decisionId, Guid teamId, string foldedOutcomeJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> ExpireStalePendingAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
