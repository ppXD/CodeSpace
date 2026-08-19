namespace CodeSpace.Messages.Agents;

/// <summary>
/// ONE model call a harness STATED in one of its own structured frames — read out of the frame that IS the harness's
/// record of a provider response, never out of a line that happens to mention a model.
///
/// <para><b>Why the distinction is the whole type.</b> A harness CLI talks to the provider itself, so no
/// <c>ILLMClient</c> and no recording decorator ever sees its calls; all the platform keeps is one summed token figure
/// per run. That makes a frame the only possible evidence — and it makes the difference between two kinds of frame
/// load bearing. A frame that IS the harness's own response record states the model, the call's identity and its usage
/// as fields; a frame that merely NAMES a model (a session/init line announcing the configured model, an assistant
/// sentence quoting one) states nothing about any particular call. Projecting the second would put a fabricated row in
/// a cost report, and a cost report that is quietly wrong is trusted — so the second yields nothing at all.</para>
///
/// <para><b>What a harness may not do with it.</b> Return it for a frame whose figures it inferred, summed, or carried
/// over from another frame. A cumulative per-turn total is not this: it is the sum of calls the harness never
/// enumerated, so reporting it here would claim one call made every token of a turn. A harness whose stream carries no
/// per-call record simply does not implement <c>IAgentModelCallFrameReader</c>, and this plane then records nothing for
/// it — which is the correct outcome, and strictly better than a row it invented.</para>
///
/// <para>Only the four fields a row cannot be honest without are required. Everything else is nullable and null MEANS
/// the record did not state it: the projection turns each absence into an explicitly declared unavailable figure, so
/// no reader can mistake a figure nobody reported for one measured at zero.</para>
/// </summary>
public sealed record GroundedModelCallFrame
{
    /// <summary>
    /// The harness's OWN identity for this response — the id it printed, not one this platform minted. It is what
    /// makes the projection idempotent: two frames carrying the same id are the same provider response (a re-delivered
    /// frame), and two genuinely different calls never share one. A record that states no id cannot be deduplicated
    /// and is therefore not projected at all, because a duplicated cost is worse than a missing one.
    /// </summary>
    public required string CallId { get; init; }

    /// <summary>The model the response was served BY, as the harness named it. The row's effective model — never the requested one, which a harness's own response record does not state.</summary>
    public required string Model { get; init; }

    /// <summary>Prompt tokens the record states. Required: a usage-less frame is not a usable per-call record, and a zero written for a figure nobody reported reads as measured.</summary>
    public required int InputTokens { get; init; }

    /// <summary>Completion tokens the record states, on the same terms as <see cref="InputTokens"/>.</summary>
    public required int OutputTokens { get; init; }

    /// <summary>Cache-read tokens, or null when this record does not report them — which the projection records as an unavailable figure rather than as zero.</summary>
    public int? CacheReadTokens { get; init; }

    /// <summary>Cache-write tokens, on the same terms as <see cref="CacheReadTokens"/>.</summary>
    public int? CacheWriteTokens { get; init; }

    /// <summary>The provider's stop reason as the harness printed it, or null when the record states none.</summary>
    public string? FinishReason { get; init; }
}
