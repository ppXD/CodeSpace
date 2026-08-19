using CodeSpace.Messages.Artifacts;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One positive retention declaration over a <see cref="WorkflowArtifact"/> — the ONLY way an artifact becomes a reap
/// candidate. No row here means the artifact is never deleted, which is the state of every row written before this
/// ledger existed and of every byte the JSON offload paths write.
///
/// <para>Minted only by <c>IArtifactRetentionWriter.PutDeclaredAsync</c>, and only when that call is the write that
/// INSERTED the artifact row. A dedup hit mints nothing and revokes any declaration already there, because the
/// content-addressed store hands the same id to the later writer and that writer may reference it from a place this
/// ledger cannot enumerate.</para>
///
/// <para>Claim/fence shape mirrors <see cref="AgentRunLogCaptureIntent"/>: a bounded lease with an owner and a
/// monotonic fence, so a settlement can prove it is the exact claim that observed the reference status.</para>
/// </summary>
public class WorkflowArtifactRetention
{
    /// <summary>The declared artifact. Primary key — a declaration is at-most-once per artifact.</summary>
    public Guid ArtifactId { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>The producer class this declaration is filed under. A class the running policy does not register reads as <see cref="ArtifactRetentionState.Indeterminate"/>, which keeps the artifact.</summary>
    public ArtifactRetentionClass RetentionClass { get; set; }

    /// <summary>What kind of row the producer said it was about to write (diagnosis only — the oracle checks every reference site regardless).</summary>
    public string HolderKind { get; set; } = "";

    /// <summary>The holder row's owning identity (the producing agent run, for the manifest-content class) — diagnosis only.</summary>
    public Guid HolderId { get; set; }

    public ArtifactRetentionState State { get; set; }

    public DateTimeOffset DeclaredAt { get; set; }

    /// <summary>Earliest DB-clock instant this row may be claimed again. Retry backoff moves it forward.</summary>
    public DateTimeOffset NextSweepAt { get; set; }

    /// <summary>When the reaper first observed the artifact unreferenced. Collection requires the class's quarantine window to have elapsed since.</summary>
    public DateTimeOffset? QuarantinedAt { get; set; }

    public DateTimeOffset? TerminalAt { get; set; }

    public Guid? OwnerId { get; set; }
    public long FenceEpoch { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }

    public long Revision { get; set; } = 1;
    public DateTimeOffset LastModifiedAt { get; set; }

    /// <summary>Postgres system column used as the concurrency token, so a settlement that lost a race fails loudly instead of overwriting.</summary>
    public uint Xmin { get; set; }
}
