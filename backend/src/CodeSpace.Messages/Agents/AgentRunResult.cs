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

    /// <summary>
    /// Per-file line diffstat (added / removed counts), git ground truth from <c>git diff --numstat</c> — parallel to
    /// <see cref="ChangedFiles"/>. Captured at run end so the "+X −Y" is a durable fact even when <see cref="Patch"/> is
    /// offloaded (the counts can't be re-derived from an absent patch). Empty when there was no workspace / nothing
    /// changed; a binary file's counts are null. The turn's "+X −Y" is the sum of the non-null counts.
    /// </summary>
    public IReadOnlyList<FileDiffStat> FileStats { get; init; } = Array.Empty<FileDiffStat>();

    /// <summary>Unified diff (git format) of everything the agent changed vs the cloned base. Empty when there was no workspace or nothing changed, OR when the diff was large enough to offload — in that case <see cref="PatchArtifactId"/> is set and the full diff is fetched from the artifact store. The artefact a downstream PR-open step consumes.</summary>
    public string Patch { get; init; } = "";

    /// <summary>When the diff was offloaded (D2: larger than the artifact inline threshold), the artifact-store id holding the full unified diff; <see cref="Patch"/> is then empty. Null when the diff is inline (small) or absent.</summary>
    public Guid? PatchArtifactId { get; init; }

    /// <summary>D3: the FAITHFUL raw harness stream (every redacted line, including ones ParseEvent dropped) — the durable "replay the exact session" record. Inline when small; a larger transcript is offloaded and this is cleared, with <see cref="TranscriptArtifactId"/> holding the ref. Empty when there was no stream.</summary>
    public string Transcript { get; init; } = "";

    /// <summary>D3: when the transcript was offloaded (larger than the inline threshold — the common case for a real run), the artifact-store id holding the full raw stream; <see cref="Transcript"/> is then empty. Null when inline (small) or absent.</summary>
    public Guid? TranscriptArtifactId { get; init; }

    /// <summary>
    /// P3: the agent's RESUMABLE session transcript — the harness-native session file (Claude's
    /// <c>projects/&lt;cwd&gt;/&lt;id&gt;.jsonl</c>) the executor read from the per-run config home before it was reaped, so a
    /// later CONTINUE can restore the conversation (distinct from <see cref="Transcript"/>, which is the stream-json the
    /// CLI EMITTED — this is the state the CLI READS on <c>--resume</c>). Inline when small; a larger one is offloaded and
    /// this is cleared with <see cref="SessionTranscriptArtifactId"/> holding the ref. Empty when the harness has no
    /// resumable transcript (Codex deferred) or the file wasn't captured.
    /// </summary>
    public string SessionTranscript { get; init; } = "";

    /// <summary>When the session transcript was offloaded (larger than the inline threshold), the artifact-store id holding it; <see cref="SessionTranscript"/> is then empty. Null when inline (small) or absent.</summary>
    public Guid? SessionTranscriptArtifactId { get; init; }

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

    /// <summary>
    /// P3.1a: the harness-native session/thread id of the agent's CLI conversation (Claude's <c>session_id</c>,
    /// Codex's <c>thread_id</c>), captured off the run's event stream. The handle a later rerun threads back as
    /// <c>--resume &lt;id&gt;</c> / <c>exec resume &lt;id&gt;</c> to CONTINUE the prior conversation instead of cold-starting.
    /// Null when the stream carried none (a pre-session CLI, or a run that never reached its session-bearing line).
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>Failure detail when <see cref="Status"/> is <see cref="AgentRunStatus.Failed"/>.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Slice A completion contract: the "what happens next" overlay on <see cref="Status"/>. Defaults to
    /// <see cref="CompletionDisposition.Completed"/> so a harness that just sets <see cref="Status"/> keeps the normal
    /// disposition; the contract re-grades a would-be <see cref="AgentRunStatus.Succeeded"/> to
    /// <see cref="AgentRunStatus.NeedsReview"/> and sets this to the reason a human is needed.
    /// </summary>
    public CompletionDisposition CompletionDisposition { get; init; } = CompletionDisposition.Completed;

    /// <summary>The task's OBJECTIVE acceptance verdict (S5): true/false when the executor graded <c>AgentTask.Acceptance</c> against the produced branch; null when the task carried no oracle (or the grade was deferred — multi-repo). The run-detail checklist reads this per item.</summary>
    public bool? AcceptancePassed { get; init; }

    /// <summary>The grader's one-line detail (exit code / missing paths / "no-branch-or-repo") — the acceptance chip's tooltip.</summary>
    public string? AcceptanceDetail { get; init; }

    /// <summary>
    /// Whether this run's self-report contradicted its objective grade (P4-1) — a
    /// <c>CodeSpace.Core.Services.Agents.AgentContradiction</c> value. In THIS lane the only reachable value is
    /// <c>over_claim</c>: <see cref="Core.Services.Agents.AgentAcceptanceContract.FailClosed"/> is the sole write
    /// site, and every one of its call sites only ever fires on a would-be <see cref="AgentRunStatus.Succeeded"/>
    /// result whose check FAILED (the grading gate returns early on any other self-reported status, so an
    /// under-claim — a self-reported Failed run whose check actually passed — can never occur here). Null when the
    /// run carried no oracle, the grade agreed, or grading never ran.
    /// </summary>
    public string? Contradiction { get; init; }

    /// <summary>The output critic's rationale + issues when it flagged this run (<c>ExitReason</c> "output-flagged") — WHY a human should look, persisted on the result (not only a timeline event), and the food the S6 revise loop feeds back to the agent under <c>ReviewMode.Improve</c>. Null when the critic approved, failed open, or never ran.</summary>
    public string? ReviewFeedback { get; init; }

    /// <summary>How many S6 revise rounds actually executed inside this run (0 = the first attempt stood). Each round is a same-session harness continuation after an oracle failure or an Improve-critic flag, re-verified through the full push→grade→review chain.</summary>
    public int ReviseRounds { get; init; }

    /// <summary>
    /// When <see cref="CompletionDisposition"/> is <see cref="CompletionDisposition.NeedsDecision"/>, the ledger id of the
    /// still-unanswered <c>decision.request</c> the run raised — the handle a reviewer (or the "Needs decision" queue)
    /// resolves. Null in every other case.
    /// </summary>
    public Guid? PendingDecisionId { get; init; }

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

    /// <summary>Set ONLY when a non-empty diff failed to push after retries — the publish-manifest's signal that <see cref="ProducedBranch"/> being null was an ATTEMPT that failed, not a policy choice. Already-redacted (the workspace provider redacts before throwing). Null on success, on a policy skip, and on every non-publish outcome.</summary>
    public string? PublishError { get; init; }

    /// <summary>Set ONLY when the publish guard chain (<c>IPublishGuard</c>) blocked the push — a BY-CHOICE skip (never both this and <see cref="PublishError"/> at once). The winning guard's human-readable reason, folded onto the publish-manifest row's <c>Summary</c> so a by-choice skip explains itself.</summary>
    public string? PublishSkipReason { get; init; }
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

    /// <summary>Per-file line diffstat for this repo (added / removed counts, git ground truth) — parallel to <see cref="ChangedFiles"/>, mirroring the top-level <see cref="AgentRunResult.FileStats"/>. A binary file's counts are null.</summary>
    public IReadOnlyList<FileDiffStat> FileStats { get; init; } = Array.Empty<FileDiffStat>();

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

    /// <summary>Mirrors <see cref="AgentRunResult.PublishError"/> for this repo — set ONLY when this repo's non-empty diff failed to push after retries.</summary>
    public string? PublishError { get; init; }

    /// <summary>Mirrors <see cref="AgentRunResult.PublishSkipReason"/> for this repo — set ONLY when the publish guard chain blocked THIS repo's push (a multi-repo run can mix policies across repos).</summary>
    public string? PublishSkipReason { get; init; }

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
