using System.Security.Cryptography;
using System.Text;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Agents.Reduction;

/// <summary>
/// The INCREMENTAL reduction itself: fold frames forward from a checkpoint and land on the same state a whole-stream
/// fold would have produced.
///
/// <para><b>Why it exists.</b> A re-attach today folds only the tail. The executor's fold is already O(1) per event,
/// but on a re-attach it starts from nothing, because nothing durable says how far it got or what it had reduced — so
/// a session id the harness named once at startup, the model the first call chose, and the prefix of the transcript are
/// silently gone, and the recovered state is a different value that no reader can tell apart from the right one.</para>
///
/// <para><b>Why a fresh fold and a resumed fold are the same code path.</b> A fresh fold is a fold resumed from
/// <see cref="SeedCheckpoint"/>. There is no second constructor and no "starting from scratch" branch, so there is no
/// path on which a field is accumulated but not restored — which is the shape of the bug this replaces. The equality
/// between folding a stream whole and folding it in segments is therefore structural, and
/// <c>HarnessReductionFoldTests</c> still proves it differentially rather than trusting the argument.</para>
///
/// <para><b>Re-consumption is idempotent by POSITION, not by hoping the reductions are.</b> Several of them are
/// deliberately not idempotent — every count would double, and <see cref="HarnessReducedStateV1.PrefixDigest"/> would
/// chain the same record twice into a value that no longer witnesses the real prefix. Instead a frame behind the
/// frontier is refused entry (<see cref="HarnessFrameDisposition.AlreadyReduced"/>), which makes replay after a crash
/// safe regardless of what the individual reductions do. That is what buys the CONSUME-THEN-CHECKPOINT direction: a
/// checkpoint may lag the fold and never lead it, so a crash re-consumes and never skips.</para>
///
/// <para><b>What it cannot promise.</b> The interleaving of records ACROSS streams is the source's deterministic total
/// order (<see cref="IHarnessRecordSource"/>), not this fold's. Under a different interleaving the FIRST/LAST fields
/// and the prefix digest are legitimately different values; segmented resumption is exact only because it reads the
/// same order the whole-stream fold did.</para>
///
/// <para>Single-threaded by contract, like <see cref="AgentResultFold"/> and <see cref="AgentRunFacts"/> next to it:
/// one fold belongs to one execution's sequential accumulation.</para>
/// </summary>
public sealed class HarnessReductionFold
{
    /// <summary>Stable kind of THIS reduction. The <c>/vN</c> is the state SHAPE's generation: a changed shape is a new kind stored beside the old one, never a rewrite of a row an old reader still parses.</summary>
    public const string ReducerKind = "harness-prefix/v1";

    /// <summary>Domain separation for the chain, so a prefix digest can never collide with a bare SHA-256 of anything else.</summary>
    private const string DigestDomain = "codespace.harness-reduction/v1";

    private readonly Dictionary<Guid, long> _frontier = new();
    private readonly List<NativeRecordChannel> _channelsSeen = new();
    private readonly HashSet<NativeRecordChannel> _channelKeys = new();
    private readonly Guid _executionId;
    private byte[] _prefixDigest;
    private long _records;
    private long _projections;
    private long _exactlyGrounded;
    private long _required;
    private long _redactedBytes;
    private Guid? _firstSessionId;
    private Guid? _firstModelCallId;
    private Guid? _lastModelCallId;
    private string? _lastRequiredEventType;

