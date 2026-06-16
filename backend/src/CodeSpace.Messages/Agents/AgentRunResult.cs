using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// The normalized OUTPUT contract of an agent run — what the harness produces at the end, regardless
/// of which CLI ran. Stable + versioned so a consumer (the agent.code node, audit, the UI) reads one
/// shape for every harness. B0.3 persists this alongside the run as <c>result_jsonb</c>. A small unified
/// diff stays INLINE in <see cref="Patch"/>; a large diff (D2) is offloaded to the artifact store and
/// <see cref="Patch"/> is cleared, with <see cref="PatchArtifactId"/> holding the reference — so the
/// <c>result_jsonb</c> row stays bounded and the full diff is fetched on demand.
/// </summary>
public sealed record AgentRunResult
{
    public required AgentRunStatus Status { get; init; }

    /// <summary>Short machine-ish reason for the terminal state (e.g. "completed", "non-zero-exit", "timed-out", "cancelled").</summary>
    public required string ExitReason { get; init; }

    /// <summary>The agent's final summary of what it did (its last assistant/summary message).</summary>
    public string? Summary { get; init; }

    /// <summary>Repo-relative paths the agent changed. When the run had a workspace, this is git ground truth (the captured diff), not the agent's self-report.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>Unified diff (git format) of everything the agent changed vs the cloned base. Empty when there was no workspace or nothing changed, OR when the diff was large enough to offload — in that case <see cref="PatchArtifactId"/> is set and the full diff is fetched from the artifact store. The artefact a downstream PR-open step consumes.</summary>
    public string Patch { get; init; } = "";

    /// <summary>When the diff was offloaded (D2: larger than the artifact inline threshold), the artifact-store id holding the full unified diff; <see cref="Patch"/> is then empty. Null when the diff is inline (small) or absent.</summary>
    public Guid? PatchArtifactId { get; init; }

    /// <summary>Branch the sandbox pushed, when the run produced one (the output handoff for opening a PR).</summary>
    public string? ProducedBranch { get; init; }

    public AgentTokenUsage? TokenUsage { get; init; }

    /// <summary>Failure detail when <see cref="Status"/> is <see cref="AgentRunStatus.Failed"/>.</summary>
    public string? Error { get; init; }
}

public sealed record AgentTokenUsage
{
    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }
}
