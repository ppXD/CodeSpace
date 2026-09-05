using CodeSpace.Core.Persistence;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The process's own last words, folded onto a bare exit-code failure text. A CLI can die WITHOUT saying anything on
/// its JSON protocol stream — it prints a plain-text fatal to stderr and exits non-zero — and a harness parser drops
/// every non-JSON line by construction, so the fold has no Error event and no final message to report. The run then
/// lands as "claude exited with code 1", which names nothing: <c>AgentRetryCauses.Classify</c> matches no marker (so a
/// cause-aware retry cannot fire for a shape it would otherwise mitigate), every surface that renders the failure —
/// the journal's agent card via <c>AgentMetricsReader</c>, the Room's diagnostic via the node's failure message —
/// shows the operator a number, and the real-model classifier sees no text to key on. What the process actually said
/// is right there in <c>SandboxResult.Stderr</c> and was simply never folded in.
///
/// <para>Shared because BOTH production harnesses have that same shape, and the wording of the fold must not drift
/// between them (a marker matched in one lane and not the other is worse than no marker at all). Each harness still
/// owns its own exit text — only the "and here is what it said" half lives here.</para>
///
/// <para>Bounded on purpose. Only the TAIL is folded in, in lines and then in characters: a failing process says why
/// at the END of its diagnostics (the same reason the durable runner returns a tail rather than the file), while the
/// run's whole stderr is durable elsewhere and this text lands in a column an operator reads. The character ceiling
/// is set so the WHOLE folded error still clears <c>AgentMetricsReader</c>'s own 400-char card cap, which truncates
/// from the FRONT — a more generous excerpt here would be silently cut on the surface that renders it.</para>
/// </summary>
public static class AgentDiagnosticExcerpt
{
    /// <summary>How many non-empty stderr lines the excerpt keeps — the last few, which is where a dying process puts the fatal.</summary>
    public const int MaxLines = 3;

    /// <summary>Character ceiling for the whole excerpt. Sized so the WHOLE folded error clears <c>AgentMetricsReader</c>'s 400-char card cap even behind the longest exit text a harness can put in front of it — the ~100-char signal decoding <c>SandboxExitCode.Describe</c> emits for a 128+signal kill. Pinned against that worst case by test, because the card truncates from the front: a raised ceiling would cut the reason off the surface an operator reads it on.</summary>
    public const int MaxChars = 250;

    /// <summary>The marker that introduces the folded diagnostics. Pinned by test: it is what a reader scans for and what a claim about this text can key on.</summary>
    public const string Separator = " — stderr: ";

    /// <summary><paramref name="exitText"/> with the tail of <paramref name="diagnostics"/> folded onto it, or <paramref name="exitText"/> unchanged when the process said nothing on stderr.</summary>
    public static string Explain(string exitText, string? diagnostics) => Tail(diagnostics) is { } excerpt ? exitText + Separator + excerpt : exitText;

    /// <summary>The last <see cref="MaxLines"/> non-empty lines, NUL-stripped so the text can be persisted (<see cref="PersistedText.Sanitize"/>) and capped at <see cref="MaxChars"/> from the END. Null when there is nothing meaningful to say.</summary>
    private static string? Tail(string? diagnostics)
    {
        var sanitized = PersistedText.Sanitize(diagnostics);

        if (string.IsNullOrWhiteSpace(sanitized)) return null;

        var lines = sanitized.Split('\n');
        var kept = new List<string>(MaxLines);

        for (var i = lines.Length - 1; i >= 0 && kept.Count < MaxLines; i--)
        {
            var line = lines[i].Trim();

            if (line.Length > 0) kept.Add(line);
        }

        if (kept.Count == 0) return null;

        kept.Reverse();

        var excerpt = string.Join('\n', kept);

        return excerpt.Length <= MaxChars ? excerpt : "…" + excerpt[^MaxChars..];
    }
}
