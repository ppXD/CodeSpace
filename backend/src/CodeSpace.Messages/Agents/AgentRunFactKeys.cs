namespace CodeSpace.Messages.Agents;

/// <summary>
/// WHERE one harness's native stream spells the three run facts the platform reads off EVERY run — the session/thread
/// id a warm retry resumes from, the token usage the run is billed by, and the model it actually ran. The three shared
/// readers own the SCAN (which payloads, in which order, first-wins vs last-wins); this record is the only thing about
/// them that is per-harness, so the adapter that owns the format owns its own spellings.
///
/// <para><b>How a lookup uses it.</b> A fact is looked for in the payload root FIRST, then in each declared container
/// in the order given — a dotted path (<c>info.total_token_usage</c>) walked segment by segment, skipped unless every
/// segment resolves to an object. <see cref="Envelopes"/> are the containers searched for the id and the model,
/// <see cref="UsageContainers"/> the ones searched for a usage object; they are separate because a stream may wrap a
/// usage block somewhere it never wraps an id, and a shared list would make each fact answerable from the other's
/// nesting. An empty key list means "this stream never states this fact" — a legitimate declaration, not an omission.</para>
///
/// <para><b>The obligation that comes with declaring.</b> These keys are read off an event's structured payload only,
/// so an adapter earns them by retaining the fact-bearing native line's structured root (or a sub-object containing
/// the fact) as that event's <c>Data</c>. A parse that keeps only the human line contributes no facts however
/// carefully it declares them.</para>
/// </summary>
public sealed record AgentRunFactKeys
{
    /// <summary>Keys, in priority order, whose string value is the harness-native session/thread id.</summary>
    public IReadOnlyList<string> SessionIdKeys { get; init; } = Array.Empty<string>();

    /// <summary>Keys, in priority order, whose string value names the model the run actually used.</summary>
    public IReadOnlyList<string> ModelKeys { get; init; } = Array.Empty<string>();

    /// <summary>Keys, in priority order, whose number is the input/prompt token count.</summary>
    public IReadOnlyList<string> InputTokenKeys { get; init; } = Array.Empty<string>();

    /// <summary>Keys, in priority order, whose number is the output/completion token count.</summary>
    public IReadOnlyList<string> OutputTokenKeys { get; init; } = Array.Empty<string>();

    /// <summary>Dotted paths, searched after the payload root in the order given, that may WRAP an id-bearing or model-bearing payload.</summary>
    public IReadOnlyList<string> Envelopes { get; init; } = Array.Empty<string>();

    /// <summary>Dotted paths, searched after the payload root in the order given, that may hold a usage object. The order decides what a line carrying two usage objects is billed for — the first location holding BOTH counts wins — so a cumulative total belongs ahead of a per-turn delta.</summary>
    public IReadOnlyList<string> UsageContainers { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The table used for a harness that declares none of its own: the UNION of the spellings the two shipped adapters
    /// (<c>ClaudeCodeHarness</c>, <c>CodexHarness</c>) use, plus the token aliases neither of them emits — the
    /// OpenAI-API <c>prompt_tokens</c>/<c>completion_tokens</c> and the bare <c>input</c>/<c>output</c> — that a
    /// gateway-fronted CLI might. It exists for compatibility only: it is what the readers held before a harness could
    /// declare anything, kept verbatim (keys and container order alike) so an adapter that declares nothing extracts
    /// exactly what it extracted before. It is NOT a discovery mechanism — a stream that spells any of the three some
    /// other way yields null here, which is why an undeclared harness's missing fact is reported as unestablished
    /// rather than as an absence the stream stated.
    ///
    /// <para>The word UNION above is enforced, not asserted:
    /// <c>AgentRunFactKeysTests.The_fallback_union_holds_every_shipped_adapters_keys_in_the_same_order</c> fails when a
    /// shipped adapter declares a key this table lacks, or in a different relative order. Order matters most for
    /// <see cref="UsageContainers"/>, where it decides which of two usage objects on one line a run is billed for, so a
    /// key added here goes at the END unless a re-price is the deliberate point of the change.</para>
    /// </summary>
    public static readonly AgentRunFactKeys Fallback = new()
    {
        SessionIdKeys = new[] { "session_id", "thread_id" },
        ModelKeys = new[] { "model", "model_name" },
        InputTokenKeys = new[] { "input_tokens", "prompt_tokens", "input" },
        OutputTokenKeys = new[] { "output_tokens", "completion_tokens", "output" },
        Envelopes = new[] { "msg" },
        UsageContainers = new[] { "usage", "info.total_token_usage", "info", "total_token_usage", "msg.usage", "msg" },
    };
}
