using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// How far a reduction has consumed ONE capture stream. <see cref="NextOrdinal"/> is the zero-based ordinal the next
/// record on that stream must carry, so it is simultaneously the count of records already folded from it — a stream
/// at <c>3</c> has folded ordinals 0, 1 and 2 and nothing else. That identity is what lets a durable checkpoint's
/// record count be CHECKED against its frontier rather than merely believed.
/// </summary>
public sealed record HarnessStreamPosition
{
    /// <summary>The capture stream this frontier belongs to — <see cref="NativeRecordV1.StreamId"/>.</summary>
    public required Guid StreamId { get; init; }

    /// <summary>Zero-based ordinal the next record on this stream must carry; equally, how many have been folded.</summary>
    public required long NextOrdinal { get; init; }
}

/// <summary>
/// The exact prefix a reduction has consumed, as one frontier PER STREAM. It is not a single scalar because
/// <see cref="NativeRecordV1.Ordinal"/> is deliberately per stream and never global — a global sequence would force
/// every channel a harness speaks at once to serialise through one writer.
///
/// <para><b>The representation is canonical</b>: <see cref="Streams"/> is ordered by <see cref="HarnessStreamPosition.StreamId"/>
/// and carries each stream at most once, and <see cref="Validate"/> refuses anything else. Two reductions that have
/// consumed the same prefix therefore have byte-identical JSON, which is what makes a stored position comparable at
/// all — the database's monotonicity guard and the reducer's own resume both read this shape.</para>
///
/// <para><b>What it does NOT establish</b>: the order records are folded in ACROSS streams. This value pins the
/// per-stream prefix only; the interleaving is the record source's own deterministic total order, and a source that
/// changes it between reads changes the answer any order-dependent reduction gives. That obligation belongs to the
/// source, and no shape here can discharge it.</para>
/// </summary>
public sealed record HarnessReductionPosition
{
    /// <summary>The frontier of a reduction that has consumed nothing.</summary>
    public static HarnessReductionPosition Empty { get; } = new() { Streams = Array.Empty<HarnessStreamPosition>() };

    /// <summary>Per-stream frontiers, ordered by stream id and each stream present at most once.</summary>
    public required IReadOnlyList<HarnessStreamPosition> Streams { get; init; }

    /// <summary>Total records this frontier accounts for. Not serialized: the count is stored once, on the checkpoint's own column and in the reduced state, and a third writable copy inside the frontier would be one more thing to disagree.</summary>
    [JsonIgnore]
    public long RecordsConsumed => Streams.Sum(stream => stream.NextOrdinal);

    /// <summary>The ordinal the next record on <paramref name="streamId"/> must carry — zero for a stream this reduction has never seen.</summary>
    public long NextOrdinalOf(Guid streamId)
    {
        foreach (var stream in Streams)
        {
            if (stream.StreamId == streamId) return stream.NextOrdinal;
        }

        return 0;
    }

    /// <summary>Every reason this frontier cannot be trusted as a consumed prefix. Empty ⇒ readable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        for (var index = 0; index < Streams.Count; index++)
        {
            var stream = Streams[index];

            if (stream.StreamId == Guid.Empty)
                errors.Add("streamId must be non-empty");
            if (stream.NextOrdinal < 0)
                errors.Add($"nextOrdinal for stream '{stream.StreamId}' must be non-negative");
            if (index > 0 && Streams[index - 1].StreamId.CompareTo(stream.StreamId) >= 0)
                errors.Add("streams must be ordered by streamId and each stream may appear at most once");
        }

        return errors;
    }

    /// <summary>Value equality that compares <see cref="Streams"/> ELEMENT-WISE. The generated record equality compares the list by reference, so a frontier and its own round trip through storage would never compare equal — which is exactly the comparison a resume has to make.</summary>
    public bool Equals(HarnessReductionPosition? other) => other is not null && Streams.SequenceEqual(other.Streams);

    /// <summary>Hashes the count and length, both of which equal lists agree on — the whole contract a hash owes.</summary>
    public override int GetHashCode() => HashCode.Combine(Streams.Count, RecordsConsumed);
}
