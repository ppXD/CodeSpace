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
/// whether the run is still alive. <see cref="SpoolDirectory"/> is the resolved absolute directory holding
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

    /// <summary>Resolved absolute path to the run's spool directory holding <c>out.log</c>, <c>err.log</c>, and the <c>exit</c> marker.</summary>
    public required string SpoolDirectory { get; init; }

    /// <summary>Absolute wall-clock deadline (launch + the spec's timeout). The observer terminates the process once <c>now</c> passes it — surviving a re-attach by a new observer.</summary>
    public required DateTimeOffset Deadline { get; init; }
}
