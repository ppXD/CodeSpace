using System.Collections.Generic;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Cause-aware retry (the 2026-08-30 wedge's L3 layer): classify a prior attempt's terminal error into the retry
/// dispositions that need to DIFFER from the default resume-and-continue. The marker vocabulary is CLOSED and
/// deliberately tight — only failure shapes seen live and understood mechanically are pinned here; it grows by PR,
/// never by fuzzy matching (an over-broad marker would silently strip resume continuity from ordinary failures).
/// </summary>
public static class AgentRetryCauses
{
    /// <summary>The gateway mangled the Anthropic wire FORMAT (e.g. thinking-block continuation) — deterministic on replay: resuming the conversation re-sends the very history that re-triggers it, so the retry must start FRESH, with extended thinking disabled.</summary>
    public const string GatewayFormatFault = "gateway-format-fault";

    /// <summary>The env var the claude CLI reads as its extended-thinking budget — 0 disables thinking entirely. Pinned by test (Rule 8): the retry degrade writes it into the task environment, and a rename here would silently un-degrade every format-fault retry.</summary>
    public const string MaxThinkingTokensEnvVar = "MAX_THINKING_TOKENS";

    /// <summary>Seen live 2026-08-30 (run wedge postmortem): the gateway's Anthropic-compat layer broke thinking-block continuation and killed the agent tail with exactly this text.</summary>
    private static readonly string[] FormatFaultMarkers = { "is not a thinking block" };

    /// <summary>The prior attempt's retry-relevant cause, or null for every ordinary failure (default resume semantics stand unchanged).</summary>
    public static string? Classify(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;

        foreach (var marker in FormatFaultMarkers)
            if (error.Contains(marker, StringComparison.OrdinalIgnoreCase)) return GatewayFormatFault;

        return null;
    }

    /// <summary>A copy of the task environment with extended thinking disabled — the degrade a format-fault retry runs under (harmless on a harness that ignores the variable).</summary>
    public static IReadOnlyDictionary<string, string> WithThinkingDisabled(IReadOnlyDictionary<string, string> environment)
    {
        var copy = new Dictionary<string, string>(environment) { [MaxThinkingTokensEnvVar] = "0" };
        return copy;
    }
}
