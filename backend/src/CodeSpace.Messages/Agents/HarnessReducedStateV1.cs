using CodeSpace.Messages.Contracts;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// Everything an incremental reduction retains about the prefix it has consumed. Every field is a BOUNDED reduction —
/// a count, a sum, a first, a last, a distinct set over an enum, or a rolling digest — so retention is O(1) in the
/// number of records folded. Nothing here grows with stream length, which is the property that lets a run of any size
/// be resumed from a row instead of from its transcript.
///
/// <para><b>Why each direction is stated rather than assumed.</b> A fold that resumes from a checkpoint may only be
/// trusted if every reduction is a left fold whose whole state is captured here: a FIRST-wins field that is not
/// restored silently answers with whatever the tail happened to carry, and that is precisely today's defect — a
/// re-attach recovers the post-attach tail, so the session id a harness named exactly once at startup is simply gone
/// and nothing downstream can tell the difference between "never said" and "not restored".</para>
///
/// <para><b>Tallies count everything; NAMED facts are grounded only.</b> The counters are honest aggregates over every
/// projection in the prefix, guesses included, so "this prefix contained a guess" stays answerable. The four fields
/// that name a thing — <see cref="FirstSessionId"/>, <see cref="FirstModelCallId"/>, <see cref="LastModelCallId"/>,
/// <see cref="LastRequiredEventType"/> — are taken ONLY from a projection whose quality is
/// <see cref="SemanticProjectionQuality.Exact"/> or <see cref="SemanticProjectionQuality.RedactedExact"/>. A warm
/// resume reads them as established facts, and <see cref="ExactlyGroundedProjections"/> is one aggregate over the whole
/// prefix, so it could never afterwards say which field had been inferred.</para>
///
/// <para><b>Bookkeeping, never authority.</b> <see cref="LastRequiredEventType"/> records the last grounded projection its
/// PRODUCER marked <see cref="SemanticEventNecessity.Required"/> and means nothing about whether the run succeeded;
/// the Agent Run's own status remains the only outcome authority, and no field here is read by completion, terminal
/// decision, planner, oracle or model routing.</para>
/// </summary>
public sealed record HarnessReducedStateV1
{
    /// <summary>Data-contract version these fields are stamped with.</summary>
    public required int ContractVersion { get; init; }

    /// <summary>Native records folded so far. Must equal the frontier this state is stored beside, which is how a checkpoint cannot claim a position it did not consume.</summary>
    public required long RecordsConsumed { get; init; }

    /// <summary>Semantic projections folded so far, across every record.</summary>
    public required long ProjectionsConsumed { get; init; }

    /// <summary>How many of those projections were <see cref="SemanticProjectionQuality.Exact"/> or <see cref="SemanticProjectionQuality.RedactedExact"/> — the only two a strict read may rest on. Kept as a count so "the prefix contained a guess" is answerable without retaining the guesses.</summary>
    public required long ExactlyGroundedProjections { get; init; }

    /// <summary>How many projections their producer marked <see cref="SemanticEventNecessity.Required"/>.</summary>
    public required long RequiredProjections { get; init; }

    /// <summary>DISTINCT channels the prefix carried, in first-occurrence order. Bounded by <see cref="NativeRecordChannel"/> itself, never by the records.</summary>
    public required IReadOnlyList<NativeRecordChannel> ChannelsSeen { get; init; }

    /// <summary>FIRST session an EXACTLY GROUNDED projection named. The once-only fact a tail-only fold loses, and the one a warm resume needs — which is why a guessed session id may not fill it. Null means no grounded projection named one, never "one was inferred and kept".</summary>
    public Guid? FirstSessionId { get; init; }

    /// <summary>FIRST model call an EXACTLY GROUNDED projection named — what identifies the model a run actually started on.</summary>
    public Guid? FirstModelCallId { get; init; }

    /// <summary>LAST model call an EXACTLY GROUNDED projection named.</summary>
    public Guid? LastModelCallId { get; init; }

    /// <summary>LAST EXACTLY GROUNDED <see cref="SemanticEventNecessity.Required"/> projection's event type, so a superseded one is superseded rather than kept beside its successor — and so an inferred one can neither become this fact nor erase it. Record-keeping only: it carries no outcome authority. It may be null while <see cref="RequiredProjections"/> is non-zero, because the tally counts every Required projection and this names only a grounded one.</summary>
    public string? LastRequiredEventType { get; init; }

