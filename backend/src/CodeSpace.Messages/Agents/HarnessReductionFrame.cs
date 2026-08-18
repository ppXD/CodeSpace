namespace CodeSpace.Messages.Agents;

/// <summary>What a reduction did with one frame it was handed.</summary>
public enum HarnessFrameDisposition
{
    /// <summary>The frame was at the reduction's frontier and has been folded into its state.</summary>
    Reduced,

    /// <summary>The frame is behind the frontier — it was already folded, so it was skipped rather than folded twice. This is what makes a crash between consuming and checkpointing safe to replay.</summary>
    AlreadyReduced,
}

/// <summary>
/// ONE unit of reduction: a native record together with the semantic projections folded from it. The two travel as
/// one because position lives on the RECORD — <see cref="NativeRecordV1.StreamId"/> plus
/// <see cref="NativeRecordV1.Ordinal"/> — while a projection has no ordinal of its own, only the source record ids it
/// cites. Delivering a projection apart from its record would leave a reduction unable to say whether it had consumed
/// that projection or not, and therefore unable to resume without either losing it or counting it twice.
///
/// <para>A projection folded from SEVERAL records rides on the frame of the record that completed it, and still cites
/// the earlier ones. <see cref="Validate"/> insists every cited-anything projection cites THIS record, so a
/// projection cannot be attributed to a frame it did not complete — the shape of a double count.</para>
/// </summary>
public sealed record HarnessReductionFrame
{
    /// <summary>The captured native frame, whose stream and ordinal are this frame's position.</summary>
    public required NativeRecordV1 Record { get; init; }

    /// <summary>The projections this record completed, in source order. Empty is normal: most records project to nothing.</summary>
    public required IReadOnlyList<AgentSemanticEventV1> Projections { get; init; }

    /// <summary>Every reason this frame cannot be folded. Empty ⇒ readable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        errors.AddRange(Record.Validate().Select(error => $"record: {error}"));

        for (var index = 0; index < Projections.Count; index++)
        {
            var projection = Projections[index];

            errors.AddRange(projection.Validate().Select(error => $"projection[{index}]: {error}"));

            if (projection.SourceNativeRecordIds.Count > 0 && !projection.SourceNativeRecordIds.Contains(Record.RecordId))
                errors.Add($"projection[{index}]: cites source records but not the record it arrived with");
        }

        return errors;
    }
}
