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

    /// <summary>D3: the FAITHFUL raw harness stream (every redacted line, including ones ParseEvent dropped) — the durable "replay the exact session" record. Inline when small; a larger transcript is offloaded and this is cleared, with <see cref="TranscriptArtifactId"/> holding the ref. Empty when there was no stream.</summary>
    public string Transcript { get; init; } = "";

    /// <summary>D3: when the transcript was offloaded (larger than the inline threshold — the common case for a real run), the artifact-store id holding the full raw stream; <see cref="Transcript"/> is then empty. Null when inline (small) or absent.</summary>
    public Guid? TranscriptArtifactId { get; init; }

    /// <summary>Branch the sandbox pushed, when the run produced one (the output handoff for opening a PR).</summary>
    public string? ProducedBranch { get; init; }

    /// <summary>
    /// The cloned base revision the <see cref="Patch"/> is rooted at — the exact commit this agent saw. The integrity
    /// anchor for on-disk branch integration (SOTA #3): the integrator checks out THIS SHA before <c>git apply --3way</c>
    /// so the pre-image resolves against the commit the agent actually edited, and REFUSES a set whose contributions
    /// report differing base SHAs (a stale-base patch otherwise applies "cleanly" onto a moved tree and silently grafts
    /// incoherent work). Null when the run had no workspace, or for a re-attached run with no surviving clone.
    /// </summary>
    public string? BaseSha { get; init; }

    public AgentTokenUsage? TokenUsage { get; init; }

    /// <summary>Failure detail when <see cref="Status"/> is <see cref="AgentRunStatus.Failed"/>.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Per-repository outcomes for a MULTI-repo workspace run (multi-repo PR3) — one entry per WRITABLE repo, each
    /// with its own changed files + produced branch + base SHA. EMPTY for a single-repo run, whose single outcome is
    /// the top-level <see cref="ChangedFiles"/>/<see cref="Patch"/>/<see cref="ProducedBranch"/>/<see cref="BaseSha"/>;
    /// those top-level fields continue to mirror the PRIMARY repo even when this is populated, so an existing consumer
    /// (the agent.code node's branch output, audit) is unaffected. The per-repo branches together form the <see cref="ChangeSetId"/>.
    /// </summary>
    public IReadOnlyList<RepositoryRunResult> RepositoryResults { get; init; } = Array.Empty<RepositoryRunResult>();

    /// <summary>A stable id for the SET of branches a MULTI-repo run produced (a Change Set the downstream integration / PR-open consumes — the SOTA #3 Change-Set spine on the repo axis). Null for a single-repo run (no change-set concept).</summary>
    public string? ChangeSetId { get; init; }
}

/// <summary>One writable repository's outcome in a multi-repo workspace run (multi-repo PR3, Rule 18.1 noun): which repo (by alias), what it changed, the branch it produced, and the base it was rooted at.</summary>
public sealed record RepositoryRunResult
{
    /// <summary>The repo's alias within the workspace (the human-facing handle the WORKSPACE.md + UI use).</summary>
    public required string Alias { get; init; }

    /// <summary>
    /// The repository this outcome belongs to — the STABLE identity a downstream Change-Set step (open a PR per repo,
    /// integrate) needs, since <see cref="AgentRunResult"/> is persisted standalone (the alias→repo map lives only on
    /// the run's authoring <c>WorkspaceSpec</c>). Null only when the run had no resolvable spec (a degraded path).
    /// </summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>Repo-relative paths the agent changed in this repo (git ground truth).</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The inline unified diff (git format) of this repo's changes vs its <see cref="BaseSha"/> — the durable,
    /// base-anchored input a per-repo on-disk integration consumes (the same role the top-level <see cref="AgentRunResult.Patch"/>
    /// plays for a single-repo run). Empty when this repo changed nothing OR when the diff was large enough to offload
    /// (then <see cref="PatchArtifactId"/> holds the ref). Capped inline like the top-level patch so a many-repo run's
    /// result row stays bounded.
    /// </summary>
    public string Patch { get; init; } = "";

    /// <summary>When this repo's diff was offloaded (larger than the artifact inline threshold), the artifact-store id holding the full unified diff; <see cref="Patch"/> is then empty. Null when the diff is inline (small) or absent. Mirrors the top-level <see cref="AgentRunResult.PatchArtifactId"/>.</summary>
    public Guid? PatchArtifactId { get; init; }

    /// <summary>The branch this repo's changes were pushed to (the per-repo PR-open handoff), or null when nothing was pushed / it had no changes.</summary>
    public string? ProducedBranch { get; init; }

    /// <summary>The cloned base revision this repo's work is rooted at (the SOTA #3 integrity anchor, per repo).</summary>
    public string? BaseSha { get; init; }

    /// <summary>The ref this repo was cloned at — the PR base for <see cref="ProducedBranch"/>, so a downstream <c>git.open_change_set</c> can open a PR per repo by binding this result verbatim (no separately-authored target). Usually the repo's default branch; a tag when an author pinned one in the run's <c>WorkspaceSpec.Ref</c> — a non-branch ref makes that repo's open a per-repo Failed (resolving a real base from a tag clone is a follow-on). Null when the run had no resolvable ref.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>The repo's access in the workspace (always <see cref="WorkspaceAccess.Write"/> for an entry that produced an outcome).</summary>
    public WorkspaceAccess Access { get; init; } = WorkspaceAccess.Write;

    /// <summary>
    /// The DIFF-FREE projection of this per-repo result — the bounded facts (alias / repository id / changed files /
    /// produced branch / base) with the unbounded <see cref="Patch"/> + <see cref="PatchArtifactId"/> cleared. The
    /// single shared way every TOKEN-CHEAP surface (the supervisor's decider-visible compact, the agent.code node
    /// output) drops the diff — exactly as those surfaces already exclude the top-level patch — so the diff lives only
    /// on the durable <c>AgentRunResult</c> (where the on-disk integration reads it) and never bloats a ledger row or
    /// an <c>outputs_jsonb</c>. One place, so the two surfaces can't drift on which fields are "heavy".
    /// </summary>
    public RepositoryRunResult WithoutDiff() => this with { Patch = "", PatchArtifactId = null };
}

public sealed record AgentTokenUsage
{
    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }
}
