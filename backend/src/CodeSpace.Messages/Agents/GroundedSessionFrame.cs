namespace CodeSpace.Messages.Agents;

/// <summary>
/// The session identity a harness STATED in one of its own structured frames — read out of the frame whose content IS
/// that identity, never out of a line that happens to mention one.
///
/// <para><b>Why the distinction is the whole type.</b> A normalized event is produced by mapping a native frame onto a
/// shared vocabulary, so a fact taken from it is <see cref="SemanticProjectionQuality.Derived"/> at best and a session
/// id pattern-matched out of prose is <see cref="SemanticProjectionQuality.Heuristic"/>. Only a fact read from the
/// harness's own session record may be projected as exactly grounded, and only an exactly grounded projection may fill
/// <see cref="HarnessReducedStateV1.FirstSessionId"/> — which a warm resume then reads as an established fact. This
/// record exists so a harness ANSWERS that narrow question and cannot accidentally answer a wider one.</para>
///
/// <para><b>What a harness may not do with it.</b> Returning this for a frame that merely mentions an id launders a
/// guess into a stated fact, and nothing downstream could afterwards tell the two apart. A harness with no such frame
/// returns null — recovering nothing is the correct outcome, and strictly better than a qualification it invented.</para>
/// </summary>
public sealed record GroundedSessionFrame
{
    /// <summary>The session the frame states. A <see cref="Guid"/> because that is what <see cref="AgentSemanticEventV1.SessionId"/> and <see cref="HarnessReducedStateV1.FirstSessionId"/> carry; a harness whose id does not fit that shape has no grounded session frame to report rather than a reshaped one.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>
    /// The frame for an id a harness just read out of its own record, or null when that id names nothing.
    ///
    /// <para>The all-zero UUID names nothing. It is what this project spells "absent" everywhere else — the semantic
    /// event's own grounding CHECK refuses it inside <c>source_native_record_ids</c> for exactly that reason — and
    /// <c>Guid.TryParseExact</c> parses it happily, so a harness reading a zeroed field would otherwise hand back a
    /// perfectly well-formed frame naming a session no reader can resume. The first-wins fold latches whatever it is
    /// given, so this is decided here, once, rather than per harness.</para>
    /// </summary>
    public static GroundedSessionFrame? For(Guid sessionId) => sessionId == Guid.Empty ? null : new GroundedSessionFrame { SessionId = sessionId };
}
