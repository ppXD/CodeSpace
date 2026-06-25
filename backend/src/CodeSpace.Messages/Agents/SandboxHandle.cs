namespace CodeSpace.Messages.Agents;

/// <summary>
/// A durable reference to a launched sandbox process — enough to re-find, observe, and recover it WITHOUT
/// the launching process still being alive. This is the "runner handle" persisted on the agent-run row
/// (<c>agent_run.runner_handle</c>) the instant a run is launched, so a backend that restarts mid-run can
/// re-attach to (or recover) the run by reading the handle rather than abandoning it.
///
/// <para>It is deliberately backend-neutral: the local runner records an OS pid + an on-disk spool, a
/// future container runner would record a container id + log stream — both fit the same envelope. The
/// fields here are the local runner's; richer backends add their own via the jsonb column without a
/// migration.</para>
///
/// <para><see cref="ProcessId"/> is the SUPERVISOR process (the <c>/bin/sh</c> wrapper that owns the spool
/// redirection + writes the exit marker), not necessarily the harness CLI itself — probing it tells us
/// whether the run is still alive. On Linux the supervisor leads its own session (launched via <c>setsid</c>)
/// so it outlives a graceful-shutdown signal aimed at the API's process group, which is why probing it after
/// a restart is meaningful. <see cref="SpoolDirectory"/> is the resolved absolute directory holding
/// <c>out.log</c> / <c>err.log</c> / <c>exit</c>. <see cref="Deadline"/> is the absolute wall-clock cap
/// (launch time + the spec timeout); the observer terminates the process once it passes, so the timeout
/// survives a re-attach by a different observer.</para>
/// </summary>
public sealed record SandboxHandle
{
    /// <summary>Runner kind that owns this handle (e.g. "local"). Matches <c>ISandboxRunner.Kind</c> so a reader resolves the right runner to interpret it.</summary>
    public required string Kind { get; init; }

    /// <summary>OS pid of the supervisor process owning the spool redirection. Probing it (e.g. <c>kill -0</c>) distinguishes a live run from a crashed one.</summary>
    public required int ProcessId { get; init; }

    /// <summary>
    /// The supervisor's process start time (UTC), recorded at launch as a PID-reuse guard: a probe across a
    /// backend restart compares it against the live process holding <see cref="ProcessId"/>, so a recycled pid
    /// (the OS handed our old number to an unrelated process) is treated as "gone" rather than mistaken for our
    /// run. Null for an older handle written before this guard existed, or when the host can't report it — the
    /// guard is then skipped (the probe falls back to liveness alone).
    /// </summary>
    public DateTimeOffset? ProcessStartTimeUtc { get; init; }

    /// <summary>Resolved absolute path to the run's spool directory holding <c>out.log</c>, <c>err.log</c>, and the <c>exit</c> marker.</summary>
    public required string SpoolDirectory { get; init; }

    /// <summary>Absolute wall-clock deadline (launch + the spec's timeout). The observer terminates the process once <c>now</c> passes it — surviving a re-attach by a new observer.</summary>
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>
    /// The stdout-spool byte offset a re-attaching observer resumes tailing from — checkpointed (advanced) as
    /// the live observer emits each batch, so a new observer after a backend restart resumes where the dead one
    /// stopped rather than re-emitting the whole spool (which would duplicate the append-only event log). 0 =
    /// from the start, and the value an older handle written before this field deserializes to (so a re-attach
    /// of a pre-existing run safely replays from the beginning). The checkpoint is written AFTER the batch's
    /// events persist, so it never runs ahead of the log — a re-attach at worst re-emits the last batch, never
    /// loses lines (exactly-once is a later slice).
    /// </summary>
    public long StdoutOffset { get; init; }

    /// <summary>
    /// A NON-REVERSIBLE fingerprint of the secret(s) the launching run injected into the sandbox env (null when
    /// the run injected none). A re-attaching observer rebuilds its redactor from the run's CURRENT credential
    /// and compares: only when the fingerprint MATCHES has it provably reconstructed the same key that masked the
    /// original output, so it's safe to re-tail the spool (which may echo that key). A mismatch — the credential
    /// was deleted or rotated since launch, or the team default changed — means it can no longer mask the echoed
    /// key, so it must complete from the exit marker only rather than freeze an unmaskable secret into the
    /// append-only log. Null on an older handle, and for a run with no injected secret (nothing to mask → safe
    /// to re-tail). It is a hash, never the key, so persisting it is safe.
    /// </summary>
    public string? InjectedKeyFingerprint { get; init; }

    /// <summary>
    /// The per-run MCP capability token the launching run bound its tool-fabric endpoint with (null when the run has
    /// no tool fabric). It is persisted here ONLY so a re-attach after a worker tear-down can RE-OPEN the endpoint with
    /// the SAME token the agent's declaration file already carries — the detached agent keeps running with its config
    /// pointing at this token, so a fresh one would lock it out. It is a LOW-VALUE per-run capability, not a durable
    /// credential: scoped to THIS run's own (team-scoped, autonomy-gated) tools, written 0600 into the spool's
    /// config-home, and dead the instant the run's listener closes. The <c>agent_run</c> row it rides on is
    /// team-scoped and never reachable from inside the sandbox. Null on an older handle and for a run with no fabric.
    /// (Unlike the model key — which is NEVER persisted — this grants nothing beyond the run's own already-gated tools.)
    /// </summary>
    public string? McpRunToken { get; init; }

    /// <summary>
    /// The key of the filtered-egress network namespace this run was launched inside (B3.2b) — non-null ONLY when a
    /// deny-by-default allowlist was enforceable and a netns was set up. It is the teardown handle: the netns / veth /
    /// nft-table names are derived purely from it, so a reap (or a re-attach after a restart, on a DIFFERENT worker)
    /// tears the namespace down with no setup-time state. Null when the run had no allowlist or the runner couldn't
    /// enforce one (degraded to None) — and the value an older handle deserializes to, so a pre-existing run is never
    /// mistaken for having a netns to reap.
    /// </summary>
    public string? EgressNetnsKey { get; init; }

    /// <summary>
    /// The key of the cgroup-v2 resource-cap leaf this run was launched inside (B4) — non-null ONLY when a memory/cpu
    /// cap was requested AND the runner could enforce it (an operator-delegated cgroup root + cgroup-v2 support). It is
    /// the teardown handle: the leaf path is derived purely from it + the operator's configured root, so a reap (or a
    /// re-attach after a restart, on a DIFFERENT worker) tears the cgroup down with no setup-time state. Null when the
    /// run had no cap or the runner couldn't enforce one — and the value an older handle deserializes to, so a
    /// pre-existing run is never mistaken for having a cgroup to reap.
    /// </summary>
    public string? CgroupRunKey { get; init; }
}
