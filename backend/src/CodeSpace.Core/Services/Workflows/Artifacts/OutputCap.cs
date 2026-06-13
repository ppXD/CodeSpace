namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Bounds a large text value to a readable preview when it exceeds a character budget — keeping the HEAD and
/// TAIL (where the signal usually is: a command's first lines + its final error) with a marker naming how much
/// was dropped. The reusable result-shedding primitive node/agent output paths share to stop unbounded blobs
/// (build logs, huge stdout) from bloating the run state.
///
/// Pure + deterministic, so a capped value is byte-stable across durable replay. It never GROWS a value: if the
/// preview-plus-marker wouldn't actually be smaller than the original, the original is returned untruncated.
/// Full-content offload to the artifact store lands when external artifact storage is wired (today's store is
/// inline-only); until then capping keeps the head/tail rather than persisting the whole blob.
/// </summary>
public static class OutputCap
{
    /// <summary>The outcome of a cap: the (possibly previewed) text, the original length, and whether it was truncated.</summary>
    public sealed record Result
    {
        public required string Text { get; init; }
        public required int OriginalLength { get; init; }
        public required bool Truncated { get; init; }
    }

    /// <summary>
    /// Caps <paramref name="text"/> to roughly <paramref name="maxChars"/> visible characters (a head-heavy
    /// head+tail split plus a small omitted-count marker). <paramref name="maxChars"/> &lt;= 0 → no cap. Null →
    /// empty. A value already within budget — or one a cap wouldn't actually shrink — is returned verbatim.
    /// </summary>
    public static Result Apply(string? text, int maxChars)
    {
        text ??= "";

        if (maxChars <= 0 || text.Length <= maxChars)
            return new Result { Text = text, OriginalLength = text.Length, Truncated = false };

        var omitted = text.Length - maxChars;
        var marker = $"\n\n…[{omitted} of {text.Length} chars omitted]…\n\n";

        var headLen = Math.Max(1, maxChars * 2 / 3);
        var tailLen = maxChars - headLen;

        var preview = tailLen > 0 ? text[..headLen] + marker + text[^tailLen..] : text[..headLen] + marker;

        // Never grow the value — for a small overflow the marker can exceed what it saves.
        if (preview.Length >= text.Length)
            return new Result { Text = text, OriginalLength = text.Length, Truncated = false };

        return new Result { Text = preview, OriginalLength = text.Length, Truncated = true };
    }
}
