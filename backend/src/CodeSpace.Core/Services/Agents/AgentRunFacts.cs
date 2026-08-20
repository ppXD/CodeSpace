using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The three run facts every harness is expected to report — token usage, session/thread id, model — folded O(1) out
/// of a stream by the shared readers (<see cref="AgentTokenUsageReader"/>, <see cref="AgentSessionIdReader"/>,
/// <see cref="AgentModelReader"/>), each kept in the direction its whole-list reader used: usage LAST-wins (the
/// newest-first scan), id and model FIRST-wins.
///
/// <para><b>What is harness-independent here, and what is not.</b> The SHAPE is: these three properties, the scan
/// that fills them, and the executor's use of them are the same whatever ran. The SPELLINGS are not, and they are no
/// longer this side's to know: <see cref="For"/> feature-detects <see cref="IAgentHarnessRunFactKeys"/> and hands the
/// harness's own <see cref="AgentRunFactKeys"/> to the readers, so a third harness reaches full fidelity by declaring
/// where its stream puts these three, with no edit to a shared reader. A harness that declares nothing is read with
/// <see cref="AgentRunFactKeys.Fallback"/> — the union the readers used to hold — so it extracts exactly what it did
/// before. Extraction still depends on the payload obligation stated on <see cref="IAgentHarness.ParseEvents"/>:
/// <see cref="Add"/> reads only <see cref="AgentEvent.Data"/>, so a parse that keeps no structured root contributes
/// no facts however it declares them.</para>
///
/// <para><b>Why a missing fact must not stay silent.</b> Null is not rejected anywhere downstream:
/// <c>AgentRunExecutor.BuildReviseTask</c> reads a null <c>SessionId</c> as "cold", so every warm retry silently
/// restarts the conversation from scratch, and a null usage records the run as having burned nothing. For a harness
/// that DECLARED its spellings a null is an absence the harness stated, and stays quiet; for one that declared none
/// it is unestablished — the fallback table simply may not know that stream's spelling — and is reported by
/// <see cref="UnestablishedFacts"/>, which the executor logs at the end of a fold. (Only the model has a second
/// route: <c>IAgentTranscriptModelSource</c>, which the executor consults when the captured model is empty, and only
/// for a harness that implements it.)</para>
///
/// <para><b>Why this is NOT on <see cref="IAgentEventFolder"/>.</b> <c>AgentRunExecutor.MapSandboxResult</c>'s
/// forced-terminal branches (timed out / stalled) never consult the harness — the agent was killed, so there is no
/// exit code to fold — yet the run must still report the tokens it burned and the session id a later RETRY needs to
/// warm-resume the killed conversation. Reading those off the harness's folder would make a fact the executor needs
/// from EVERY harness depend on whether that particular harness's folder happened to keep it, so a folder that legitimately
/// doesn't need the model for its OWN result would silently drop it from every forced terminal. The executor
/// therefore accumulates these itself, alongside the folder, and <c>MapSandboxResult</c> takes them as their own
/// parameter — a sibling accumulator, not a wider seam (Rule 7).</para>
///
/// <para>Single-threaded by contract, like the folder it is driven next to.</para>
/// </summary>
public sealed class AgentRunFacts
{
    private readonly AgentRunFactKeys _keys;
    private readonly bool _declared;

    /// <summary>Facts read with <see cref="AgentRunFactKeys.Fallback"/> — what a harness that declares no spellings of its own gets, and what an empty accumulator (a run whose stream never reached this side) is.</summary>
    public AgentRunFacts() : this(AgentRunFactKeys.Fallback, declared: false) { }

    private AgentRunFacts(AgentRunFactKeys keys, bool declared)
    {
        _keys = keys;
        _declared = declared;
    }

    /// <summary>The single feature-detection point (Rule 7): a harness that declares its own spellings is read with them, one that does not is read with the fallback union and has its missing facts reported by <see cref="UnestablishedFacts"/>.</summary>
    public static AgentRunFacts For(IAgentHarness harness) => harness is IAgentHarnessRunFactKeys declaring ? new AgentRunFacts(declaring.RunFactKeys, declared: true) : new AgentRunFacts();

    /// <summary>The LAST recognizable token usage the stream carried (a cumulative stream's run total), or null when none did — see <see cref="AgentTokenUsageReader"/>.</summary>
    public AgentTokenUsage? TokenUsage { get; private set; }

    /// <summary>The FIRST recognizable harness session/thread id, or null when none was carried — see <see cref="AgentSessionIdReader"/>.</summary>
    public string? SessionId { get; private set; }

    /// <summary>The FIRST recognizable model the CLI named, or null when none was carried — see <see cref="AgentModelReader"/>.</summary>
    public string? Model { get; private set; }

    /// <summary>
    /// The facts that came back null from a harness that declared no spellings — so their absence is UNESTABLISHED
    /// (the vocabulary <see cref="SemanticProjectionQuality.Unknown"/> uses: provenance not established), not an
    /// absence the harness stated. Always empty for a harness that implements <see cref="IAgentHarnessRunFactKeys"/>,
    /// whose nulls are its own statement. Computed on read; the executor logs it once per fold, which is the whole
    /// point of collecting it — nothing else consumes it.
    /// </summary>
    public IReadOnlyList<string> UnestablishedFacts => _declared ? Array.Empty<string>() : MissingFacts();

    /// <summary>Read the structured payload's facts once, keeping each in the direction its whole-list reader used. O(1) retention.</summary>
    public void Add(AgentEvent normalized)
    {
        if (normalized.Data is null) return;

        TokenUsage = AgentTokenUsageReader.TryRead(normalized, _keys) ?? TokenUsage;
        SessionId ??= AgentSessionIdReader.TryRead(normalized, _keys);
        Model ??= AgentModelReader.TryRead(normalized, _keys);
    }

    /// <summary>Fold a stream that is already fully in hand THROUGH THIS HARNESS'S spellings — replay, offline analysis, and tests. The streaming executor calls <see cref="Add"/> per line instead, which is the whole point.</summary>
    public static AgentRunFacts From(IEnumerable<AgentEvent> events, IAgentHarness harness) => Fold(For(harness), events);

    /// <summary>The same whole-stream fold for a caller with no harness in hand, under <see cref="AgentRunFactKeys.Fallback"/>.</summary>
    public static AgentRunFacts From(IEnumerable<AgentEvent> events) => Fold(new AgentRunFacts(), events);

    private static AgentRunFacts Fold(AgentRunFacts facts, IEnumerable<AgentEvent> events)
    {
        foreach (var normalized in events) facts.Add(normalized);

        return facts;
    }

    private IReadOnlyList<string> MissingFacts()
    {
        var missing = new List<string>(3);

        if (SessionId is null) missing.Add("session id");
        if (TokenUsage is null) missing.Add("token usage");
        if (Model is null) missing.Add("model");

        return missing;
    }
}
