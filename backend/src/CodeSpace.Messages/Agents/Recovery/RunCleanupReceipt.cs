namespace CodeSpace.Messages.Agents.Recovery;

/// <summary>
/// One class of host resource an agent run occupies while it is alive — the unit a cleanup receipt is written PER, so
/// "the run was cleaned up" can never be one opaque boolean over resources that live in different places and are
/// reclaimed by different tools. Every kind here is HOST-LOCAL: the spool is a directory, the netns / cgroup are
/// kernel objects, the socket is an inode, the workspace is a clone. That is exactly why a run abandoned from a
/// DIFFERENT host cannot clean any of them up, and why each one needs its own row saying so.
/// </summary>
public enum RunResourceKind
{
    /// <summary>The run's spool directory (<c>out.log</c> / <c>err.log</c> / <c>exit</c> / <c>pid</c>) and the whole revise-round family beside it.</summary>
    Spool,

    /// <summary>The run's durable log-capture promises — the intents that must reach a terminal health state, or the run's log story stays an open question.</summary>
    LogSegments,

    /// <summary>The Unix-domain socket the run's tool-fabric endpoint was bound on (inside the spool, but named separately because its reader is the agent, not the operator).</summary>
    McpSocket,

    /// <summary>The filtered-egress network namespace and, with it, the host-global subnet lease the allocator handed this run. The scarcest of these resources: the lease space is bounded.</summary>
    EgressSubnet,

    /// <summary>The cgroup-v2 resource-cap leaf the run was launched inside.</summary>
    Cgroup,

    /// <summary>The primary repo's on-disk clone directory the run worked in.</summary>
    Workspace,

    /// <summary>The model credential the launch injected into the sandbox env. Never reclaimable by a sweep — see <see cref="RunResourceOutcome.Unknown"/>.</summary>
    ProviderCredentialLease,
}

/// <summary>
/// What became of one <see cref="RunResourceKind"/> for one run. Two of these are terminal-good and two are open, and
/// the difference is the whole point: an <see cref="Orphaned"/> row is a claim that something REAL is still sitting on
/// a named host, addressed to the sweep that runs there.
/// </summary>
public enum RunResourceOutcome
{
    /// <summary>Cleanup ran on the host that owned the resource, at the moment the run ended, and succeeded. Terminal.</summary>
    Completed,

    /// <summary>The resource was orphaned first and a later sweep ON ITS OWNING HOST reclaimed it. Terminal, and deliberately distinct from <see cref="Completed"/>: the run DID leave something behind for a while, and that fact survives the repair.</summary>
    Compensated,

    /// <summary>The resource is still there and this host cannot touch it. REQUIRES <c>OwnerHost</c> — an orphan nobody can address is the silence this whole table exists to break.</summary>
    Orphaned,

    /// <summary>Nobody can say. A teardown this host cannot even attempt (no kernel support), or a resource whose state is unknowable in principle — an injected credential may have been mid-use when the host died, and no sweep can prove otherwise.</summary>
    Unknown,
}

/// <summary>
/// A typed statement about ONE resource of ONE agent run: what it was, who owns it, and what became of it. Written on
/// every abandon — including, and especially, the abandon that happens on a host that owns none of the run's
/// resources, where the pre-existing code ran the local teardowns anyway and logged "best-effort … failed" when they
/// did nothing.
///
/// <para>The identity is (<see cref="AgentRunId"/>, <see cref="Kind"/>, <see cref="ResourceKey"/>) — one row per
/// resource, upserted, never appended, so a run's cleanup story is a small readable set rather than a log to
/// reconstruct. <see cref="FenceEpoch"/> records which generation of the run wrote the statement; it is evidence for
/// a reader, not a concurrency token (the monotonic outcome rule is what orders concurrent writers).</para>
/// </summary>
public sealed record RunCleanupReceipt
{
    /// <summary>The agent run whose resource this is.</summary>
    public required Guid AgentRunId { get; init; }

    /// <summary>The run's fence epoch at the moment the statement was made — which generation of the run left this behind.</summary>
    public required long FenceEpoch { get; init; }

    public required RunResourceKind Kind { get; init; }
    public required RunResourceOutcome Outcome { get; init; }

    /// <summary>The host the resource physically lives on (the run's <c>LaunchHost</c>). Mandatory for <see cref="RunResourceOutcome.Orphaned"/> — that outcome is addressed TO a host — and otherwise the host that owned it, when known.</summary>
    public string? OwnerHost { get; init; }

    /// <summary>The teardown handle a sweep on <see cref="OwnerHost"/> needs: a spool path, a netns key, a cgroup key, a socket path, a clone directory. Null when the kind has no key (or the run carried none).</summary>
    public string? ResourceKey { get; init; }

    /// <summary>The host that WROTE this statement — the reconciling or sweeping worker, which for an orphan is precisely not <see cref="OwnerHost"/>.</summary>
    public required string RecordedByHost { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>Why the outcome is not terminal-good — the reason a teardown failed or could not be attempted (e.g. <c>unsupported</c>). Null on a clean outcome.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>True for the two outcomes nothing may overwrite: the resource is provably gone, so a later sweep's "still orphaned" read is stale by construction.</summary>
    public bool IsSettled => Outcome is RunResourceOutcome.Completed or RunResourceOutcome.Compensated;
}
