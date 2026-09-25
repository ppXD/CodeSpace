using System.Data.Common;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The hold points the stop / continue interleaving suites put a writer on: a named signal awaited with a bound, a
/// session seen blocked on a row lock, a database command held once before or after it runs, and a decision-log call
/// held once around its commit. Every hold is one-shot and fires only in the scope it is registered in.
/// </summary>
public static class StopContinueSignals
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Awaits <paramref name="signal"/> for at most <see cref="Timeout"/>, failing with the signal's name.</summary>
    public static async Task AwaitAsync(Task signal, string name)
    {
        try { await signal.WaitAsync(Timeout).ConfigureAwait(false); }
        catch (TimeoutException) { throw new TimeoutException($"Timed out after {Timeout.TotalSeconds}s waiting for {name}."); }
    }

    /// <summary>
    /// Waits, bounded, until session <paramref name="pid"/> is blocked on a lock — the signal a lock handoff proceeds on.
    /// Fails at once when <paramref name="writer"/>, the call running on that session, finishes first: it never queued
    /// behind the lock it was meant to wait for, and there is nothing left to wait on.
    /// </summary>
    public static async Task WaitForLockWaitAsync(PostgresFixture fixture, int pid, string signal, Task writer)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var deadline = DateTime.UtcNow + Timeout;

        while (!await db.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {pid} AND wait_event_type = 'Lock') AS \"Value\"").SingleAsync().ConfigureAwait(false))
        {
            if (writer.IsCompleted)
                throw new InvalidOperationException($"The writer on session {pid} finished without ever waiting for {signal}: nothing held the lock it should have queued behind.");

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out after {Timeout.TotalSeconds}s waiting for {signal}. Diagnose with: psql -c \"SELECT wait_event_type, wait_event, query FROM pg_stat_activity WHERE pid = {pid}\"");

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>A scope whose database commands pass through <paramref name="interceptors"/>, over the fixture's own context options.</summary>
    public static ILifetimeScope InterceptedScope(PostgresFixture fixture, params IInterceptor[] interceptors) => InterceptedScope(fixture, _ => { }, interceptors);

    /// <summary>An intercepted scope with further registrations of the caller's.</summary>
    public static ILifetimeScope InterceptedScope(PostgresFixture fixture, Action<ContainerBuilder> configure, params IInterceptor[] interceptors)
    {
        DbContextOptions<CodeSpaceDbContext> baseOptions;
        using (var root = fixture.BeginScope())
            baseOptions = root.Resolve<DbContextOptions<CodeSpaceDbContext>>();

        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>(baseOptions).AddInterceptors(interceptors).Options;

        return fixture.BeginScope(builder =>
        {
            builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
            configure(builder);
        });
    }
}

/// <summary>Holds the first database command <paramref name="matches"/> accepts, once: before it runs, or — with <paramref name="afterItRuns"/> — after, with its locks taken and its transaction still open. Signals <see cref="Reached"/>, then waits for <see cref="Release"/>.</summary>
public sealed class HeldCommand(Func<string, bool> matches, bool afterItRuns = false) : DbCommandInterceptor
{
    private int _fired;

    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (!afterItRuns) await HoldIfMatchedAsync(command).ConfigureAwait(false);
        return result;
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (afterItRuns) await HoldIfMatchedAsync(command).ConfigureAwait(false);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!afterItRuns) await HoldIfMatchedAsync(command).ConfigureAwait(false);
        return result;
    }

    public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (afterItRuns) await HoldIfMatchedAsync(command).ConfigureAwait(false);
        return result;
    }

    private async Task HoldIfMatchedAsync(DbCommand command)
    {
        if (!matches(command.CommandText) || Interlocked.Exchange(ref _fired, 1) == 1) return;

        Reached.TrySetResult();
        await Release.Task.ConfigureAwait(false);
    }
}

/// <summary>The decision-log step a <see cref="DecisionLogHold"/> holds a supervisor turn at.</summary>
public enum DecisionLogStep
{
    /// <summary>Once the decision's claim row has committed, still Pending.</summary>
    AfterClaim,

    /// <summary>Once the decision has committed Running, before its side effect.</summary>
    AfterBegin,

    /// <summary>Once the side effect has committed, before the decision's terminal record is written.</summary>
    BeforeTerminal,
}

/// <summary>
/// Holds a supervisor turn once at <paramref name="at"/>: signals <see cref="Reached"/>, then waits for
/// <see cref="Release"/>. <see cref="Decorate"/> puts it on a scope's decision log; armed through
/// <see cref="SupervisorDecisionScript.HoldDecisionLog"/> it holds the run's next walk instead, the engine's own included.
/// </summary>
public sealed class DecisionLogHold(DecisionLogStep at)
{
    private int _fired;

    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Decorate(ContainerBuilder builder) => builder.RegisterDecorator<ISupervisorDecisionLog>((_, _, inner) => new HeldDecisionLog(inner, this));

    /// <summary>Whether the call at <paramref name="step"/> is the one to hold: the named step, reached for the first time.</summary>
    internal bool TryFire(DecisionLogStep step) => step == at && Interlocked.Exchange(ref _fired, 1) == 0;

    internal async Task HoldAsync()
    {
        Reached.TrySetResult();
        await Release.Task.ConfigureAwait(false);
    }

    internal async Task HoldAtAsync(DecisionLogStep step)
    {
        if (TryFire(step)) await HoldAsync().ConfigureAwait(false);
    }
}

