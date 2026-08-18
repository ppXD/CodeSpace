using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Agents.Reduction;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The representative frame stream the reduction tests fold, plus the injected <see cref="IHarnessRecordSource"/> that
/// serves it. Kept in one place so the differential, idempotence and retention tests all fold the IDENTICAL stream — a
/// differential proof over three slightly different streams proves nothing about the reduction.
/// </summary>
internal static class HarnessReductionStream
{
    internal static readonly Guid ExecutionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid PrimaryStreamId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid ProtocolStreamId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid SessionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    internal static readonly Guid FirstModelCallId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    internal static readonly Guid LastModelCallId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    internal const string SupersededTerminal = "https://codespace.dev/agent/events/run-finished";
    internal const string WinningTerminal = "https://codespace.dev/agent/events/run-finished-after-revise";

    /// <summary>
    /// The headline stream. Frame 0 is the ONLY frame that names the session — the once-only fact a tail-only fold
    /// loses today — and it sits before every boundary the segmented tests cut at. It also carries a duplicate fact
    /// (the same model call named twice), a superseded Required projection, a masked payload whose dropped bytes are
    /// summed, and a second capture stream so the frontier is genuinely per-stream rather than a scalar in disguise.
    /// </summary>
    internal static IReadOnlyList<HarnessReductionFrame> Representative() => new[]
    {
        Frame(PrimaryStreamId, 0, NativeRecordChannel.SessionState, "session.created", Session()),
        Frame(PrimaryStreamId, 1, NativeRecordChannel.Stdout, "assistant"),
        Frame(ProtocolStreamId, 0, NativeRecordChannel.Protocol, "handshake", Required(SupersededTerminal)),
        Frame(PrimaryStreamId, 2, NativeRecordChannel.ModelWire, "response", ModelCall(FirstModelCallId)),
        Frame(PrimaryStreamId, 3, NativeRecordChannel.ModelWire, "response", ModelCall(FirstModelCallId)),
        Frame(PrimaryStreamId, 4, NativeRecordChannel.Stderr, "warning", Heuristic()),
        Frame(ProtocolStreamId, 1, NativeRecordChannel.Protocol, "token_count", ModelCall(LastModelCallId)),
        Masked(PrimaryStreamId, 5, NativeRecordChannel.ToolWire, "tool_result"),
        Frame(PrimaryStreamId, 6, NativeRecordChannel.Stdout, "assistant"),
        Frame(ProtocolStreamId, 2, NativeRecordChannel.Control, "abort", Required(WinningTerminal)),
    };

    /// <summary>
    /// A stream in which every NAMED fact is a guess: the only session id rides on a Heuristic projection scraped out of
    /// stderr, and the only model call and Required event type ride on one whose provenance was never established. Both
    /// are fully valid projections — an uncited non-grounded projection is exactly what the contract expects — so
    /// nothing upstream rejects them, and the fold is the last place the distinction can still be kept.
    /// </summary>
    internal static IReadOnlyList<HarnessReductionFrame> GuessedFactsOnly() => new[]
    {
        Frame(PrimaryStreamId, 0, NativeRecordChannel.Stderr, "warning", Heuristic() with { SessionId = SessionId }),
        Frame(PrimaryStreamId, 1, NativeRecordChannel.Stderr, "warning", Unprovenanced()),
    };

    /// <summary>A synthetic stream of <paramref name="count"/> frames on one capture stream, for the retention proof. Every frame carries the same event type, so the only thing that may legitimately grow in the reduced state is the digits of its counters.</summary>
    internal static IReadOnlyList<HarnessReductionFrame> Long(int count) =>
        Enumerable.Range(0, count).Select(index => Frame(PrimaryStreamId, index, NativeRecordChannel.Stdout, "assistant", ModelCall(FirstModelCallId), Required("https://codespace.dev/agent/events/step"))).ToArray();

    internal static HarnessReductionFrame Frame(Guid streamId, long ordinal, NativeRecordChannel channel, string nativeType, params AgentSemanticEventV1[] projections)
    {
        var record = Record(streamId, ordinal, channel, nativeType);

        return new HarnessReductionFrame { Record = record, Projections = Attribute(record.RecordId, projections) };
    }

    /// <summary>A frame whose payload was masked, so 24 of its 64 wire bytes never reached storage.</summary>
    internal static HarnessReductionFrame Masked(Guid streamId, long ordinal, NativeRecordChannel channel, string nativeType)
    {
        var record = Record(streamId, ordinal, channel, nativeType) with { Redaction = NativeRecordRedaction.Masked, SizeBytes = 40 };

        return new HarnessReductionFrame { Record = record, Projections = Attribute(record.RecordId, new[] { RedactedExact() }) };
    }

