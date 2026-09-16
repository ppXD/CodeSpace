namespace CodeSpace.Messages.Agents;

/// <summary>
/// What a durable runner's terminate actually DID to the supervised process tree. Four of these six are a KILL THAT
/// NEVER HAPPENED: the runner declined to signal, for a reason only it could see. Before this type those four were
/// indistinguishable from a successful kill at every call site — the terminate returned <c>void</c>, and the abandon
/// path's only log fired when it THREW — so a run recorded as abandoned could leave its agent running with nothing
/// anywhere saying so, and a test that found the process still alive could only report "should be True but was False".
/// </summary>
public enum SandboxTerminateOutcome
{
    /// <summary>The process was alive, the signal was issued, and the tree was observed gone afterwards.</summary>
    Killed,

    /// <summary>The process was already gone before any signal — the terminate is a no-op, which is the contract's idempotence, not a miss.</summary>
    AlreadyGone,

    /// <summary>The handle names a process on ANOTHER host (or another boot of this one), where its pid is either nothing or an unrelated local process. No signal is issued on purpose; that host's own sweep owns it.</summary>
    SkippedNotLocal,

    /// <summary>The runner could not bind the handle to a launch it is willing to act on — a receipt/commitment mismatch, or an I/O failure while re-reading the four native-launch files. <c>Detail</c> names which gate refused.</summary>
    SkippedUnresolvableHandle,

    /// <summary>Liveness could not be observed at all (the <c>/proc</c> or <c>libproc</c> read failed with something other than "absent"), so the runner refused to guess in either direction.</summary>
    SkippedIndeterminate,

    /// <summary>The signal WAS issued but the tree was still alive when the bounded reap wait expired. The process may yet die; nothing here proves it did. <c>Detail</c> names the bound.</summary>
    TimedOutWaitingReap,

    /// <summary>The terminate THREW. Structurally different from every skip above — those are decisions the runner made and can explain, this is the runner failing to reach a decision — so it never shares their vocabulary. Recorded by the CALLER, which is what owns the try/catch.</summary>
    ThrewDuringTerminate,
}

/// <summary>
/// One terminate's outcome plus the evidence behind it. <paramref name="Detail"/> is free text for a human reading a
/// warning or a failing test — the refusing gate, the host identity that disagreed, the observation that could not be
/// made, the wait bound that expired — and is null only for the two outcomes that need no explanation.
/// </summary>
public sealed record SandboxTerminateResult(SandboxTerminateOutcome Outcome, string? Detail)
{
    /// <summary>True for the two outcomes that settle the question: the process is provably not running any more.</summary>
    public bool IsSettled => Outcome is SandboxTerminateOutcome.Killed or SandboxTerminateOutcome.AlreadyGone;

    public static SandboxTerminateResult Killed { get; } = new(SandboxTerminateOutcome.Killed, null);

    public static SandboxTerminateResult AlreadyGone { get; } = new(SandboxTerminateOutcome.AlreadyGone, null);

    /// <summary>A kill that did not happen, carrying why. <paramref name="detail"/> is what a reader needs to tell this skip apart from the other three.</summary>
    public static SandboxTerminateResult Skipped(SandboxTerminateOutcome outcome, string? detail) => new(outcome, detail);
}
