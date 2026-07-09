namespace CodeSpace.Messages.Agents;

/// <summary>Which of the three terminal shapes a supervisor <c>stop</c> took — the CLOSED axis every stop resolves to.</summary>
public enum SupervisorStopKind
{
    /// <summary>A genuine task success — the model authored a stop whose <c>outcome</c> is in the shared success set.</summary>
    Succeeded,

    /// <summary>The model stopped ITSELF without a success — a fail-closed no-decision / no-model / unknown-decision give-up (the run produced no delivered work).</summary>
    GaveUp,

    /// <summary>The SERVER forced the stop — a fail-closed bound / budget / governance trip; <see cref="SupervisorStopClassification.Reason"/> names which.</summary>
    Forced,
}

/// <summary>
/// The classified terminal shape of a supervisor <c>stop</c> — the ONE authority both the run's RESULT card
/// (<c>RoomProjector</c>) and the per-decision journal step (<c>SupervisorDecisionTimelineMap</c>) read, so
/// "did the run finish well, and if not why?" is decided in exactly one place and the two surfaces can never drift.
/// A pure data noun (Rule 18.1); built by <c>SupervisorOutcome.ClassifyStop</c> from the stop decision's payload
/// (a server-forced stop stamps <c>{reason}</c>) plus its outcome (a model stop records <c>{outcome, summary}</c>).
/// </summary>
public sealed record SupervisorStopClassification
{
    public required SupervisorStopKind Kind { get; init; }

    /// <summary>The model-authored closing line — a success / give-up stop's <c>summary</c>. Null for a server-forced stop (which carries a reason, not a summary).</summary>
    public string? Summary { get; init; }

    /// <summary>Why the run stopped short — a forced stop's bound reason (e.g. "no progress"), or a give-up's non-success outcome label. Null for a genuine success.</summary>
    public string? Reason { get; init; }

    /// <summary>P3.5 — a DYNAMIC elaboration of <see cref="Reason"/> for the bounds that carry one (currently the cost cap: "$X.XX spent of $Y.YY cap ($Z.ZZ OVER) — agent execution $A, ..."). Null for every reason that has no per-run figure to cite (no-progress, governance-denied, etc.) and for a give-up/success stop.</summary>
    public string? Detail { get; init; }

    /// <summary>True when the run did NOT finish well — anything but <see cref="SupervisorStopKind.Succeeded"/>. The RESULT card renders neutral/degraded (never a green success) on this.</summary>
    public bool Degraded => Kind != SupervisorStopKind.Succeeded;

    /// <summary>The best human-facing line for the RESULT card / step summary: the model's closing summary when it wrote one, else the short reason (with its dynamic detail appended when present). Null when neither a summary nor a reason exists.</summary>
    public string? DisplayText => string.IsNullOrWhiteSpace(Summary) ? ReasonWithDetail : Summary;

    private string? ReasonWithDetail => string.IsNullOrWhiteSpace(Reason) ? null : string.IsNullOrWhiteSpace(Detail) ? Reason : $"{Reason} — {Detail}";
}
