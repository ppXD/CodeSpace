using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Capture;

/// <summary>
/// The launch half of the durable process record, as a SIBLING of <see cref="INativeRecordPlane"/> (Rule 7) rather
/// than a widening of it: FIND the attempt a recovery must adopt a physical execution by.
///
/// <para><b>Why a launch-side reader exists at all.</b> The attempt row is written BEFORE the process exists, so its
/// presence proves an intent and not an admission. Between the runner admitting an execution and the executor making
/// that execution reachable — the app-side handle write — there is a window in which the process is real and nothing
/// reachable says so. A crash there used to terminalize the run and reclaim the clone a live agent was working in.
/// What closes that window is not a new fact but the one already on the row: team, run, execution and the attempt's
/// own id ARE the launch identity, its spool address is already in the locator the launch wrote, and the runner that
/// owns both is named by the parent execution. So this plane only READS what the open already recorded.
/// </para>
///
/// <para><b>Why nothing is written here.</b> 0137 also models an observer claim on the attempt, and taking it at
/// launch looks like the obvious way to record "this worker owns a live process". It is not, and a first cut of this
/// plane proved it: <c>ck_workflow_run_harness_process_attempt_terminal_claim</c> demands
/// <c>claim_owner_id IS NULL</c> for every terminal state, while the row's guard trigger refuses to release a LIVE
/// claim in the same statement that records a process outcome. Neither closer — <see cref="INativeRecordPlane"/>'s
/// own <c>CloseAsync</c> nor the execution plane's attempt close — releases a claim, so a claim taken at launch makes
/// the attempt permanently unclosable: EVERY native attempt on the happy path, with the constraint violation
/// swallowed by the best-effort close. A claim here would therefore need its own two-statement release protocol
/// (release under <c>WHERE claim_owner_id = @me AND claim_fence = @observed AND revision = @observed</c>, THEN record
/// the outcome) wired into every closer and every abandon path before it could buy anything. It is deliberately NOT
/// built and NOT re-added here: no reader consults <c>claim_owner_id</c> or <c>claim_fence</c> on this table, and
/// adoption addresses an execution by the identity columns below, which exist whether or not a claim was ever taken.
/// </para>
///
/// <para><b>What it still is not.</b> Reading the row is bookkeeping about reachability, never authority over an
/// outcome. Nothing here decides a run's status, and an attempt whose address or runner cannot be read is reported as
/// unadoptable rather than guessed at.</para>
/// </summary>
public interface INativeRecordLaunchPlane
{
    /// <summary>
    /// The live process attempt a recovery would adopt this run's execution by — its exact launch identity, the spool
    /// address the launch recorded, and the runner kind that owns both. Null ⇒ this run has no live recorded attempt
    /// whose execution can be addressed, so there is nothing here to adopt and nothing to be inferred about a process
    /// from its absence.
    /// </summary>
    Task<AdmittedLaunch?> FindAdmittedLaunchAsync(Guid agentRunId, CancellationToken cancellationToken);
}

public sealed partial class NativeRecordPlane : INativeRecordLaunchPlane
{
    public async Task<AdmittedLaunch?> FindAdmittedLaunchAsync(Guid agentRunId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        // The newest live attempt: a revise round appends the next process, and only the last one can still be running.
        // The runner kind rides along from the parent execution, which is the row that owns it — routing an adoption to
        // whichever registered runner happens to answer first would hand one backend's spool address to another.
        var live = await db.WorkflowRunHarnessProcessAttempt.AsNoTracking()
            .Where(attempt => attempt.AgentRunId == agentRunId && attempt.State == HarnessProcessAttemptState.Running)
            .OrderByDescending(attempt => attempt.AttemptOrdinal)
            .Select(attempt => new { attempt.Id, attempt.TeamId, attempt.ExecutionId, attempt.RunnerLocatorJson, attempt.Execution.RunnerKind })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (live is null) return null;

        var spoolKey = NativeLaunchLocator.SpoolKeyOf(live.RunnerLocatorJson);

        if (spoolKey is null)
        {
            _logger.LogWarning("Live process attempt {AttemptId} of agent run {RunId} records no spool key, so its admitted execution cannot be addressed for adoption", live.Id, agentRunId);

            return null;
        }

        if (string.IsNullOrWhiteSpace(live.RunnerKind))
        {
            _logger.LogWarning("Live process attempt {AttemptId} of agent run {RunId} names no runner kind on its execution, so no runner can be asked to re-discover its admitted execution", live.Id, agentRunId);

            return null;
        }

        return new AdmittedLaunch(new SandboxLaunchIdentity(live.TeamId, agentRunId, live.ExecutionId, live.Id), spoolKey, live.RunnerKind);
    }
}
