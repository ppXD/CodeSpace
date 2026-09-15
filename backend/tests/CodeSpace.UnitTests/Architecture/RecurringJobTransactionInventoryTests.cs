using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeSpace.Core;
using CodeSpace.Core.Jobs;
using CodeSpace.Messages.Mediation;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Fail-closed floor for the OTHER half of the pipeline every recurring job passes through: the transaction.
/// <c>TransactionalBehavior</c> wraps every <c>ICommand&lt;T&gt;</c> in one transaction, which is right for a
/// command that writes one coherent unit of work and wrong for a system sweep — a bounded batch of independent
/// rows, each settled by its own fenced CAS write, each failure caught and retried next tick. Wrapping a sweep
/// makes the batch atomic, so one unrecoverable row undoes every other row's recovery.
///
/// <para>Why pin it: that is not a hypothetical. The agent-run reconciler inherited the wrapper silently, and its
/// wait-recovery step refuses to run under an ambient transaction — so from the day that guard landed, every
/// minutely tick threw at its last step and the behavior rolled the WHOLE sweep back. Nothing in the diff said
/// "this command is now transactional"; the marker's absence looked exactly like every other command's.</para>
///
/// <para>So each recurring job's command must either carry <see cref="INonTransactionalCommand"/> or be named in
/// <see cref="AtomicByDesign"/> with the reason one transaction is the right unit for it. The command each job
/// dispatches is observed by invoking the real <c>Execute</c> against a recording mediator, so a job that changes
/// which command it sends is re-measured rather than re-assumed.</para>
/// </summary>
[Trait("Category", "Unit")]
public class RecurringJobTransactionInventoryTests
{
    /// <summary>
    /// Sweeps whose tick IS one coherent unit of work, so a single transaction is the right boundary: they write
    /// little, own no per-row fence, and a mid-tick failure is better undone than half-applied. Every entry names
    /// why — "it's only a sweep" is not a reason, since that is exactly what the reconciler looked like too.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AtomicByDesign = new Dictionary<string, string>
    {
        ["CleanupExpiredOAuthStatesCommand"] = "one bounded DELETE of rows already past their expiry; there are no per-row decisions to preserve.",
        ["WarnUnrotatedBootstrapPasswordsCommand"] = "reads and logs a warning; it writes nothing, so the transaction wraps nothing.",
        ["SweepStaleAgentWorkspacesCommand"] = "a filesystem walk that touches no table at all; there is nothing for a rollback to undo.",
        ["ProjectWorkflowRunToolCallsCommand"] = "it WANTS one transaction: its row locks and sequenced saves are a single coherent projection, and it already opens one itself when none is open.",
        ["ProjectWorkflowRunModelCallsCommand"] = "the same projection shape — several passes that must land together or not at all.",
        ["ExpireStaleToolCallsCommand"] = "a bounded batch of status-guarded CAS updates with no lease of its own to strand: an all-or-nothing rollback loses nothing the next tick will not redo.",
        ["ExpireStaleSupervisorDecisionsCommand"] = "the same shape — guarded CAS updates, no fence, no per-row catch to preserve.",
        ["ExpireStaleToolApprovalsCommand"] = "the same shape, and its follow-ups are deliberately deferred to the post-commit drain because a transaction is expected.",
        ["ExpireStaleDecisionsCommand"] = "the same shape, delegating to the same guarded ledger CAS.",
        ["FireDueScheduleTriggersCommand"] = "a rollback loses no trigger: the lookback window re-fires them and their idempotency keys roll back with the runs, so nothing double-fires.",
    };

    [Fact]
    public void Every_recurring_job_command_is_non_transactional_or_allow_listed()
    {
        var offenders = DispatchedCommands()
            .Where(c => !typeof(INonTransactionalCommand).IsAssignableFrom(c.Command))
            .Where(c => !AtomicByDesign.ContainsKey(c.Command.Name))
            .Select(c => $"{c.Job.Name} → {c.Command.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "these recurring-job commands silently inherit TransactionalBehavior's one-transaction-per-command wrapper. " +
            $"If the sweep settles independent rows with its own fenced CAS writes and quiet per-row failure, mark it " +
            $"{nameof(INonTransactionalCommand)}. If its tick really is one coherent unit of work, add it to " +
            $"{nameof(AtomicByDesign)} with the reason:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_allow_list_does_not_rot()
    {
        var dispatched = DispatchedCommands().Select(c => c.Command).ToList();

        foreach (var name in AtomicByDesign.Keys)
        {
            var command = dispatched.SingleOrDefault(c => c.Name == name);

            command.ShouldNotBeNull($"allow-listed command '{name}' is no longer dispatched by any recurring job — remove it");
            typeof(INonTransactionalCommand).IsAssignableFrom(command!).ShouldBeFalse($"allow-listed command '{name}' now declares {nameof(INonTransactionalCommand)} — remove it from the allow-list");
        }

        foreach (var (name, reason) in AtomicByDesign)
            reason.ShouldNotBeNullOrWhiteSpace($"allow-listed command '{name}' must name why one transaction is the right unit for it");
    }

    /// <summary>Every (job, command) pair, measured by running the real <c>Execute</c> against a recording mediator — the jobs are thin dispatchers, so this observes what is actually sent.</summary>
    private static IReadOnlyList<(Type Job, Type Command)> DispatchedCommands()
    {
        var jobs = typeof(CodeSpaceModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IRecurringJob).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        jobs.ShouldNotBeEmpty("the reflection scan found no recurring job — every check in this class would pass vacuously");

        return jobs.Select(DispatchedCommand).ToList();
    }

    private static (Type Job, Type Command) DispatchedCommand(Type jobType)
    {
        var parameters = jobType.GetConstructors().Single().GetParameters();

        parameters.Select(p => p.ParameterType).ShouldBe([typeof(IMediator)],
            $"{jobType.Name} must take IMediator and nothing else (Rule 14 — a thin Mediator dispatcher). This scan " +
            "constructs each job by hand to observe what it dispatches, and cannot supply anything else.");

        var recorder = new RecordingMediator();
        var job = (IRecurringJob)Activator.CreateInstance(jobType, recorder)!;

        job.Execute().GetAwaiter().GetResult();

        recorder.Sent.Count.ShouldBe(1, $"{jobType.Name} must send exactly one command; it sent {recorder.Sent.Count}");

        return (jobType, recorder.Sent[0]);
    }

    /// <summary>Records the request types a job sends and answers every call with a default — the jobs discard the response, so nothing here has to be real.</summary>
    private sealed class RecordingMediator : IMediator
    {
        public List<Type> Sent { get; } = new();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request.GetType());
            return Task.FromResult(default(TResponse)!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Sent.Add(typeof(TRequest));
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request.GetType());
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}