    /// <summary>Resume a reduction from <paramref name="resumeFrom"/>. Pass <see cref="SeedCheckpoint"/> to start one.</summary>
    public HarnessReductionFold(HarnessReductionCheckpointV1 resumeFrom)
    {
        EnsureResumable(resumeFrom);

        _executionId = resumeFrom.ExecutionId;
        _records = resumeFrom.State.RecordsConsumed;
        _projections = resumeFrom.State.ProjectionsConsumed;
        _exactlyGrounded = resumeFrom.State.ExactlyGroundedProjections;
        _required = resumeFrom.State.RequiredProjections;
        _redactedBytes = resumeFrom.State.RedactedByteCount;
        _firstSessionId = resumeFrom.State.FirstSessionId;
        _firstModelCallId = resumeFrom.State.FirstModelCallId;
        _lastModelCallId = resumeFrom.State.LastModelCallId;
        _lastRequiredEventType = resumeFrom.State.LastRequiredEventType;
        _prefixDigest = Convert.FromHexString(resumeFrom.State.PrefixDigest);

        foreach (var stream in resumeFrom.Position.Streams) _frontier[stream.StreamId] = stream.NextOrdinal;
        foreach (var channel in resumeFrom.State.ChannelsSeen) RecordChannel(channel);
    }

