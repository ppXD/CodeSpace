using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Credentials;

/// <summary>
/// Fronts a run's model credential instead of handing it over. The gap it closes: the provider API key is a
/// LONG-LIVED third-party credential — it outlives the run, the worker and usually the deployment — and putting it in
/// the CLI's environment made every agent process a holder of it. Nothing could then withdraw it: the only revocation
/// available was killing the process, so a lease that lapsed while the worker was alive left a CLI happily spending
/// the tenant's key, and a key echoed by a CLI (a 401 body, an init banner) had already left the building.
///
/// <para>A broker turns that into a capability with a lifetime: the run is given a base URL on this worker plus a
/// per-run bearer, the real key stays in the broker's in-memory lease, and every upstream call is authenticated
/// server-side. <see cref="RevokeAsync"/> then means what it says, and it lands BEFORE the kill — the difference
/// between "the agent will stop spending once the signal arrives" and "the agent cannot spend, whatever it does with
/// the seconds it has left".</para>
///
/// <para><b>Its own concern folder, not <c>ModelCredentials/</c>.</b> That folder is the CATALOG — which credentials
/// a team has, which models they carry, which one a run resolves to. This is the BROKERAGE: holding one already
/// resolved credential on one run's behalf for a bounded window. They compose (the resolver picks the row, the broker
/// fronts it) and neither is a part of the other, so Rule 18.2 puts them side by side.</para>
///
/// <para><b>What it deliberately is not.</b> It brokers the MODEL credential only. A run's other third-party
/// credentials (npm, pypi, a cloud CLI) still reach the sandbox however they always did, and an in-flight upstream
/// request is not interrupted by a revocation — the lease gates the NEXT call, not the stream already open.</para>
/// </summary>
public interface IModelCredentialBroker
{
    /// <summary>
    /// Open (or REPLACE, at a higher epoch) the run's lease and return what the harness should be given instead of
    /// the key. Null when this deployment cannot broker the credential at all — no listener could be bound, or the
    /// credential names no upstream endpoint to forward to — which the caller must treat as a decision point, never
    /// as a silent fall-through: a deployment that mandates confinement refuses the run, and one that does not
    /// injects directly AND discloses that it did.
    /// </summary>
    Task<BrokeredModelCredential?> OpenAsync(ModelCredentialLeaseRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Extend the run's lease by its TTL. False when there is nothing to extend — no lease (already revoked, or held
    /// by another worker) or one opened under a DIFFERENT epoch, which is exactly the reclaimed-run case a superseded
    /// worker must not be able to keep alive. Never throws: a renewal is advisory, and the caller is a heartbeat.
    /// </summary>
    Task<bool> RenewAsync(Guid runId, long epoch, CancellationToken cancellationToken);

    /// <summary>
    /// Withdraw the run's lease NOW: the next call presenting its token is refused. Idempotent, and a no-op for a run
    /// this worker never brokered (the lease is process-local, so a cancel issued by another worker relies instead on
    /// the owning worker's heartbeat stopping — the lease then lapses within its TTL). Never throws:
    /// callers are teardown paths whose outcome must not depend on it.
    /// </summary>
    Task RevokeAsync(Guid runId, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// The lease WINDOW — how long a brokered credential answers without a renewal, expressed against the cadence that
/// renews it. Lives beside the abstraction rather than inside the implementation because the caller passes it
/// (<see cref="ModelCredentialLeaseRequest.Ttl"/>) and a second broker must honour the same window.
/// </summary>
public static class ModelCredentialLease
{
    /// <summary>
    /// How many heartbeat intervals of silence a lease survives. Two, for the same reason
    /// <see cref="AgentRunLiveness.HeartbeatInterval"/> is a THIRD of the liveness window: a single lost ping is a
    /// transient DB blip, not a dead worker, and a lease that died on one would 401 a perfectly live agent mid-run.
    /// Two also keeps the lease strictly SHORTER than the reconciler's own abandon window, so a run whose worker is
    /// gone stops being able to spend before anything else has even declared it stale. Pinned by a test — raising it
    /// widens the window in which a dead worker's agent keeps billing the tenant's key.
    /// </summary>
    public const int TtlHeartbeats = 2;

    /// <summary>The lease TTL every caller passes. A PROPERTY, not a captured constant: <see cref="AgentRunLiveness.HeartbeatInterval"/> is operator-tunable at run time, and a TTL frozen at type-load would not track it.</summary>
    public static TimeSpan Ttl => AgentRunLiveness.HeartbeatInterval * TtlHeartbeats;
}
