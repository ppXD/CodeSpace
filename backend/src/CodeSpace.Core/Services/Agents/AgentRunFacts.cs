using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The three run facts every harness is expected to report — token usage, session/thread id, model — folded O(1) out
/// of a stream by the shared readers (<see cref="AgentTokenUsageReader"/>, <see cref="AgentSessionIdReader"/>,
/// <see cref="AgentModelReader"/>), each kept in the direction its whole-list reader used: usage LAST-wins (the
/// newest-first scan), id and model FIRST-wins.
///
/// <para><b>What is harness-independent here, and what is not.</b> The SHAPE is: these three properties, and the
/// executor's use of them, are the same whatever ran. The EXTRACTION is not — it is PARAMETERIZED by a key table the
/// three readers hold between them (<c>session_id</c>/<c>thread_id</c>, <c>input_tokens</c>/<c>prompt_tokens</c>/…,
/// <c>model</c>/<c>model_name</c>), a UNION over the two harnesses that exist. A third harness whose stream spells any
/// of these differently must EXTEND the relevant reader; nothing here will discover its spelling. Extraction also
/// depends on an obligation no interface states: <see cref="Add"/> reads only <see cref="AgentEvent.Data"/>, so a
/// harness whose <c>ParseEvents</c> does not retain the native line's structured root contributes nothing — and
/// <c>IAgentHarness.ParseEvents</c> requires no such retention.</para>
///
/// <para><b>The failure mode is silence, not an error.</b> An unextended reader yields null, which nothing rejects:
/// <c>AgentRunExecutor.BuildReviseTask</c> reads a null <c>SessionId</c> as "cold", so every warm retry silently
/// restarts the conversation from scratch, and a null usage records the run as having burned nothing. Only the model has
/// a second route — <c>IAgentTranscriptModelSource</c>, which the executor consults when the captured model is empty,
/// and only for a harness that implements it. A new harness must therefore be checked against these three readers
/// explicitly; a green run proves nothing about them.</para>
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
    /// <summary>The LAST recognizable token usage the stream carried (a cumulative stream's run total), or null when none did — see <see cref="AgentTokenUsageReader"/>.</summary>
    public AgentTokenUsage? TokenUsage { get; private set; }

    /// <summary>The FIRST recognizable harness session/thread id, or null when none was carried — see <see cref="AgentSessionIdReader"/>.</summary>
    public string? SessionId { get; private set; }

    /// <summary>The FIRST recognizable model the CLI named, or null when none was carried — see <see cref="AgentModelReader"/>.</summary>
    public string? Model { get; private set; }

    /// <summary>Read the structured payload's facts once, keeping each in the direction its whole-list reader used. O(1) retention.</summary>
    public void Add(AgentEvent normalized)
    {
        if (normalized.Data is null) return;

        TokenUsage = AgentTokenUsageReader.TryRead(normalized) ?? TokenUsage;
        SessionId ??= AgentSessionIdReader.TryRead(normalized);
        Model ??= AgentModelReader.TryRead(normalized);
    }

    /// <summary>Fold a stream that is already fully in hand — replay, offline analysis, and tests. The streaming executor calls <see cref="Add"/> per line instead, which is the whole point.</summary>
    public static AgentRunFacts From(IEnumerable<AgentEvent> events)
    {
        var facts = new AgentRunFacts();

        foreach (var normalized in events) facts.Add(normalized);

        return facts;
    }
}
