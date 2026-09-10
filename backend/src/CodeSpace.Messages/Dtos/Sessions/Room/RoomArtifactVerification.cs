using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Sessions.Room;

/// <summary>
/// One PER-ARTIFACT / PER-REPOSITORY verification fact (Launch Extremis P21) — which check ran against THIS
/// delivered artifact or repository, distinct from every OTHER one the same turn produced. A multi-repo turn can
/// have one repository cleanly verified and a sibling withheld; folding both into the run's single
/// <see cref="FinalAnswerBlock.Verified"/> chip would hide exactly that split. Attached to the <see cref="DeliveryBlock"/>
/// (per repository) or <see cref="DeliverableFile"/> (per produced file) it belongs to, so a reader never has to
/// cross-reference a separate list to find which row a check is about.
///
/// <para>Populated ONLY from RECORDED per-unit grader facts — never from the request. When a fact was never
/// captured, its field is null; this projection never fabricates a pass.</para>
/// </summary>
public sealed record RoomArtifactVerification
{
    /// <summary>
    /// Which delivered thing this row is about: the repository (its alias, or id when unaliased) for a
    /// per-repository row, or the producing agent run id for a per-file row. Display/audit only — the row already
    /// lives nested under its own artifact/repository block, so nothing downstream needs to parse this back out.
    /// </summary>
    public required string ArtifactOrRepositoryRef { get; init; }

    /// <summary>The kind of check this row reports (open vocabulary, e.g. <c>"acceptance"</c>) — never switched on for copy.</summary>
    public required string CheckKind { get; init; }

    /// <summary>
    /// Whether a real objective check actually EXECUTED for this unit — the same graded/ungraded line
    /// <c>RoomProjector.UnitGrades</c> draws for the run-level verdict (excludes a WAIVED disposition and a VACUOUS
    /// "nothing to verify" pass, neither of which ran a check). False means nothing verified this delivery, no
    /// matter what <see cref="Passed"/> happens to hold.
    /// </summary>
    public required bool Ran { get; init; }

    /// <summary>The check's verdict when it ran; null when it did not (<see cref="Ran"/> is false) — never a fabricated pass for a check that never executed.</summary>
    public bool? Passed { get; init; }

    /// <summary>The grader's own reason text (e.g. "tests-failed-exit-1"), bounded. Null when there is nothing to add.</summary>
    public string? Detail { get; init; }

    public required RoomOracleProtection OracleProtection { get; init; }

    /// <summary>The CAS id of this check's captured output, when the grader kept one. Null when none was captured.</summary>
    public Guid? EvidenceArtifactId { get; init; }

    /// <summary>
    /// Whether the producing agent's OWN log stream(s) settled completely — a fact kept SEPARATE from
    /// <see cref="Passed"/> on purpose: an incomplete log must never silently cancel an otherwise-verified delivery
    /// (the P21 invariant), so the two ride independent fields rather than one collapsing into the other. Null when
    /// the agent declared no log stream at all.
    /// </summary>
    public bool? LogsComplete { get; init; }
}

/// <summary>
/// Whether an objective check's judge program was protected from candidate tampering (see
/// <c>AcceptanceOracleProtection</c>) — read off the same Detail markers the supervisor decider prompt already
/// decodes, never a second definition.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoomOracleProtection
{
    /// <summary>
    /// No oracle-protection signal on this grade's Detail — either the check owns no judge program to protect at
    /// all, or (the silent, dominant case) it was cleanly protected and left untampered, which leaves no trace on
    /// Detail by design. This projection cannot yet tell the two apart from recorded per-unit facts alone, so it
    /// reports the WEAKER claim rather than assume a protection it cannot evidence.
    /// </summary>
    None = 0,

    /// <summary>
    /// The judge COULD have been protected but had no base to restore from (no anchor). DEFINED, UNPOPULATED
    /// today (mirrors <see cref="Protected"/>'s same precedent): the grader records this clause in
    /// <c>OracleNote</c> (<c>AcceptanceOracleProtection.UnanchoredDetailMarker</c>), which no per-unit
    /// <c>AcceptanceDetail</c> producer copies onto <see cref="RoomArtifactVerification.Detail"/> — never emitted
    /// by this projection on real data.
    /// </summary>
    Unanchored,

    /// <summary>This grade ran, at least in part, on the candidate's OWN copy of a program file — self-graded, not a protected judge.</summary>
    Subject,

    /// <summary>
    /// DEFINED for a future producer (mirrors <see cref="AnswerAttachmentKind.Image"/>'s same "defined,
    /// unpopulated" precedent): no per-unit fact recorded today distinguishes a cleanly protected, untampered pass
    /// from <see cref="None"/> — a clean restore is deliberately silent on Detail. Never emitted by this projection.
    /// </summary>
    Protected,
}
