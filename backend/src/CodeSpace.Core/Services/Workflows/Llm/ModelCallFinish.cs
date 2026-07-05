namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>How a model completion STOPPED, on the legibility axis — the ONE classification both the model-call timeline beat and the model-call fact row read, so a truncated / filtered completion can't read one way on the beat and another on the row.</summary>
public enum ModelCallFinishKind
{
    /// <summary>The model stopped on its own (end_turn / stop / tool_use / tool_calls / stop_sequence / no reported reason) — the answer is complete.</summary>
    Clean,

    /// <summary>The output hit the token ceiling (Anthropic <c>max_tokens</c> / OpenAI <c>length</c>) — the answer was CUT OFF mid-generation; a downstream reader must not treat it as complete.</summary>
    Truncated,

    /// <summary>The provider's content filter blocked the output (<c>content_filter</c>) — the answer is incomplete for a policy reason.</summary>
    Filtered,
}

/// <summary>
/// Classifies a provider finish reason (Anthropic <c>stop_reason</c> / OpenAI <c>finish_reason</c>, as recorded on the
/// completion's <c>usage.finishReason</c>) into the legibility axis — so a length-capped or content-filtered completion
/// reads distinctly from a clean one, instead of every completed call rendering the same neutral "Model call". The ONE
/// authority both <c>RunRecordTimelineMap</c> (the timeline beat) and <c>ModelCallFactsSource</c> (the fact row) read,
/// mirroring how <c>SupervisorOutcome.ClassifyStop</c> is the single stop-classification authority.
/// </summary>
public static class ModelCallFinish
{
    /// <summary>Classify a provider finish reason. Case-insensitive; a length-cap (max_tokens / length) is TRUNCATED, a content_filter is FILTERED, and everything else — the clean stops, a null (unreported), or an unknown future reason — is CLEAN, so a new provider reason never false-alarms.</summary>
    public static ModelCallFinishKind Classify(string? finishReason) => finishReason?.Trim().ToLowerInvariant() switch
    {
        "max_tokens" or "length" => ModelCallFinishKind.Truncated,
        "content_filter" => ModelCallFinishKind.Filtered,
        _ => ModelCallFinishKind.Clean,
    };

    /// <summary>A short human qualifier for the beat title / the fact status — null for a clean stop (no qualifier).</summary>
    public static string? Qualifier(ModelCallFinishKind kind) => kind switch
    {
        ModelCallFinishKind.Truncated => "output truncated",
        ModelCallFinishKind.Filtered => "content filtered",
        _ => null,
    };
}
