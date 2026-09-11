using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Recovery;

namespace CodeSpace.Core.Services.Agents.Recovery;

/// <summary>
/// PURE planner: what an abandon must SAY about the resources of a run, given only the run's durable handle. No I/O,
/// so the one piece of judgement in this slice — which resources a run really held, and which of them a foreign host
/// is entitled to claim anything about — is unit-testable without a database, a filesystem, or a kernel.
///
/// <para><b>The claim a foreign abandon is allowed to make is exactly one: "still there, on that host."</b> Every
/// resource named here is host-local (a directory, an inode, a netns, a cgroup leaf, a clone), so a worker that did
/// not launch the run cannot free any of them and must not pretend it did. The pre-existing abandon path DID pretend:
/// it ran the local netns / cgroup teardowns against keys naming nothing in its own namespaces and swallowed the miss
/// as a best-effort warning.</para>
/// </summary>
public static class RunCleanupReceipts
{
    /// <summary>
    /// The receipts for an abandon performed on a host that did NOT launch the run. Every host-local resource the
    /// handle names is <see cref="RunResourceOutcome.Orphaned"/> against <see cref="SandboxHandle.LaunchHost"/>; a
    /// resource the handle does not name gets no row, because a row is a claim that something real is standing there.
    ///
    /// <para>Two kinds are not about hosts at all. The log-capture promises are reported only when this abandon
    /// actually settled some — <paramref name="logSegmentsTerminalized"/> greater than zero — exactly like the
    /// same-host branch (<c>AgentRunReconcilerService.ReclaimLocalIsolationAsync</c>): a sweep that moved no intent
    /// knows there was nothing outstanding, so it has nothing to say and writes no row. The injected model credential
    /// is permanently <see cref="RunResourceOutcome.Unknown"/>: the agent may have been mid-call when its host died,
    /// and no sweep anywhere can establish otherwise.</para>
    /// </summary>
    public static IReadOnlyList<RunCleanupReceipt> ForForeignAbandon(SandboxHandle handle, RunCleanupStamp stamp, int logSegmentsTerminalized)
    {
        var owner = handle.LaunchHost;
        if (string.IsNullOrWhiteSpace(owner)) return Array.Empty<RunCleanupReceipt>();   // an unstamped legacy handle names no owner to address, and inventing one would address nobody

        var receipts = new List<RunCleanupReceipt> { stamp.Orphaned(RunResourceKind.Spool, owner, handle.SpoolDirectory) };

        if (handle.McpSocketPath is { Length: > 0 } socket) receipts.Add(stamp.Orphaned(RunResourceKind.McpSocket, owner, socket));
        if (handle.EgressNetnsKey is { Length: > 0 } netns) receipts.Add(stamp.Orphaned(RunResourceKind.EgressSubnet, owner, netns));
        if (handle.CgroupRunKey is { Length: > 0 } cgroup) receipts.Add(stamp.Orphaned(RunResourceKind.Cgroup, owner, cgroup));
        if (handle.WorkspaceDirectory is { Length: > 0 } workspace) receipts.Add(stamp.Orphaned(RunResourceKind.Workspace, owner, workspace));
        if (handle.InjectedKeyFingerprint is { Length: > 0 }) receipts.Add(stamp.Unknown(RunResourceKind.ProviderCredentialLease, owner, null, CredentialUnknowableCode));

        if (logSegmentsTerminalized > 0) receipts.Add(stamp.Completed(RunResourceKind.LogSegments, owner, null));

        return receipts;
    }

    /// <summary>The credential lease's permanent error code — it names an unknowability, not a failure to try.</summary>
    public const string CredentialUnknowableCode = "host-died-mid-use-possible";

    /// <summary>Recorded when the sweeping host cannot even attempt a teardown (no netns / cgroup-v2 support, or no delegated cgroup root) — never <see cref="RunResourceOutcome.Compensated"/>, which would claim a reclaim that never ran.</summary>
    public const string UnsupportedCode = "unsupported";
}

/// <summary>
/// The run identity every receipt of ONE abandon (or one sweep) shares, so a caller states it once instead of on
/// every row. Lives beside the planner because the planner is its first caller; the reconciler's SAME-host branch and
/// the orphan sweep are the others.
/// </summary>
public sealed record RunCleanupStamp(Guid AgentRunId, long FenceEpoch, string RecordedByHost, DateTimeOffset RecordedAt)
{
    /// <summary>The resource is still standing on <paramref name="ownerHost"/> and this host cannot reach it.</summary>
    public RunCleanupReceipt Orphaned(RunResourceKind kind, string ownerHost, string? resourceKey) =>
        Build(kind, RunResourceOutcome.Orphaned, ownerHost, resourceKey, null);

    /// <summary>Cleanup ran here, at the run's end, and succeeded.</summary>
    public RunCleanupReceipt Completed(RunResourceKind kind, string? ownerHost, string? resourceKey) =>
        Build(kind, RunResourceOutcome.Completed, ownerHost, resourceKey, null);

    /// <summary>Nobody can say what became of it — <paramref name="errorCode"/> says why the question is open.</summary>
    public RunCleanupReceipt Unknown(RunResourceKind kind, string? ownerHost, string? resourceKey, string errorCode) =>
        Build(kind, RunResourceOutcome.Unknown, ownerHost, resourceKey, errorCode);

    private RunCleanupReceipt Build(RunResourceKind kind, RunResourceOutcome outcome, string? ownerHost, string? resourceKey, string? errorCode) => new()
    {
        AgentRunId = AgentRunId, FenceEpoch = FenceEpoch, Kind = kind, Outcome = outcome,
        OwnerHost = ownerHost, ResourceKey = resourceKey, RecordedByHost = RecordedByHost, RecordedAt = RecordedAt, ErrorCode = errorCode,
    };
}
