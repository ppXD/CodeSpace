using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Agents.Capture;

/// <summary>
/// The EXACTLY GROUNDED projector: it reads a captured frame's own bytes and, when the harness recognises them as one
/// of its own structured session records, emits a projection that may honestly claim the harness's words.
///
/// <para><b>What it closes.</b> <see cref="AgentNativeRecordPump.Project"/> stamps
/// <see cref="SemanticProjectionQuality.Derived"/> on every projection it makes, correctly, because it is projecting a
/// NORMALIZED event. The reduction takes its named once-only facts only from an exactly grounded projection, so before
/// this projector existed <see cref="HarnessReducedStateV1.FirstSessionId"/> was null on every real run and a re-attach
/// recovered the counts, the channel set and the prefix digest — nothing that names anything.</para>
///
/// <para><b>Why it reads the RECORD and not the line.</b> An exactness claim is a claim about the bytes that were
/// stored, and the database enforces exactly that: it refuses <see cref="SemanticProjectionQuality.Exact"/> over a
/// source frame whose redaction is not <see cref="NativeRecordRedaction.None"/>, and refuses even
/// <see cref="SemanticProjectionQuality.RedactedExact"/> over one that was never captured. Reading
/// <see cref="NativeRecordV1.InlinePayload"/> — the bytes the row will carry — is what makes the claim and the stored
/// evidence the same thing rather than two statements that could drift.</para>
///
/// <para><b>What it deliberately does not do.</b> It never infers. The harness answers a narrow question over its own
/// frame (<see cref="IAgentGroundedFrameReader"/>) and a harness that has no such frame answers null, so a channel
/// whose frames only MENTION a session stays <see cref="SemanticProjectionQuality.Derived"/> forever. It also claims
/// nothing about the model: <see cref="HarnessReducedStateV1"/> has no field for one, and inventing a state shape to
/// hold it would be a new reducer kind rather than this projector's business.</para>
/// </summary>
internal static class GroundedFrameProjector
{
    /// <summary>The event a grounded session frame projects to. Its own type rather than a normalized kind, because no <see cref="AgentEventKind"/> means "the harness stated its session identity here".</summary>
    internal const string SessionNamedEventType = AgentNativeRecordPump.EventTypeNamespace + "harness-session-named";

    /// <summary>Schema generation of <see cref="SessionNamedEventType"/>'s payload, one-based and independent of the plane's contract version.</summary>
    internal const int SessionNamedEventSchemaVersion = 1;

    /// <summary>
    /// The exactly grounded projection of one captured frame, or null when the harness cannot read one out of it —
    /// which is the answer for a harness with no grounded-frame reader at all, for every frame that is not its session
    /// record, and for a frame whose captured bytes could not support an exactness claim.
    ///
    /// <para>The all-zero id is refused HERE and not only in <see cref="GroundedSessionFrame.For"/>, because this is
    /// the one gate every grounded fact passes through and a harness is free to construct the record directly. It is
    /// the same reading 0139's grounding CHECK takes of that id: well-formed, and naming nothing.</para>
    /// </summary>
    internal static AgentSemanticEventV1? Project(IAgentHarness harness, NativeRecordCaptureHandle handle, NativeRecordV1 record)
    {
        if (harness is not IAgentGroundedFrameReader reader) return null;
        if (record.InlinePayload is not { } captured) return null;
        if (ClaimableOver(record) is not { } quality) return null;
        if (reader.ReadSessionFrame(captured) is not { SessionId: var sessionId } || sessionId == Guid.Empty) return null;

        return SessionNamed(handle, record, sessionId, quality);
    }

    /// <summary>
    /// The strongest fidelity the CAPTURED bytes support, or null for bytes that support none. Verbatim bytes are what
    /// the harness wrote, so a fact read out of them is <see cref="SemanticProjectionQuality.Exact"/>; masked bytes
    /// support only <see cref="SemanticProjectionQuality.RedactedExact"/>, which is what that value means; a withheld
    /// frame was never captured, so nothing read "out of it" could have been read at all. The database refuses each
    /// wrong pairing independently, so this is the in-process statement of a rule the row is held to either way.
    /// </summary>
    private static SemanticProjectionQuality? ClaimableOver(NativeRecordV1 record) => record.Redaction switch
    {
        NativeRecordRedaction.None => SemanticProjectionQuality.Exact,
        NativeRecordRedaction.Masked => SemanticProjectionQuality.RedactedExact,
        _ => null,
    };

    /// <summary>
    /// <see cref="SemanticEventNecessity.Ignorable"/>, stated rather than assumed: nothing reads this plane yet and the
    /// normalized <c>agent_run_event</c> log remains the authority, so a reader that cannot route this event loses no
    /// fact it is accountable for. It also keeps <see cref="HarnessReducedStateV1.LastRequiredEventType"/> null, which
    /// is honest — this lane makes the session id flow and makes no claim about that field.
    /// </summary>
    private static AgentSemanticEventV1 SessionNamed(NativeRecordCaptureHandle handle, NativeRecordV1 record, Guid sessionId, SemanticProjectionQuality quality) => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion,
        EventId = Guid.NewGuid(),
        EventType = SessionNamedEventType,
        EventSchemaVersion = SessionNamedEventSchemaVersion,
        SourceNativeRecordIds = new[] { record.RecordId },
        ExecutionId = handle.ExecutionId,
        SessionId = sessionId,
        Necessity = SemanticEventNecessity.Ignorable,
        ProjectionQuality = quality,
    };
}
