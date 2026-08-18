using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The BOUNDED accumulator a harness folds its <see cref="AgentRunResult"/> from — what the executor keeps in
/// managed memory while an agent streams, in place of the whole run's events.
///
/// <para><b>Why it exists.</b> The executor used to hold <c>List&lt;AgentEvent&gt;</c> for the ENTIRE run purely so
/// <see cref="IAgentHarness.BuildResult"/> could reduce it at the end. Retention was O(run): a long agent (a
/// multi-gigabyte stdout) exhausted the heap and the executor's generic catch landed the run
/// Failed("executor-error") even though the agent had succeeded. Nothing is lost by not keeping them — the full
/// ordered log is already durable in <c>agent_run_event</c>, and the DB write buffer was already bounded
/// (<c>BufferedEventWriter.MaxBuffered</c>); the in-memory list was the one unbounded half of that pair.</para>
///
/// <para><b>What it retains.</b> Only what a fold actually reads: the last text per event KIND (bounded by the
/// enum), the first session id / model, the last token usage, the last terminal kind, and the DISTINCT changed
/// files (bounded by the files the agent touched, never by the events it emitted). Each reduction keeps the same
/// first-wins / last-wins direction the whole-list readers used, so folding incrementally is indistinguishable
/// from scanning the finished list — <c>AgentResultFoldTests</c> pins that differentially against the frozen
/// pre-change reduction.</para>
///
/// <para>Single-threaded by contract: one fold belongs to one run's line-by-line accumulation (the executor's
/// PersistLineAsync), mirroring the sequential event writer next to it.</para>
/// </summary>
public sealed class AgentResultFold
{
    private readonly Dictionary<AgentEventKind, string> _lastTextByKind = new();
    private readonly List<string> _changedFiles = new();
    private readonly HashSet<string> _changedFileKeys = new(StringComparer.Ordinal);
    private AgentEventKind? _lastTerminalKind;

    /// <summary>The distinct non-empty <see cref="AgentEventKind.FileChanged"/> texts, in first-occurrence order — what <c>Enumerable.Distinct</c> over the whole stream produced. The fold's own list: read it once the stream has been fully folded, which is where <see cref="IAgentHarness.BuildResult"/> runs.</summary>
    public IReadOnlyList<string> ChangedFiles => _changedFiles;

    /// <summary>The LAST recognizable token usage the stream carried (a cumulative stream's run total), or null when none did — see <see cref="AgentTokenUsageReader"/>.</summary>
    public AgentTokenUsage? TokenUsage { get; private set; }

    /// <summary>The FIRST recognizable harness session/thread id, or null when none was carried — see <see cref="AgentSessionIdReader"/>.</summary>
    public string? SessionId { get; private set; }

    /// <summary>The FIRST recognizable model the CLI named, or null when none was carried — see <see cref="AgentModelReader"/>.</summary>
    public string? Model { get; private set; }

    /// <summary>The text of the most recent event, whatever its kind; null before any event. The reduction a harness uses when its stream's final line IS the summary.</summary>
    public string? LastText { get; private set; }

    /// <summary>True when the harness's own last terminal event was an Error — the exit-code reconciliation <see cref="AgentTerminalOutcomeReader"/> performs.</summary>
    public bool ReportedFailure => _lastTerminalKind == AgentEventKind.Error;

    /// <summary>Fold ONE normalized event in, in stream order. O(1) retention (plus a distinct changed file).</summary>
    public void Add(AgentEvent normalized)
    {
        LastText = normalized.Text;
        _lastTextByKind[normalized.Kind] = normalized.Text;

        if (AgentTerminalOutcomeReader.IsTerminal(normalized.Kind)) _lastTerminalKind = normalized.Kind;

        AccumulateChangedFile(normalized);
        AccumulateStructuredFacts(normalized);
    }

    /// <summary>
    /// The text of the LAST event of this kind, or null when the stream carried none. Returns the empty string for a
    /// kind whose last event had blank text — the fold distinguishes "never seen" from "seen but blank", because the
    /// pre-change reduction chained its summary fallbacks over EVENTS (<c>?? …)?.Text</c>), not over texts.
    /// </summary>
    public string? LastTextOf(AgentEventKind kind) => _lastTextByKind.TryGetValue(kind, out var text) ? text : null;

    /// <summary>Fold a stream that is already fully in hand — replay, offline analysis, and tests. The streaming executor calls <see cref="Add"/> per line instead, which is the whole point.</summary>
    public static AgentResultFold From(IEnumerable<AgentEvent> events)
    {
        var fold = new AgentResultFold();

        foreach (var normalized in events) fold.Add(normalized);

        return fold;
    }

    private void AccumulateChangedFile(AgentEvent normalized)
    {
        if (normalized.Kind != AgentEventKind.FileChanged || normalized.Text.Length == 0) return;

        if (_changedFileKeys.Add(normalized.Text)) _changedFiles.Add(normalized.Text);
    }

    /// <summary>Read the structured payload's facts once, keeping each in the direction its whole-list reader used: usage LAST-wins (the newest-first scan), id + model FIRST-wins.</summary>
    private void AccumulateStructuredFacts(AgentEvent normalized)
    {
        if (normalized.Data is null) return;

        TokenUsage = AgentTokenUsageReader.TryRead(normalized) ?? TokenUsage;
        SessionId ??= AgentSessionIdReader.TryRead(normalized);
        Model ??= AgentModelReader.TryRead(normalized);
    }
}
