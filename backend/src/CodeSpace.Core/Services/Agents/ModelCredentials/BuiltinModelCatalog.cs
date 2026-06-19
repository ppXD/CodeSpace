namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// The in-code seed of which KNOWN model ids support STRUCTURED / JSON-schema output — the ONE capability the
/// scheduler gates on (the decider / planner / schema-bearing <c>llm.complete</c> need it; <c>ModelPoolSelector</c>'s
/// structured filter reads it). Mirrors the <c>AgentCostPricing</c> static-table + env-override convention (Rule 8):
/// an operator declares a custom / gateway model's structured support via <see cref="StructuredModelsEnvVar"/> WITHOUT
/// a code change, and the const name is pinned by a test. Pure + static — no DB, no I/O — so it is unit-pinned and
/// safe to call from the reflector or a read query.
///
/// <para><b>Unknown → false</b> (the safe floor): a custom / gateway model the seed doesn't know claims no structured
/// capability until an operator declares it. The scheduler reads the flag only as a positive grant, so "unknown"
/// never over-claims — a mislabeled model surfaces as a provider-side failure, never a wrong selection.</para>
/// </summary>
public static class BuiltinModelCatalog
{
    /// <summary>
    /// Operators declare which custom / gateway models support structured output here WITHOUT a code change (Rule 8
    /// escape hatch). Format: a semicolon-separated list of model ids — e.g. <c>"my-co/coder;some-gateway/opus"</c>.
    /// Additive over the seed; blank / malformed entries are ignored (lookup never throws). Pinned by a test.
    /// </summary>
    public const string StructuredModelsEnvVar = "CODESPACE_STRUCTURED_OUTPUT_MODELS";

    /// <summary>The seeded structured-capable ids. Claude (Opus/Sonnet/Haiku) + Fable + GPT-5.4 return schema-valid JSON; the codex-class coding models do NOT (tool-use only, no structured API). Cache-dated to the harness model lists (2026-06).</summary>
    private static readonly IReadOnlySet<string> StructuredSeed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "claude-opus-4-8",
        "claude-opus-4-7",
        "claude-opus-4-6",
        "claude-fable-5",
        "claude-sonnet-4-6",
        "claude-haiku-4-5",
        "gpt-5.4",
    };

    /// <summary>Whether <paramref name="modelId"/> supports structured / JSON-schema output — an env-declared id wins, else the seed, else false (the unknown floor). Pure.</summary>
    public static bool SupportsStructuredOutput(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;

        var id = modelId.Trim();

        return EnvDeclaresStructured(id) || StructuredSeed.Contains(id);
    }

    /// <summary>True when the env override lists this id as structured-capable. Lenient: a blank / malformed table yields no override (falls back to the seed).</summary>
    private static bool EnvDeclaresStructured(string modelId)
    {
        var raw = Environment.GetEnvironmentVariable(StructuredModelsEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return false;

        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(id => string.Equals(id, modelId, StringComparison.OrdinalIgnoreCase));
    }
}
