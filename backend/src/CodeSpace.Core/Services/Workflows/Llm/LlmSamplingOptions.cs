namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Optional generation knobs shared by every LLM request record — added in ONE place so a new knob is a single field
/// here, not N duplicated across each request type + caller. Every field is NULLABLE and omitted from the wire when
/// null (so a caller opts in only where it cares, and a gateway that doesn't support a knob is never sent a key it
/// would 400 on). Each client maps these onto the params ITS API actually supports: Anthropic honours <c>top_p</c> +
/// <c>stop_sequences</c> (it has NO frequency/presence penalty — sending them would error), OpenAI honours all four.
/// <c>Temperature</c> stays on the request records themselves (nullable — omitted when null, the "let the model decide"
/// default); <c>MaxOutputTokens</c> is always sent (Anthropic requires it). <c>top_p</c> + the penalties here, like a
/// pinned <c>temperature</c>, are DROPPED by the transport for a reasoning-tier model that rejects them (see
/// <see cref="LlmModelCapabilities"/>); <c>stop_sequences</c> survive.
/// </summary>
public sealed record LlmSamplingOptions
{
    /// <summary>Nucleus sampling (0..1). Both providers.</summary>
    public double? TopP { get; init; }

    /// <summary>Penalise token frequency (-2..2). OpenAI-wire only (Anthropic has no equivalent → dropped for that client).</summary>
    public double? FrequencyPenalty { get; init; }

    /// <summary>Penalise token presence (-2..2). OpenAI-wire only.</summary>
    public double? PresencePenalty { get; init; }

    /// <summary>Stop sequences — generation halts when any is produced. Both providers (Anthropic <c>stop_sequences</c>, OpenAI <c>stop</c>).</summary>
    public IReadOnlyList<string>? Stop { get; init; }
}
