using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The BOUNDED reduction a harness's <see cref="IAgentEventFolder"/> composes to build its
/// <see cref="AgentRunResult"/> — what stays in managed memory while an agent streams, in place of the run's events.
///
/// <para><b>Why it exists.</b> The executor used to hold <c>List&lt;AgentEvent&gt;</c> for the ENTIRE run purely so
/// the harness could reduce it at the end. Retention was O(run): a long agent (a multi-gigabyte stdout) exhausted
/// the heap and the executor's generic catch landed the run Failed("executor-error") even though the agent had
/// succeeded. Nothing is lost by not keeping them — the full ordered log is already durable in
/// <c>agent_run_event</c>, and the DB write buffer was already bounded (<c>BufferedEventWriter.MaxBuffered</c>);
/// the in-memory list was the one unbounded half of that pair.</para>
///
/// <para><b>Why it is no longer the seam.</b> Both production harnesses happen to want the same reductions, so this
/// stays the shared, differentially-tested implementation they compose — but it is now an implementation detail of
/// each folder, NOT a type <see cref="IAgentHarness"/> names. A harness that needs a reduction no other harness has
/// adds a field to its OWN folder; nothing here widens for it (Rule 7 / ISP). The pathology that made the
/// distinction concrete: a last-text-of-any-kind field lived here, written on every <see cref="Add"/> and read by
/// NEITHER production harness — it existed only so test doubles could keep <c>events[^1].Text</c>, and it now lives
/// in those doubles' own folder.</para>
///
/// <para><b>What it retains.</b> Only what a fold actually reads: the last text per event KIND (bounded by the
/// enum), the last terminal kind, and the DISTINCT changed files (bounded by the files the agent touched, never by
/// the events it emitted). It deliberately does NOT reduce usage / session id / model: the executor owns that
/// <see cref="AgentRunFacts"/> and hands it to <see cref="IAgentEventFolder.BuildResult"/>, so those three are
/// accumulated exactly ONCE per event no matter how many folders compose this. Each reduction keeps the same first-wins /
/// last-wins direction the whole-list readers used, so folding incrementally is indistinguishable from scanning the
/// finished list — <c>AgentResultFoldTests</c> pins that differentially against the frozen pre-change reduction.</para>
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

    /// <summary>The distinct non-empty <see cref="AgentEventKind.FileChanged"/> texts, in first-occurrence order — what <c>Enumerable.Distinct</c> over the whole stream produced. The fold's own list: read it once the stream has been fully folded, which is where <see cref="IAgentEventFolder.BuildResult"/> runs.</summary>
    public IReadOnlyList<string> ChangedFiles => _changedFiles;

    /// <summary>True when the harness's own last terminal event was an Error — the exit-code reconciliation <see cref="AgentTerminalOutcomeReader"/> performs.</summary>
    public bool ReportedFailure => _lastTerminalKind == AgentEventKind.Error;

    /// <summary>Fold ONE normalized event in, in stream order. O(1) retention (plus a distinct changed file).</summary>
    public void Add(AgentEvent normalized)
    {
        _lastTextByKind[normalized.Kind] = normalized.Text;

        if (AgentTerminalOutcomeReader.IsTerminal(normalized.Kind)) _lastTerminalKind = normalized.Kind;

        AccumulateChangedFile(normalized);
    }

    /// <summary>
    /// The text of the LAST event of this kind, or null when the stream carried none. Returns the empty string for a
    /// kind whose last event had blank text — the fold distinguishes "never seen" from "seen but blank", because the
    /// pre-change reduction chained its summary fallbacks over EVENTS (<c>?? …)?.Text</c>), not over texts.
    /// </summary>
    public string? LastTextOf(AgentEventKind kind) => _lastTextByKind.TryGetValue(kind, out var text) ? text : null;

    private void AccumulateChangedFile(AgentEvent normalized)
    {
        if (normalized.Kind != AgentEventKind.FileChanged || normalized.Text.Length == 0) return;

        if (_changedFileKeys.Add(normalized.Text)) _changedFiles.Add(normalized.Text);
    }
}
