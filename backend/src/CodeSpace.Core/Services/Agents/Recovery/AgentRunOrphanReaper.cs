using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents.Recovery;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Recovery;

/// <summary>
/// The other half of a cross-host abandon: the sweep that runs ON the host an orphan receipt is addressed to, and
/// settles it. A receipt written by a foreign reconciler is a claim ("this netns / this spool is still standing on
/// host A"); this is the only component that can answer it, because every resource named is host-local.
///
/// <para><b>It reclaims what nothing else will, and OBSERVES what something else owns.</b> The egress netns and the
/// cgroup leaf are torn down here and now: the netns teardown is also what returns the host-global subnet lease to a
/// BOUNDED pool, and for an abandoned run nothing else on this host will reach them until the spool reaper's 24h
/// retention elapses — if it ever does. The spool, the MCP socket inside it, and the workspace clone are NOT deleted
/// here: they belong to the spool reaper's retention policy and the workspace janitor's age policy, which exist to
/// keep a raw-output recovery source and a re-attachable clone alive for a while, and an orphan receipt is not
/// authority to pre-empt either. For those, this sweep checks whether the path is gone and flips the row to
/// <see cref="RunResourceOutcome.Compensated"/> when it is — so "the orphan was reclaimed" becomes a recorded fact
/// rather than an assumption, and a resource still standing keeps saying so.</para>
///
/// <para>Host-local by construction: it asks the ledger only for orphans whose <c>OwnerHost</c> is THIS host, so on a
/// multi-worker deployment each replica settles its own and none of them guesses about another's.</para>
/// </summary>
public interface IAgentRunOrphanReaper
{
    /// <summary>Settle this host's outstanding orphan receipts. Returns how many resources were RECLAIMED (<see cref="RunResourceOutcome.Compensated"/>); a row this sweep could not settle keeps its orphan claim.</summary>
    Task<int> ReapAsync(CancellationToken cancellationToken);
}

public sealed class AgentRunOrphanReaper : IAgentRunOrphanReaper, IScopedDependency
{
    /// <summary>Per-sweep cap so a fleet-wide host loss can't run one tick forever; the next tick continues from the oldest remaining.</summary>
    public const int BatchSize = 200;

    private readonly IRunCleanupLedger _cleanup;
    private readonly ILogger<AgentRunOrphanReaper> _logger;

    public AgentRunOrphanReaper(IRunCleanupLedger cleanup, ILogger<AgentRunOrphanReaper> logger)
    {
        _cleanup = cleanup;
        _logger = logger;
    }

    public async Task<int> ReapAsync(CancellationToken cancellationToken)
    {
        var host = LocalProcessRunner.CurrentHost;
        var orphans = await _cleanup.OrphanedOnHostAsync(host, BatchSize, cancellationToken).ConfigureAwait(false);
        var compensated = 0;

        foreach (var orphan in orphans)
            if (await SettleAsync(orphan, host, cancellationToken).ConfigureAwait(false)) compensated++;

        if (compensated > 0)
            _logger.LogInformation("AgentRunOrphanReaper: reclaimed {Compensated} orphaned resource(s) left on host {Host} by run(s) abandoned elsewhere", compensated, host);

        return compensated;
    }

    /// <summary>Settle one receipt: attempt or observe, then write the answer. Returns true only when the resource was actually reclaimed here.</summary>
    private async Task<bool> SettleAsync(RunCleanupReceipt orphan, string host, CancellationToken cancellationToken)
    {
        if (await AnswerFor(orphan, cancellationToken).ConfigureAwait(false) is not { } answer) return false;

        var settled = orphan with { Outcome = answer.Outcome, RecordedByHost = host, RecordedAt = DateTimeOffset.UtcNow, ErrorCode = answer.ErrorCode };

        return await UpsertQuietlyAsync(settled, cancellationToken).ConfigureAwait(false) && answer.Outcome == RunResourceOutcome.Compensated;
    }

    /// <summary>Write one settled receipt, swallowing a ledger failure so a transient DB error never aborts the rest of the batch — the row keeps its prior orphan claim and the next sweep retries it.</summary>
    private async Task<bool> UpsertQuietlyAsync(RunCleanupReceipt receipt, CancellationToken cancellationToken)
    {
        try
        {
            await _cleanup.UpsertAsync(receipt, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "AgentRunOrphanReaper: could not record the {Kind} cleanup receipt for agent run {RunId}; it stays orphaned for the next sweep", receipt.Kind, receipt.AgentRunId);
            return false;
        }
    }

