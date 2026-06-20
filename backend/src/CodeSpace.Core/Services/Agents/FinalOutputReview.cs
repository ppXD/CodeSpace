using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Slice A2 — the BEST-EFFORT half of the completion contract (the A1 gate in <see cref="AgentCompletionContract"/> is
/// the HARD half). When a would-be-success run's FINAL message reads as an unresolved question handed back to the human
/// — the agent ended by ASKING rather than calling <c>decision.request</c> (the H3 gap) — re-grade it to
/// <see cref="AgentRunStatus.NeedsReview"/> / <see cref="CompletionDisposition.NeedsReview"/> so a person resolves it,
/// instead of the ask surviving only as unparsed summary text under a green "Succeeded".
///
/// <para>A1 (a real raised-but-unanswered decision) ALWAYS takes precedence — it fires first, and this only runs while
/// the result is still Succeeded, so a concrete decision outranks this heuristic guess. This is a TEXT heuristic over
/// the agent's own words: it can false-positive (a rhetorical closing question) and false-negate (an ask not phrased as
/// a question), so it is OPT-IN (Rule 8, default-OFF) — an operator enables it when they want the net, and the SOTA
/// upgrade is an LLM judge (human-gated; no model egress here). The detection (<see cref="EndsWithUnresolvedQuestion"/>)
/// is PURE + harness-agnostic — it reads only the normalized <c>AgentRunResult.Summary</c> both harnesses populate — so
/// it unit-tests exhaustively; only <see cref="Enabled"/> reads the environment.</para>
/// </summary>
public static class FinalOutputReview
{
    /// <summary>Operator opt-in for the best-effort final-output review (Rule 8). Default-OFF. Pinned by a unit test — a rename silently disables the net for any operator who enabled it.</summary>
    public const string EnabledEnvVar = "CODESPACE_AGENT_FINAL_OUTPUT_REVIEW_ENABLED";

    /// <summary>True ONLY for "1"/"true"/"TRUE" (trimmed); default-OFF (no re-grade) otherwise. Mirrors the other agent opt-in flags exactly (Rule 8).</summary>
    public static bool Enabled
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(EnabledEnvVar)?.Trim();
            return raw is "1" or "true" or "TRUE";
        }
    }

    // Curated, high-precision hand-back phrases: a CLOSING line containing one reads as "I need YOU to decide / act
    // before this is done", even without a trailing '?'. Lower-case; matched case-insensitively against the last line.
    // Deliberately tight (no bare "let me know") to keep false positives low; the behavioural boundary is pinned by tests.
    private static readonly string[] HandbackPhrases =
    {
        "please confirm", "please advise", "let me know which", "let me know how you",
        "which would you prefer", "do you want me to", "would you like me to",
        "should i proceed", "shall i proceed", "should i continue",
        "waiting for your", "awaiting your", "need your decision", "need your confirmation", "need your input",
    };

    /// <summary>
    /// Re-grade a would-be-success result whose final output reads as an unresolved question. A
    /// <see cref="AgentRunStatus.Succeeded"/> result whose summary <see cref="EndsWithUnresolvedQuestion"/> becomes
    /// <see cref="AgentRunStatus.NeedsReview"/> / <see cref="CompletionDisposition.NeedsReview"/> (exit reason
    /// <c>needs-review</c>), with the captured work preserved; every other case passes through reference-unchanged.
    /// Pure — the caller gates the CALL on <see cref="Enabled"/>.
    /// </summary>
    public static AgentRunResult ReGrade(AgentRunResult result)
    {
        if (result.Status != AgentRunStatus.Succeeded || !EndsWithUnresolvedQuestion(result.Summary)) return result;

        return result with
        {
            Status = AgentRunStatus.NeedsReview,
            CompletionDisposition = CompletionDisposition.NeedsReview,
            ExitReason = "needs-review",
        };
    }

    /// <summary>
    /// Best-effort: does the agent's final message END on an unresolved question handed to the human? Conservative —
    /// scoped to the LAST non-empty line (the agent's parting words), so a question resolved earlier in the body does
    /// not trip it. True when that line ends with '?' (after stripping trailing markdown / quote wrappers) OR contains a
    /// curated hand-back phrase. Pure; a null / blank summary ⇒ false.
    /// </summary>
    public static bool EndsWithUnresolvedQuestion(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return false;

        var lastLine = summary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

        if (string.IsNullOrEmpty(lastLine)) return false;

        // A markdown SECTION HEADER ending in '?' ("## Next steps?", "## Questions?") is a heading, not a hand-back to the
        // human — and a very common CLOSING pattern for the harnesses this reads, so flagging it would re-grade a clean
        // success. Skip a parting line that's a header. A genuine ask phrased as prose ("Should I proceed?") has no
        // leading '#' and is still caught.
        if (lastLine.TrimStart().StartsWith('#')) return false;

        var tail = lastLine.TrimEnd('"', '\'', '`', ')', ']', '*', '_', ' ', '\t');

        if (tail.EndsWith('?')) return true;

        return HandbackPhrases.Any(p => lastLine.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}
