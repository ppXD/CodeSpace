using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// The in-code capability seed for KNOWN model ids — the library-authored "high trust" source (NOT operator-asserted).
/// Mirrors the <c>AgentCostPricing</c> static-table + env-override convention (Rule 8): the seeded defaults are
/// operator-correctable via <see cref="CapabilityTableEnvVar"/> without a code change (an air-gapped operator declares
/// a custom / gateway model's capabilities there), and the const name is pinned by a test. Pure + static — no DB, no
/// I/O — so it is unit-pinned and safe to call from the reflector or a read query.
///
/// <para><b>Unknown → the all-false floor</b> (declares nothing): a custom / gateway model the seed doesn't know
/// claims no capability until an operator declares it. The floor is safe — the scheduler reads a capability only as a
/// positive grant, so "unknown" never over-claims.</para>
/// </summary>
public static class BuiltinModelCatalog
{
    /// <summary>
    /// Operators declare / correct model capabilities here (custom models, drift) WITHOUT a code change (Rule 8 escape
    /// hatch). Format: a semicolon-separated list of <c>modelId=flags</c>, where flags is any subset of the letters
    /// <c>s</c> (structured output), <c>t</c> (tool use), <c>r</c> (recommended for supervisor) — e.g.
    /// <c>"my-co/coder=st;some-gateway/opus=str"</c>. An entry overrides the seeded default for that id; malformed
    /// entries are skipped (lenient — capability lookup never throws). Pinned by a test.
    /// </summary>
    public const string CapabilityTableEnvVar = "CODESPACE_MODEL_CAPABILITIES";

    private static ModelCapabilityFlags Strong => new() { SupportsStructuredOutput = true, SupportsToolUse = true, RecommendedForSupervisor = true };

    private static ModelCapabilityFlags Capable => new() { SupportsStructuredOutput = true, SupportsToolUse = true };

    private static ModelCapabilityFlags ToolAgent => new() { SupportsToolUse = true };

    /// <summary>The seeded defaults. Strong Claude/Opus-class + Fable are supervisor-recommended; Sonnet/Haiku/GPT-5.x are capable; Codex-class are tool-agent-suitable. Cache-dated to the harness model lists (2026-06).</summary>
    private static readonly IReadOnlyDictionary<string, ModelCapabilityFlags> Seed = new Dictionary<string, ModelCapabilityFlags>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-4-8"] = Strong,
        ["claude-opus-4-7"] = Strong,
        ["claude-opus-4-6"] = Strong,
        ["claude-fable-5"] = Strong,
        ["claude-sonnet-4-6"] = Capable,
        ["claude-haiku-4-5"] = Capable,
        ["gpt-5.4"] = Capable,
        ["gpt-5.4-codex"] = ToolAgent,
        ["gpt-5.3-codex"] = ToolAgent,
    };

    /// <summary>The capability flags for a model id — the env override taking precedence over the seed; an unknown / blank id → the all-false floor (declares nothing). Pure.</summary>
    public static ModelCapabilityFlags For(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return new();

        var id = modelId.Trim();

        if (TryReadEnvOverride(id, out var overridden)) return overridden;

        return Seed.TryGetValue(id, out var seeded) ? seeded : new();
    }

    /// <summary>Read this one id's env override (so tests can set the env then call without a cache to reset). Lenient: a malformed table / entry yields no override (falls back to the seed).</summary>
    private static bool TryReadEnvOverride(string modelId, out ModelCapabilityFlags flags)
    {
        flags = new();

        var raw = Environment.GetEnvironmentVariable(CapabilityTableEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return false;

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=');

            if (eq <= 0) continue;   // no id, or no '=' — skip (lenient)

            if (!string.Equals(entry[..eq].Trim(), modelId, StringComparison.OrdinalIgnoreCase)) continue;

            var letters = entry[(eq + 1)..].Trim();
            flags = new ModelCapabilityFlags
            {
                SupportsStructuredOutput = letters.Contains('s', StringComparison.OrdinalIgnoreCase),
                SupportsToolUse = letters.Contains('t', StringComparison.OrdinalIgnoreCase),
                RecommendedForSupervisor = letters.Contains('r', StringComparison.OrdinalIgnoreCase),
            };
            return true;
        }

        return false;
    }
}