    /// <summary>What this host can say about one orphaned resource, or null when it has nothing to add and the standing orphan claim is still the truest answer.</summary>
    private async Task<Answer?> AnswerFor(RunCleanupReceipt orphan, CancellationToken cancellationToken) => orphan.Kind switch
    {
        RunResourceKind.EgressSubnet => await TearDownEgressNetnsAsync(orphan, cancellationToken).ConfigureAwait(false),
        RunResourceKind.Cgroup => await TearDownCgroupAsync(orphan, cancellationToken).ConfigureAwait(false),
        RunResourceKind.Spool or RunResourceKind.McpSocket or RunResourceKind.Workspace => ObservePath(orphan),
        _ => null,   // LogSegments / ProviderCredentialLease are never minted as orphans — neither is a host-local resource
    };

    /// <summary>Tear down the netns (and with it release the host-global subnet lease). An unsupported host must answer <see cref="RunResourceOutcome.Unknown"/>, never Compensated — a reclaim that could not be attempted is not a reclaim.</summary>
    private async Task<Answer?> TearDownEgressNetnsAsync(RunCleanupReceipt orphan, CancellationToken cancellationToken)
    {
        if (orphan.ResourceKey is not { Length: > 0 } key) return null;
        if (!FilteredEgressNetns.IsSupported) return new Answer(RunResourceOutcome.Unknown, RunCleanupReceipts.UnsupportedCode);

        try
        {
            await FilteredEgressNetns.TeardownAsync(key, cancellationToken).ConfigureAwait(false);
            return new Answer(RunResourceOutcome.Compensated, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "AgentRunOrphanReaper: egress-netns teardown for orphaned run {RunId} failed; it stays orphaned for the next sweep", orphan.AgentRunId);
            return new Answer(RunResourceOutcome.Orphaned, AgentRunReconcilerService.TeardownFailedCode);
        }
    }

    /// <summary>The cgroup parallel. A kernel with no cgroup-v2 support can never attempt this, so it answers Unknown. An unconfigured delegated root is different — an operator can set <c>Sandbox:CgroupRoot</c> later — so that case leaves the row Orphaned for a future, configured sweep rather than de-queuing it as Unknown.</summary>
    private async Task<Answer?> TearDownCgroupAsync(RunCleanupReceipt orphan, CancellationToken cancellationToken)
    {
        if (orphan.ResourceKey is not { Length: > 0 } key) return null;
        if (!CgroupResourceLimit.IsSupported) return new Answer(RunResourceOutcome.Unknown, RunCleanupReceipts.UnsupportedCode);
        if (CgroupResourceLimit.CgroupRoot is not { } root) return null;   // cgroup-root-unconfigured: a config gap, not a kernel limit

        try
        {
            await CgroupResourceLimit.TeardownAsync(root, key, cancellationToken).ConfigureAwait(false);
            return new Answer(RunResourceOutcome.Compensated, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "AgentRunOrphanReaper: cgroup teardown for orphaned run {RunId} failed; it stays orphaned for the next sweep", orphan.AgentRunId);
            return new Answer(RunResourceOutcome.Orphaned, AgentRunReconcilerService.TeardownFailedCode);
        }
    }

    /// <summary>
    /// Observe a path another reaper owns. Gone means the owning policy has reclaimed it, which is exactly
    /// <see cref="RunResourceOutcome.Compensated"/> — the run DID leave it behind, and a later sweep on its own host
    /// found it repaired. Still there means the standing orphan claim remains the truest thing anyone can say, so
    /// nothing is written (rewriting it every tick would only churn the timestamp).
    /// </summary>
    private static Answer? ObservePath(RunCleanupReceipt orphan)
    {
        if (orphan.ResourceKey is not { Length: > 0 } path) return null;

        try { return Path.Exists(path) ? null : new Answer(RunResourceOutcome.Compensated, null); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;   // a path this host cannot even stat is not a path it may declare gone
        }
    }

    /// <summary>One host's answer about one orphaned resource — the outcome to record and the code that says why it is not clean.</summary>
    private readonly record struct Answer(RunResourceOutcome Outcome, string? ErrorCode);
}
