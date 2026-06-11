namespace CodeSpace.Messages.Agents;

/// <summary>
/// A point-in-time snapshot of a launched run's liveness, read from its durable <see cref="SandboxHandle"/>
/// WITHOUT attaching to observe it. Lets the reconciler decide, for a run whose live observer went away,
/// whether to recover it (it finished while unobserved), leave it alone (still running), or abandon it
/// (truly gone).
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
}
