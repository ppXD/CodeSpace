namespace CodeSpace.Messages.Agents;

/// <summary>
/// A point-in-time snapshot of a launched run's liveness, read from its durable <see cref="SandboxHandle"/>
/// WITHOUT attaching to observe it. Lets the reconciler decide, for a run whose live observer went away,
/// whether to recover it (it finished while unobserved), leave it alone (still running), or abandon it
/// (truly gone) — plus the fourth answer a multi-worker deployment forces, <see cref="SandboxRunState.Indeterminate"/>:
/// this worker cannot tell, so it must not decide.
/// </summary>
public sealed record SandboxProbe
{
    public required SandboxRunState State { get; init; }

    /// <summary>The recorded exit code when <see cref="State"/> is <see cref="SandboxRunState.Exited"/>; <c>null</c> otherwise.</summary>
    public int? ExitCode { get; init; }
}

/// <summary>What a <see cref="SandboxProbe"/> found at the handle.</summary>
public enum SandboxRunState
{
    /// <summary>The supervised process is still alive and no exit marker is present — the run is in flight.</summary>
    Running,

    /// <summary>An exit marker is present — the run finished (with <see cref="SandboxProbe.ExitCode"/>) while unobserved.</summary>
    Exited,

    /// <summary>The supervised process is gone and never recorded an exit marker — it was killed before completing.</summary>
    Gone,

    /// <summary>
    /// The runner cannot answer this handle's liveness FROM HERE — a statement about the prober, not about the run.
    /// The local runner answers it for a handle whose <see cref="SandboxHandle.LaunchHost"/> is not the worker's own
    /// host: an OS pid means nothing outside the process namespace that minted it, so resolving it here would either
    /// find nothing (a LIVE run read as <see cref="Gone"/>) or find an UNRELATED local process wearing the same
    /// number (read as <see cref="Running"/>).
    ///
    /// <para>A caller must NOT fold it into <see cref="Gone"/>: absence of evidence is not evidence of death. The
    /// only sound responses are to defer (a sweep landing on the minting host answers the same handle definitively)
    /// or to act on grounds that do not need the pid at all — e.g. that
    /// <see cref="SandboxHandle.Deadline"/> has passed, after which no observer can still be completing the run.</para>
    /// </summary>
    Indeterminate,
}
