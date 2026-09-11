namespace CodeSpace.Messages.Agents;

/// <summary>What a run's launch actually did about OS-level confinement — the fact a reader needs before believing an "off" network posture.</summary>
public enum SandboxConfinementOutcome
{
    /// <summary>The runner applies no confinement at all (the non-durable streaming path) — nothing was attempted, so nothing was severed.</summary>
    NotApplicable,

    /// <summary>The launch rewrote the command through bubblewrap: namespaces, a read-only root, and — when <c>NetworkSevered</c> — a fresh empty net namespace.</summary>
    Confined,

    /// <summary>The host could not confine (see <c>Reason</c>), so the command ran bare. Any permission the tier expressed as "off" was NOT enforced by the OS.</summary>
    Unconfined,
}

/// <summary>
/// The per-run record of what the sandbox ACTUALLY did, stamped at launch and persisted on the agent-run row
/// (<c>agent_run.sandbox_confinement</c>). It exists because <c>AgentPermissions.Network == Off</c> is a PERMISSION,
/// not a proven severed namespace: the durable runner turns it into <c>--unshare-net</c> only where
/// <c>BubblewrapSandbox.Available</c> is non-null, and <c>Sandbox:RequireConfinement</c> — the setting that would
/// REFUSE an unconfinable host — is committed off. Without this record every reader had to hedge ("severed only
/// where the sandbox confines") because nothing anywhere said whether this particular run got confinement.
///
/// <para>Persisted on its OWN column rather than inside the launch handle: <c>agent_run.runner_handle</c> is nulled
/// by the spool reaper 24h after a run goes terminal, and the posture a run had is a permanent fact about it, not a
/// recovery aid. The handle still CARRIES it from runner to executor (see <c>SandboxHandle.Confinement</c>) — that is
/// the transport; this column is the record.</para>
/// </summary>
public sealed record SandboxConfinement
{
    /// <summary>Not Linux, so bubblewrap cannot exist — macOS/Windows development.</summary>
    public const string ReasonNotLinux = "not-linux";

    /// <summary>Linux, but no runnable <c>bwrap</c> binary (absent, or not executable).</summary>
    public const string ReasonNoBubblewrap = "no-bwrap";

    /// <summary>Linux with <c>bwrap</c> present, but the confinement probe failed — unprivileged user namespaces denied, or a bwrap too old for the flags a real launch needs.</summary>
    public const string ReasonNoUserNamespaces = "no-userns";

    public required SandboxConfinementOutcome Outcome { get; init; }

    /// <summary>Why the host could not confine — one of the <c>Reason*</c> constants. Null for every outcome but <see cref="SandboxConfinementOutcome.Unconfined"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>Whether the launch put the agent in a fresh EMPTY net namespace (<c>--unshare-net</c>) — true when confinement applied AND its egress policy came out anything but full: the run's network was off, OR it asked for an allowlist the sandbox cannot yet enforce and so failed closed. False for a plain shared-network run and for every unconfined one.</summary>
    public bool NetworkSevered { get; init; }

    /// <summary>
    /// Whether the run's model credential was BROKERED (true) or placed in the sandbox's environment as the tenant's
    /// own provider key (false). Null when the launch injected no credential at all — a harness that authenticates
    /// some other way, or a run for which none resolved — because there is then nothing to disclose either way.
    ///
    /// <para>A distinct fact from <see cref="Outcome"/>, stated separately because a reader who is told only about
    /// confinement will assume the wrong thing about the key: a run can be fully confined and still hold a long-lived
    /// third-party credential its operator cannot withdraw before the process dies. False is what
    /// <c>AgentAutonomyPolicy.DirectModelCredentialCaveat</c> exists to say out loud.</para>
    /// </summary>
    public bool? ModelCredentialBrokered { get; init; }

    /// <summary>
    /// Whether this run's BROKERED model credential is no longer held by any live worker — stamped by a re-attach,
    /// because the lease lives in the memory of the worker that minted it and dies with that process. The detached
    /// CLI is still pointed at a broker port that no longer answers, so from the moment this is true the run has no
    /// model access at all.
    ///
    /// <para>The consequence of the same design that makes revocation real, recorded rather than left to be
    /// diagnosed: a run that stalls after a worker restart looks exactly like a provider outage otherwise. False (the
    /// default) for every run whose launch is still being observed by the worker that opened its lease, and for every
    /// unbrokered run — those have nothing to lose here, and say so by saying nothing.</para>
    /// </summary>
    public bool ModelCredentialLeaseLost { get; init; }
}
