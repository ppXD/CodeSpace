using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.UnitTests;

/// <summary>
/// The <see cref="IAgentEventFolder"/> a TEST DOUBLE harness hands back from <c>CreateFolder()</c>: it accumulates
/// the shared <see cref="AgentResultFold"/> plus <see cref="LastText"/> — the last text of ANY kind — and defers the
/// result to the lambda each fake supplies, so a fake still declares its result shape in one line.
///
/// <para><see cref="LastText"/> lives HERE, in the test assembly, and not in Core. It used to be a field on the
/// shared fold that NEITHER production harness read: only the doubles wanted the pre-fold <c>events[^1].Text</c>,
/// and while a single concrete accumulator sat on the harness seam that was the only place to put it. With the seam
/// narrowed to <see cref="IAgentEventFolder"/>, a reduction only the doubles need belongs to the doubles — which is
/// the whole point of the inversion (Rule 7 / ISP).</para>
/// </summary>
internal sealed class TestEventFolder : IAgentEventFolder
{
    private readonly Func<TestEventFolder, int, AgentRunResult> _build;
    private readonly AgentResultFold _fold = new();
    private AgentRunFacts? _facts;

    public TestEventFolder(Func<TestEventFolder, int, AgentRunResult> build) => _build = build;

    /// <summary>The text of the most recent event, whatever its kind; null before any event. The reduction a double uses when its stream's final line IS the summary.</summary>
    public string? LastText { get; private set; }

    /// <summary>The run's stderr as the executor handed it in at <see cref="BuildResult"/> — the process's OTHER opening, which never reaches a folder through its events. Empty before the terminal.</summary>
    public string Diagnostics { get; private set; } = "";

    /// <summary>The LAST recognizable token usage the stream carried — the executor's own facts, handed in at <see cref="BuildResult"/>. Null before the terminal, because the double never accumulates them itself: they are the executor's to drive.</summary>
    public AgentTokenUsage? TokenUsage => _facts?.TokenUsage;

    public void Add(AgentEvent normalized)
    {
        LastText = normalized.Text;
        _fold.Add(normalized);
    }

    public AgentRunResult BuildResult(AgentRunFacts facts, int exitCode, string diagnostics)
    {
        Diagnostics = diagnostics;

        _facts = facts;

        return _build(this, exitCode);
    }

    /// <summary>The text of the LAST event of this kind, or null when the stream carried none — forwarded from the shared fold.</summary>
    public string? LastTextOf(AgentEventKind kind) => _fold.LastTextOf(kind);
}
