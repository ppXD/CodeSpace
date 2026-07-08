using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The publish-or-park ledger's durable row: ONE git artifact an agent subtask (<see cref="PublishManifestKind.Agent"/>,
/// keyed on <see cref="AgentRunId"/>) or a run-level integration (<see cref="PublishManifestKind.Integration"/>, keyed
/// on <see cref="WorkflowRunId"/> alone) produced. Written REGARDLESS of the owning run's final status — a Failed or
/// TimedOut run's captured diff gets a row exactly like a Succeeded one, because "did this leave a trace" must never
/// depend on how the run ended.
///
/// <para>This becomes the single source of truth a dependent subtask's workspace staging, the supervisor decider
/// prompt, the session room's result card, and the next session turn's fold all read — nobody re-derives a branch
/// name from <c>agent_run.result_jsonb</c> after this. The unique <c>(AgentRunId, RepositoryAlias)</c> index (agent
/// rows) / <c>(WorkflowRunId, RepositoryAlias)</c> index (integration rows) IS the idempotency lock: a retry,
/// reattach, or reconciler re-run upserts this SAME row rather than minting a duplicate branch record.</para>
///
/// <para><see cref="RepositoryAlias"/> (never null, defaults to "primary") — not <see cref="RepositoryId"/>, which
/// mirrors <c>RepositoryRunResult.RepositoryId</c> in being nullable — is the idempotency key component, so multiple
/// unresolved-repository-id rows for the same agent run can never silently bypass the unique index (Postgres treats
/// distinct NULLs as non-colliding).</para>
///
/// <para><see cref="PublishStateValue"/> carries the I1/I3 invariant: every row for a non-empty diff resolves to
/// exactly one state, never left implicit by an absent branch. Every state but <see cref="Agents.PublishState.None"/>
/// carries a non-null <see cref="PatchArtifactId"/> — the work is never merely gone, even when no branch exists.</para>
/// </summary>
public class PublishManifest : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>Tenancy on every row — FK to team, like <see cref="AgentRun.TeamId"/>.</summary>
    public Guid TeamId { get; set; }

    public PublishManifestKind Kind { get; set; } = PublishManifestKind.Agent;

    /// <summary>The owning workflow run. Soft link (no FK), like <see cref="AgentRun.WorkflowRunId"/>. Required for an Integration row; usually set for an Agent row too (a standalone agent run may have none).</summary>
    public Guid? WorkflowRunId { get; set; }

    /// <summary>The subtask this row describes. Soft link (no FK), like <see cref="AgentRunEvent.AgentRunId"/>. Null for an Integration row — no single agent run owns the merged branch.</summary>
    public Guid? AgentRunId { get; set; }

    /// <summary>The catalog repository, when resolved. Soft link (no FK); nullable like <c>RepositoryRunResult.RepositoryId</c> — NOT the idempotency key (see <see cref="RepositoryAlias"/>).</summary>
    public Guid? RepositoryId { get; set; }

    /// <summary>The workspace alias this row's artifact belongs to ("primary" for a single-repo run). Never null — the actual idempotency key component alongside <see cref="AgentRunId"/> / <see cref="WorkflowRunId"/>.</summary>
    public string RepositoryAlias { get; set; } = "primary";

    /// <summary>The cloned HEAD revision the diff is against (<c>WorkspaceChanges.BaseSha</c>).</summary>
    public string? BaseSha { get; set; }

    /// <summary>The pushed remote branch name. Null unless <see cref="PublishStateValue"/> is <see cref="Agents.PublishState.Pushed"/>.</summary>
    public string? Branch { get; set; }

    /// <summary>The commit the branch's tip resolved to at push time.</summary>
    public string? CommitSha { get; set; }

    /// <summary>Soft link to <c>workflow_artifact.id</c> holding the FULL, untruncated unified diff — offloaded via <c>IArtifactOffloader</c> BEFORE any inline-string cap is applied, so a large diff never loses bytes.</summary>
    public Guid? PatchArtifactId { get; set; }

    public int ChangedFileCount { get; set; }

    /// <summary>The full changed-file path list as a JSON string array — never silently capped (unlike a UI's display cap).</summary>
    public string? ChangedFilesJson { get; set; }

    public PublishAcceptanceState AcceptanceState { get; set; } = PublishAcceptanceState.NotApplicable;

    public PublishState PublishStateValue { get; set; } = PublishState.None;

    /// <summary>Set when <see cref="PublishStateValue"/> is <see cref="Agents.PublishState.PatchOnly"/> AND the reason was a failed push attempt (not an intentional policy skip) — null distinguishes "by choice" from "attempted and failed".</summary>
    public string? PublishError { get; set; }

    /// <summary>A one-line human-readable description of the artifact, folded into the room result card / decider prompt without a second lookup.</summary>
    public string? Summary { get; set; }

    /// <summary>The pull/merge request opened against <see cref="Branch"/>, once one exists (the Room's Open-PR action, or a workflow's git.open_pr / git.open_change_set node). Null until then — doubles as the idempotency read: a non-null pair means a repeat Open-PR click must reuse this PR, never open a duplicate.</summary>
    public int? PullRequestNumber { get; set; }

    public string? PullRequestUrl { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    /// <summary>Npgsql xmin optimistic-concurrency token (same convention as <see cref="AgentRun.Xmin"/>) — guards the upsert's read-modify-write against a concurrent revise-round write.</summary>
    public uint Xmin { get; set; }
}