/// <summary>The real decision log with the calls <paramref name="hold"/> names held around their commit; everything reaches the real log unchanged.</summary>
public sealed class HeldDecisionLog(ISupervisorDecisionLog inner, DecisionLogHold hold) : ISupervisorDecisionLog
{
    public async Task<SupervisorDecisionClaim> TryClaimAsync(SupervisorDecisionClaimRequest request, CancellationToken cancellationToken)
    {
        var claim = await inner.TryClaimAsync(request, cancellationToken).ConfigureAwait(false);
        await hold.HoldAtAsync(DecisionLogStep.AfterClaim).ConfigureAwait(false);
        return claim;
    }

    public async Task<bool> TryBeginExecutionAsync(Guid decisionId, Guid teamId, CancellationToken cancellationToken)
    {
        var began = await inner.TryBeginExecutionAsync(decisionId, teamId, cancellationToken).ConfigureAwait(false);
        await hold.HoldAtAsync(DecisionLogStep.AfterBegin).ConfigureAwait(false);
        return began;
    }

    public async Task RecordTerminalAsync(Guid decisionId, Guid teamId, SupervisorDecisionStatus status, string? outcomeJson, string? error, CancellationToken cancellationToken)
    {
        await hold.HoldAtAsync(DecisionLogStep.BeforeTerminal).ConfigureAwait(false);
        await inner.RecordTerminalAsync(decisionId, teamId, status, outcomeJson, error, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SupervisorDecisionRecord>> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) => inner.GetForRunAsync(supervisorRunId, teamId, cancellationToken);

    public Task<IReadOnlyList<SupervisorPriorDecision>> GetTerminalDecisionsAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) => inner.GetTerminalDecisionsAsync(supervisorRunId, teamId, cancellationToken);

    public Task UpdateOutcomeAsync(Guid decisionId, Guid teamId, string foldedOutcomeJson, CancellationToken cancellationToken) => inner.UpdateOutcomeAsync(decisionId, teamId, foldedOutcomeJson, cancellationToken);

    public Task<int> ExpireStalePendingAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) => inner.ExpireStalePendingAsync(olderThan, cancellationToken);
}

/// <summary>
/// The fixture-root decoration of the real decision log, so a test can hold a turn inside the supervisor node's own scope
/// — the engine's walk resolves it from the root container, where a test's child-scope decorator cannot reach. Pass-through
/// for every run nothing is armed for (<see cref="SupervisorDecisionScript.HoldDecisionLog"/>); a hold, once it fires,
/// is disarmed.
/// </summary>
public sealed class ScriptedDecisionLog(ISupervisorDecisionLog inner, SupervisorDecisionScript script, CodeSpaceDbContext db) : ISupervisorDecisionLog
{
    public async Task<SupervisorDecisionClaim> TryClaimAsync(SupervisorDecisionClaimRequest request, CancellationToken cancellationToken)
    {
        var claim = await inner.TryClaimAsync(request, cancellationToken).ConfigureAwait(false);
        await HoldAsync(request.SupervisorRunId, DecisionLogStep.AfterClaim).ConfigureAwait(false);
        return claim;
    }

    public async Task<bool> TryBeginExecutionAsync(Guid decisionId, Guid teamId, CancellationToken cancellationToken)
    {
        var began = await inner.TryBeginExecutionAsync(decisionId, teamId, cancellationToken).ConfigureAwait(false);
        await HoldForDecisionAsync(decisionId, DecisionLogStep.AfterBegin, cancellationToken).ConfigureAwait(false);
        return began;
    }

    public async Task RecordTerminalAsync(Guid decisionId, Guid teamId, SupervisorDecisionStatus status, string? outcomeJson, string? error, CancellationToken cancellationToken)
    {
        await HoldForDecisionAsync(decisionId, DecisionLogStep.BeforeTerminal, cancellationToken).ConfigureAwait(false);
        await inner.RecordTerminalAsync(decisionId, teamId, status, outcomeJson, error, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SupervisorDecisionRecord>> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) => inner.GetForRunAsync(supervisorRunId, teamId, cancellationToken);

    public Task<IReadOnlyList<SupervisorPriorDecision>> GetTerminalDecisionsAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) => inner.GetTerminalDecisionsAsync(supervisorRunId, teamId, cancellationToken);

    public Task UpdateOutcomeAsync(Guid decisionId, Guid teamId, string foldedOutcomeJson, CancellationToken cancellationToken) => inner.UpdateOutcomeAsync(decisionId, teamId, foldedOutcomeJson, cancellationToken);

    public Task<int> ExpireStalePendingAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) => inner.ExpireStalePendingAsync(olderThan, cancellationToken);

    /// <summary>The run a decision belongs to is read only while some hold is armed, so an unarmed fixture pays nothing.</summary>
    private async Task HoldForDecisionAsync(Guid decisionId, DecisionLogStep step, CancellationToken cancellationToken)
    {
        if (!script.AnyDecisionLogHold) return;

        var runId = await db.SupervisorDecisionRecord.AsNoTracking().Where(d => d.Id == decisionId).Select(d => d.SupervisorRunId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        await HoldAsync(runId, step).ConfigureAwait(false);
    }

    private async Task HoldAsync(Guid runId, DecisionLogStep step)
    {
        if (script.DecisionLogHoldFor(runId) is not { } hold || !hold.TryFire(step)) return;

        script.DisarmDecisionLogHold(runId);
        await hold.HoldAsync().ConfigureAwait(false);
    }
}
