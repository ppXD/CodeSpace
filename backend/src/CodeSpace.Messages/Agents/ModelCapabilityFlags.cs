namespace CodeSpace.Messages.Agents;

/// <summary>
/// The per-model capability boundary the supervisor/agent scheduling reads (Rule 18.1, a pure data noun). Three
/// flags, all default false = "declares nothing" — a safe floor. A capability is the MODEL's: not the credential's
/// (one key backs models of differing capability) nor the harness's (the harness only constrains which models it can
/// invoke). A new orthogonal capability (vision, long-context, effort-depth) arrives as a new flag here — or a
/// sibling type (Rule 7) — only when a real reader lands, never by overloading one of these.
/// </summary>
public sealed record ModelCapabilityFlags
{
    /// <summary>The model can return structured / JSON-schema output — the decider-eligibility gate.</summary>
    public bool SupportsStructuredOutput { get; init; }

    /// <summary>The model is suitable for tool-using agents — suitability, not a claim about native tool APIs (the harness often provides the tools).</summary>
    public bool SupportsToolUse { get; init; }

    /// <summary>A high-trust model recommended to drive the supervisor decider — a scheduling tie-break, never a hard gate.</summary>
    public bool RecommendedForSupervisor { get; init; }
}