    internal static NativeRecordV1 Record(Guid streamId, long ordinal, NativeRecordChannel channel, string nativeType) => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion,
        RecordId = DeterministicId($"record:{streamId:D}:{ordinal}"),
        StreamId = streamId,
        Ordinal = ordinal,
        Channel = channel,
        NativeType = nativeType,
        IngestedAt = new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero).AddSeconds(ordinal),
        ByteOffset = ordinal * 64,
        ByteLength = 64,
        InlinePayload = $"{nativeType}#{ordinal}",
        DigestAlgorithm = WorkflowRunDataContract.Sha256Algorithm,
        Digest = Sha256($"{streamId:D}:{ordinal}:{nativeType}"),
        SizeBytes = 64,
        Encoding = NativeRecordPayloadEncoding.Utf8,
        Redaction = NativeRecordRedaction.None,
        IsFinal = true,
    };

    internal static Guid DeterministicId(string seed) => new(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16).ToArray());

    internal static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static AgentSemanticEventV1 Session() => Projection("session-started") with { SessionId = SessionId };

    private static AgentSemanticEventV1 ModelCall(Guid modelCallId) => Projection("model-call") with { ModelCallId = modelCallId };

    private static AgentSemanticEventV1 Required(string eventType) => Projection("required") with { EventType = eventType, Necessity = SemanticEventNecessity.Required };

    private static AgentSemanticEventV1 Heuristic() => Projection("guess") with { ProjectionQuality = SemanticProjectionQuality.Heuristic };

    /// <summary>A Required projection naming a model call and a terminal event type, whose provenance was never established — retained so it stays visible, and backing no strict read.</summary>
    private static AgentSemanticEventV1 Unprovenanced() => Projection("unprovenanced") with
    {
        EventType = WinningTerminal, Necessity = SemanticEventNecessity.Required,
        ModelCallId = LastModelCallId, ProjectionQuality = SemanticProjectionQuality.Unknown,
    };

    private static AgentSemanticEventV1 RedactedExact() => Projection("masked") with { ProjectionQuality = SemanticProjectionQuality.RedactedExact };

    private static AgentSemanticEventV1 Projection(string name) => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion,
        EventId = DeterministicId($"event:{name}"),
        EventType = $"https://codespace.dev/agent/events/{name}",
        EventSchemaVersion = 1,
        SourceNativeRecordIds = Array.Empty<Guid>(),
        ExecutionId = ExecutionId,
        Necessity = SemanticEventNecessity.Ignorable,
        ProjectionQuality = SemanticProjectionQuality.Exact,
    };

    /// <summary>Cites the record a projection arrived with, which every exactly-grounded projection owes. A heuristic one deliberately cites nothing — it was inferred from prose, not read off a frame.</summary>
    private static IReadOnlyList<AgentSemanticEventV1> Attribute(Guid recordId, IReadOnlyList<AgentSemanticEventV1> projections) =>
        projections.Select(projection => projection.ProjectionQuality.IsExactlyGrounded()
            ? projection with { SourceNativeRecordIds = new[] { recordId } }
            : projection).ToArray();
}

/// <summary>
/// Serves a fixed frame list as the suffix after a position, in list order — the deterministic total order
/// <see cref="IHarnessRecordSource"/> requires of a real source. <see cref="RedeliverEverything"/> makes it ignore the
/// position entirely, which is what a source does when its own read cursor is coarser than the reduction's frontier —
/// the situation a crash between folding and checkpointing leaves behind.
/// </summary>
internal sealed class FixedHarnessRecordSource : IHarnessRecordSource
{
    private readonly IReadOnlyList<HarnessReductionFrame> _frames;

    public FixedHarnessRecordSource(IReadOnlyList<HarnessReductionFrame> frames) => _frames = frames;

    /// <summary>Yield every frame regardless of the resume position, so already-folded ones come back.</summary>
    public bool RedeliverEverything { get; init; }

    /// <summary>Every position this source was asked to read from, in call order.</summary>
    public List<HarnessReductionPosition> Reads { get; } = new();

    public async IAsyncEnumerable<HarnessReductionFrame> ReadForwardAsync(HarnessReductionPosition after, CancellationToken cancellationToken)
    {
        Reads.Add(after);

        foreach (var frame in _frames)
        {
            if (!RedeliverEverything && frame.Record.Ordinal < after.NextOrdinalOf(frame.Record.StreamId)) continue;

            yield return frame;
        }

        await Task.CompletedTask;
    }
}
