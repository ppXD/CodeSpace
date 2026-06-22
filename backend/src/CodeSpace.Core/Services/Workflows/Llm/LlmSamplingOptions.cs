namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Optional generation knobs shared by every LLM request record — added in ONE place so a new knob is a single field
/// here, not N duplicated across each request type + caller. Every field is NULLABLE and omitted from the wire when
/// null (so a caller opts in only where it cares, and a gateway that doesn't support a knob is never sent a key it
/// would 400 on). Each client maps these onto the params ITS API actually supports: Anthropic honours <c>top_p</c> +
/// <c>stop_sequences</c> (it has NO frequency/presence penalty — sending them would error), OpenAI honours all four.
/// <c>Temperature</c> + <c>MaxOutputTokens</c> stay on the request records themselves (they are always-set with a
/// default); these are the strictly-optional refinements.
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