    /// <summary>The checkpoint as it stands: exactly the prefix folded so far and exactly the state folded from it. Each read materializes fresh values, so a checkpoint taken mid-stream is not mutated by the frames that follow it.</summary>
    public HarnessReductionCheckpointV1 Checkpoint => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion,
        ExecutionId = _executionId,
        ReducerKind = ReducerKind,
        Position = MaterializePosition(),
        State = MaterializeState(),
    };

    /// <summary>The checkpoint of a reduction that has consumed nothing — the value a fresh fold resumes from.</summary>
    public static HarnessReductionCheckpointV1 SeedCheckpoint(Guid executionId) => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion,
        ExecutionId = executionId,
        ReducerKind = ReducerKind,
        Position = HarnessReductionPosition.Empty,
        State = SeedState(),
    };

    /// <summary>
    /// Fold ONE frame in, in the source's reduction order. O(1) retention. Returns
    /// <see cref="HarnessFrameDisposition.AlreadyReduced"/> for a frame behind the frontier, and raises
    /// <see cref="HarnessReductionGapException"/> for one past it — a hole cannot be folded into a state that will be
    /// stored as a consumed prefix.
    /// </summary>
    public HarnessFrameDisposition Add(HarnessReductionFrame frame)
    {
        var expected = _frontier.TryGetValue(frame.Record.StreamId, out var next) ? next : 0;

        if (frame.Record.Ordinal < expected) return HarnessFrameDisposition.AlreadyReduced;

        if (frame.Record.Ordinal > expected)
            throw new HarnessReductionGapException(frame.Record.StreamId, expected, frame.Record.Ordinal);

        ReduceRecord(frame.Record);

        foreach (var projection in frame.Projections) ReduceProjection(projection);

        _frontier[frame.Record.StreamId] = expected + 1;

        return HarnessFrameDisposition.Reduced;
    }

    private static HarnessReducedStateV1 SeedState() => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion,
        RecordsConsumed = 0,
        ProjectionsConsumed = 0,
        ExactlyGroundedProjections = 0,
        RequiredProjections = 0,
        ChannelsSeen = Array.Empty<NativeRecordChannel>(),
        RedactedByteCount = 0,
        PrefixDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DigestDomain))).ToLowerInvariant(),
    };

    /// <summary>Chains one record's IDENTITY as well as its payload digest, so the witness distinguishes the same bytes arriving at a different position — which is exactly what a fold that skipped or duplicated a record produces.</summary>
    private static byte[] ChainDigest(byte[] previous, NativeRecordV1 record)
    {
        var link = $"{Convert.ToHexString(previous).ToLowerInvariant()}:{record.StreamId:D}:{record.Ordinal}:{record.Digest}";

        return SHA256.HashData(Encoding.UTF8.GetBytes(link));
    }

    /// <summary>Clamped at zero per record: masking may substitute a placeholder LONGER than the secret it replaced, and a negative "bytes dropped" would silently offset a real drop elsewhere in the sum.</summary>
    private static long RedactedBytesOf(NativeRecordV1 record) =>
        record.Redaction == NativeRecordRedaction.None ? 0 : Math.Max(0, record.ByteLength - record.SizeBytes);

    private static void EnsureResumable(HarnessReductionCheckpointV1 resumeFrom)
    {
        ArgumentNullException.ThrowIfNull(resumeFrom);

        // A checkpoint written by a DIFFERENT reduction has a different state shape behind the same field names, so
        // resuming from it would produce a state that is internally consistent and quietly wrong.
        if (!string.Equals(resumeFrom.ReducerKind, ReducerKind, StringComparison.Ordinal))
            throw new ArgumentException($"cannot resume '{ReducerKind}' from a '{resumeFrom.ReducerKind}' checkpoint", nameof(resumeFrom));

        var errors = resumeFrom.Validate();

        if (errors.Count > 0)
            throw new ArgumentException($"checkpoint is not resumable: {string.Join("; ", errors)}", nameof(resumeFrom));
    }

    private void ReduceRecord(NativeRecordV1 record)
    {
        _records++;
        _redactedBytes += RedactedBytesOf(record);
        _prefixDigest = ChainDigest(_prefixDigest, record);

        RecordChannel(record.Channel);
    }

    /// <summary>The TALLIES count every projection, so "the prefix contained a guess" stays answerable; the NAMED facts are taken only from an exactly-grounded one.</summary>
    private void ReduceProjection(AgentSemanticEventV1 projection)
    {
        _projections++;

        if (projection.Necessity == SemanticEventNecessity.Required) _required++;

        if (!projection.ProjectionQuality.IsExactlyGrounded()) return;

        _exactlyGrounded++;

        ReduceGroundedFacts(projection);
    }

    /// <summary>
    /// The facts that NAME something, taken only from a projection that is what the harness said. A session id pattern-matched
    /// out of stdout is <see cref="SemanticProjectionQuality.Heuristic"/> and one with no established provenance is
    /// <see cref="SemanticProjectionQuality.Unknown"/>; letting either set <see cref="HarnessReducedStateV1.FirstSessionId"/> would
    /// hand a warm resume a guess wearing the shape of an established fact, which is exactly the distinction the quality
    /// vocabulary exists to keep. <see cref="HarnessReducedStateV1.ExactlyGroundedProjections"/> cannot recover it afterwards —
    /// it is one aggregate over the whole prefix, so it can never say WHICH field was inferred.
    /// </summary>
    private void ReduceGroundedFacts(AgentSemanticEventV1 projection)
    {
        _firstSessionId ??= projection.SessionId;
        _firstModelCallId ??= projection.ModelCallId;
        _lastModelCallId = projection.ModelCallId ?? _lastModelCallId;

        if (projection.Necessity != SemanticEventNecessity.Required) return;

        _lastRequiredEventType = projection.EventType;
    }

    private void RecordChannel(NativeRecordChannel channel)
    {
        if (_channelKeys.Add(channel)) _channelsSeen.Add(channel);
    }

    /// <summary>Ordered by stream id so the same consumed prefix always has the same representation — the property the stored frontier's monotonicity guard and this fold's own equality both read.</summary>
    private HarnessReductionPosition MaterializePosition() => new()
    {
        Streams = _frontier.OrderBy(entry => entry.Key).Select(entry => new HarnessStreamPosition { StreamId = entry.Key, NextOrdinal = entry.Value }).ToArray(),
    };

    private HarnessReducedStateV1 MaterializeState() => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion,
        RecordsConsumed = _records,
        ProjectionsConsumed = _projections,
        ExactlyGroundedProjections = _exactlyGrounded,
        RequiredProjections = _required,
        ChannelsSeen = _channelsSeen.ToArray(),
        FirstSessionId = _firstSessionId,
        FirstModelCallId = _firstModelCallId,
        LastModelCallId = _lastModelCallId,
        LastRequiredEventType = _lastRequiredEventType,
        RedactedByteCount = _redactedBytes,
        PrefixDigest = Convert.ToHexString(_prefixDigest).ToLowerInvariant(),
    };
}
