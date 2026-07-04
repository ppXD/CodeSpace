using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Tasks.Phases;

/// <summary>
/// The per-agent rollup the run-detail outline + terminal surface for ANY agent (not just supervisor-spawned) — read
/// team-scoped off the real <c>AgentRun</c> row + its tool-call ledger by the Core <c>AgentMetricsReader</c>.
/// <see cref="Status"/> is the ground-truth <see cref="AgentRunStatus"/>; <see cref="DurationMs"/> is LIVE (recomputed
/// each read); tokens come from the completed <c>ResultJson</c> (null until done, or when the harness reported none);
/// <see cref="Model"/> from the task envelope (null when unpinned). <see cref="ToolCount"/> is a real 0+ count of the
/// agent's side-effecting tool calls. <see cref="CostUsd"/> is the realized spend (model × tokens, fail-open null when
/// the model is unpriced); <see cref="FilesChanged"/> is the git-truth changed-file count off the result. The
/// non-status figures map 1:1 onto <see cref="PhaseAgentRef"/>'s metric fields.
/// </summary>
public sealed record AgentRunMetrics
{
    public required AgentRunStatus Status { get; init; }

    /// <summary>A concise one-line title derived from the agent's goal (its instruction), for the run-detail display NAME of a plain node / map agent that has no model-authored role. Null when the task carried no goal.</summary>
    public string? Goal { get; init; }

    public long? DurationMs { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public int ToolCount { get; init; }

    public string? Model { get; init; }

    /// <summary>The harness kind the agent ran on (e.g. "codex-cli" / "claude-code"), off the task envelope — the small harness glyph a card shows. Null when the task didn't name one (a malformed/partial envelope).</summary>
    public string? Harness { get; init; }

    public decimal? CostUsd { get; init; }

    public int? FilesChanged { get; init; }

    /// <summary>The git-truth changed-file paths (bounded) off the result — the LIST behind <see cref="FilesChanged"/>, so a phase agent carries the files for the terminal's Files tab. Empty until the result lands / when it touched none.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>The git-truth per-file line diffstat (bounded) off the result — the same paths as <see cref="ChangedFiles"/> plus their +added / −removed counts (a binary file's counts are null). Empty for a pre-diffstat run / before the result lands, so a consumer degrades to the plain <see cref="ChangedFiles"/> list.</summary>
    public IReadOnlyList<FileDiffStat> ChangedFileStats { get; init; } = Array.Empty<FileDiffStat>();

    /// <summary>Whether this agent CONTINUED a prior conversation (its task carried a resume session id) rather than starting fresh — the "⟳ resumed" provenance a retry's re-run agent shows. False for a first-run agent.</summary>
    public bool Resumed { get; init; }
}
