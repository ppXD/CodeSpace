using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Review;

/// <summary>
/// An artifact too large for one review call, reviewed in parts the reviewer model's window holds. The reviewed party
/// decides how large its change is, so a review sized to one call failed whenever it chose a large change — and a
/// review that cannot run now holds the result, so an honest large change (a regenerated lockfile, generated code, a
/// long report) could never be approved. A part is sized from the picked row's declared window by the repository's own
/// estimator (<see cref="LlmContextWindowBudget"/>), so no artifact the render admits can push a call past it; a row
/// that declares none gets <see cref="DefaultPartBytes"/>. Every part must approve for the whole to; the issues of every
/// part ride the merged verdict.
/// </summary>
internal static class CriticArtifactParts
{
    /// <summary>The most review calls one artifact may take. Past it the review does not run, and the caller holds the result for a human.</summary>
    internal const int MaxParts = 8;

    /// <summary>The UTF-8 bytes of artifact one call carries when the reviewer row declares no window: about 67k tokens by the estimator's bytes/3.</summary>
    internal const int DefaultPartBytes = 200_000;

    /// <summary>The fewest artifact bytes worth one call. A window that leaves less than this beside the prompt's own framing is too small to review the artifact at all.</summary>
    internal const int MinPartBytes = 4_096;

    /// <summary>
    /// How many UTF-8 bytes of artifact one call may carry on a row whose declared window is <paramref name="contextWindowTokens"/>:
    /// the usable share of the window the preflight allows, less the reserved output and everything
    /// <paramref name="framing"/> (the request with an empty artifact) already spends, at the estimator's own three bytes
    /// per token. Null when that leaves less than <see cref="MinPartBytes"/>.
    /// </summary>
    internal static int? PartBytes(int? contextWindowTokens, StructuredLLMCompletionRequest framing)
    {
        if (contextWindowTokens is not > 0) return DefaultPartBytes;

        var usable = (long)contextWindowTokens.Value * LlmContextWindowBudget.UsableCapacityNumerator / LlmContextWindowBudget.UsableCapacityDenominator;
        var output = framing.MaxOutputTokens is > 0 ? framing.MaxOutputTokens.Value : LlmBudgetGuard.DefaultMaxOutputTokensEstimate;
        var bytes = (usable - output - LlmContextWindowBudget.EstimateInputTokens(framing)) * 3;

        return bytes < MinPartBytes ? null : (int)Math.Min(bytes, int.MaxValue);
    }

    /// <summary>
    /// <paramref name="artifact"/> in consecutive parts of at most <paramref name="maxBytes"/> UTF-8 bytes each, cut after
    /// the last line break that fits where there is one, and never inside a surrogate pair. Concatenated, the parts are
    /// the artifact. One part — the artifact itself — when it fits.
    /// </summary>
    internal static IReadOnlyList<string> Split(string artifact, int maxBytes)
    {
        var parts = new List<string>();

        for (var start = 0; start < artifact.Length || parts.Count == 0;)
        {
            var end = FitEnd(artifact, start, maxBytes);

            if (end < artifact.Length && artifact.LastIndexOf('\n', end - 1, end - start) is var lineEnd and >= 0 && lineEnd >= start) end = lineEnd + 1;

            parts.Add(artifact[start..end]);
            start = end;
        }

        return parts;
    }

    /// <summary>
    /// One verdict over every part's. GATE approves only when every part approved; IMPROVE keeps every part's critique
    /// that warrants a revision. Every part's issues ride, and each part's rationale is labelled with its place, so the
    /// feedback a flag feeds back names where the problem is. One part's verdict is returned as it is.
    /// </summary>
    internal static CriticVerdict Merge(ReviewMode mode, IReadOnlyList<CriticVerdict> parts)
    {
        if (parts.Count == 1) return parts[0];

        var issues = parts.SelectMany(part => part.Issues).ToList();
        var rationale = string.Join(" ", parts.Select((part, i) => $"[part {i + 1} of {parts.Count}] {part.Rationale}"));

        if (mode == ReviewMode.Improve)
        {
            var critiques = parts.Select((part, i) => part.Critique is null ? null : $"[part {i + 1} of {parts.Count}] {part.Critique}").OfType<string>().ToList();

            return new CriticVerdict { Mode = ReviewMode.Improve, Critique = critiques.Count == 0 ? null : string.Join("\n\n", critiques), Issues = issues, Rationale = rationale };
        }

        return new CriticVerdict { Mode = ReviewMode.Gate, Approved = parts.All(part => part.Approved), Score = parts.Select(part => part.Score).Min(), Issues = issues, Rationale = rationale };
    }

    /// <summary>The end of the longest run of whole characters from <paramref name="start"/> whose UTF-8 encoding fits <paramref name="maxBytes"/> — at least one character, so a split always progresses.</summary>
    private static int FitEnd(string text, int start, int maxBytes)
    {
        var bytes = 0;
        var at = start;

        while (at < text.Length)
        {
            var pair = char.IsHighSurrogate(text[at]) && at + 1 < text.Length && char.IsLowSurrogate(text[at + 1]);
            var size = pair ? 4 : text[at] < 0x80 ? 1 : text[at] < 0x800 ? 2 : 3;

            if (bytes + size > maxBytes && at > start) break;

            bytes += size;
            at += pair ? 2 : 1;
        }

        return at;
    }
}
