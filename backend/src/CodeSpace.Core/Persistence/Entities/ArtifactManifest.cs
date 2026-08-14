using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// DC-4 slice 1 (typed artifact manifest) — one produced NON-GIT deliverable's durable, first-class identity:
/// WHAT kind of thing an attempt produced (document / diagram / dataset), WHERE its exact bytes live
/// (<see cref="WorkflowArtifact"/> CAS row), and WHICH later capture superseded it. Before this row a produced
/// report/diagram survived only as a hunk inside the captured diff, and the CAS row holding its bytes was an
/// untyped bag whose meaning lived in the referring field's name.
///
/// <para>A SIBLING of <see cref="CaptureIntent"/> (same attempt-grain key: <c>(AgentRunId, FenceEpoch,
/// LogicalPath)</c> unique), deliberately NOT an extension of <see cref="PublishManifest"/> — that table is
/// structurally a GIT ledger (repository alias inside both unique indices; branch/commit/patch as its only
/// content axes). <see cref="SupersededByManifestId"/> copies capture_intent's #1352 discipline: a pointer,
/// never a rewrite.</para>
/// </summary>
public class ArtifactManifest : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>Tenancy on EVERY row — the owning team, like <see cref="CaptureIntent.TeamId"/>.</summary>
    public Guid TeamId { get; set; }

    /// <summary>The agent run (attempt) that produced this artifact. Soft link, like <see cref="CaptureIntent.AgentRunId"/>.</summary>
    public Guid AgentRunId { get; set; }

    /// <summary>The owning workflow run when the attempt is workflow-bound — the run-level "what did this run produce" join key. Soft link, nullable (standalone runs).</summary>
    public Guid? WorkflowRunId { get; set; }

    /// <summary>The fence epoch the producing attempt claimed — the attempt discriminator (a reclaimed re-attach runs at a bumped epoch and captures its own rows).</summary>
    public long FenceEpoch { get; set; } = 1;

    /// <summary>The artifact's TYPE — <see cref="ArtifactManifestKind"/> stored as text (wire-stable).</summary>
    public ArtifactManifestKind Kind { get; set; }

    /// <summary>The workspace-relative path the run declared and the capture read — the artifact's human name (e.g. <c>docs/report.md</c>).</summary>
    public string LogicalPath { get; set; } = "";

    /// <summary>The CAS row holding the exact captured bytes — soft link to <see cref="WorkflowArtifact"/>.</summary>
    public Guid ContentArtifactId { get; set; }

    /// <summary>The captured bytes' SHA-256 (mirrors the CAS row — carried here so consumers verify identity without a second read).</summary>
    public string Sha256 { get; set; } = "";

    public long SizeBytes { get; set; }

    /// <summary>Best-effort MIME derived from the path's extension (the store does not validate it against the bytes).</summary>
    public string ContentType { get; set; } = "";

    /// <summary>The LATER manifest row (same attempt coordinates, a re-capture) that formally supersedes this one — a pointer, never a rewrite (the #1352 discipline). Null = current.</summary>
    public Guid? SupersededByManifestId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
