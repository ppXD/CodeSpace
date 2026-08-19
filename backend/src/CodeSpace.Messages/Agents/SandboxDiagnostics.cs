namespace CodeSpace.Messages.Agents;

/// <summary>
/// One delivery from a run's diagnostic stream: the text, and whether it is a WHOLE diagnostic. False only where the reader had to cut a line no single pass could terminate, so a continuation follows; the source's own final line is whole even when it carries no terminator, because nothing was cut from it.
///
/// <para><b>Why the flag exists.</b> A bounded reader works in passes, and a line longer than one pass is a line no
/// pass can terminate. Delivering nothing for it stops the drain at that byte for good; delivering it as if it were a
/// whole line hands the caller two records that read like two diagnostics the harness never wrote. So the reader
/// delivers what it has and SAYS it is a cut, and a caller that turns these into records records this one as the
/// partial it is. The remainder is the next delivery, from the answered offset.</para>
/// </summary>
public readonly record struct SandboxDiagnosticLine(string Text, bool IsComplete);

/// <summary>
/// What one drain of a diagnostic stream may cost its caller, in BOTH dimensions that cost is paid in.
///
/// <para><see cref="MaxLines"/> bounds how many lines are delivered — the ROW count, where each line becomes a durable
/// row. <see cref="MaxBytes"/> bounds how many source bytes are read to produce them, which is the dimension a line
/// budget alone leaves wide open: two thousand lines of a megabyte each is two thousand rows and two gigabytes of
/// payload. A caller whose per-line work is a write pays in both, so it must be able to bound both.</para>
///
/// <para>Whichever is reached first stops the drain, and the answered offset is where the next one resumes — an
/// exhausted budget is a position, not a loss.</para>
/// </summary>
public sealed record SandboxDiagnosticBudget
{
    /// <summary>Most lines this drain delivers.</summary>
    public required int MaxLines { get; init; }

    /// <summary>Most source bytes this drain reads. It bounds each single read too, not just their sum, so a drain never reads past its budget in order to finish a line.</summary>
    public required int MaxBytes { get; init; }
}