    /// <summary>Bytes the prefix's own capture dropped — summed over every record whose payload was masked or withheld. Clamped at zero per record because masking may substitute a longer placeholder than the secret it replaced.</summary>
    public required long RedactedByteCount { get; init; }

    /// <summary>
    /// Rolling SHA-256 over the prefix, chaining each record's identity and payload digest into the previous value.
    /// It is the WITNESS that this state reduced this exact prefix: a fold that skipped a record, folded one twice, or
    /// saw them in a different order lands on a different digest, so "the whole prefix" stops being a claim only its
    /// author can check. Canonical lowercase hex.
    /// </summary>
    public required string PrefixDigest { get; init; }

    /// <summary>Every reason this state cannot be trusted as a reduced prefix. Empty ⇒ readable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!WorkflowRunDataContract.IsSupported(ContractVersion))
            errors.Add($"contractVersion '{ContractVersion}' is unsupported");
        if (RecordsConsumed < 0 || ProjectionsConsumed < 0 || RedactedByteCount < 0)
            errors.Add("recordsConsumed, projectionsConsumed and redactedByteCount must be non-negative");
        if (!WorkflowRunDataContract.IsCanonicalSha256(PrefixDigest))
            errors.Add("prefixDigest must be a canonical lowercase SHA-256 value");

        errors.AddRange(TallyErrors());
        errors.AddRange(ChannelErrors());

        return errors;
    }

    /// <summary>Value equality that compares <see cref="ChannelsSeen"/> ELEMENT-WISE. The generated record equality compares that list by reference, so a state and its own round trip through the checkpoint would never compare equal — and that comparison is the whole point of the resume path.</summary>
    public bool Equals(HarnessReducedStateV1? other) =>
        other is not null
        && ContractVersion == other.ContractVersion
        && RecordsConsumed == other.RecordsConsumed
        && ProjectionsConsumed == other.ProjectionsConsumed
        && ExactlyGroundedProjections == other.ExactlyGroundedProjections
        && RequiredProjections == other.RequiredProjections
        && ChannelsSeen.SequenceEqual(other.ChannelsSeen)
        && FirstSessionId == other.FirstSessionId
        && FirstModelCallId == other.FirstModelCallId
        && LastModelCallId == other.LastModelCallId
        && string.Equals(LastRequiredEventType, other.LastRequiredEventType, StringComparison.Ordinal)
        && RedactedByteCount == other.RedactedByteCount
        && string.Equals(PrefixDigest, other.PrefixDigest, StringComparison.Ordinal);

    /// <summary>Hashes the prefix digest and counts — all compared above, and a chained digest already distinguishes every prefix a hash would need to.</summary>
    public override int GetHashCode() => HashCode.Combine(ContractVersion, RecordsConsumed, ProjectionsConsumed, PrefixDigest, ChannelsSeen.Count);

    private IEnumerable<string> TallyErrors()
    {
        if (ExactlyGroundedProjections < 0 || ExactlyGroundedProjections > ProjectionsConsumed)
            yield return "exactlyGroundedProjections must be between zero and projectionsConsumed";
        if (RequiredProjections < 0 || RequiredProjections > ProjectionsConsumed)
            yield return "requiredProjections must be between zero and projectionsConsumed";
        if (RequiredProjections == 0 && LastRequiredEventType is not null)
            yield return "lastRequiredEventType claims a required projection the tally does not count";
        if (ProjectionsConsumed > 0 && RecordsConsumed == 0)
            yield return "projections cannot be reduced from no records";
    }

    private IEnumerable<string> ChannelErrors()
    {
        if (ChannelsSeen.Any(channel => !Enum.IsDefined(channel)))
            yield return "channelsSeen contains an unsupported channel";
        if (ChannelsSeen.Distinct().Count() != ChannelsSeen.Count)
            yield return "channelsSeen must be distinct";
        if (ChannelsSeen.Count > 0 && RecordsConsumed == 0)
            yield return "channelsSeen claims a channel no record arrived on";
    }
}
