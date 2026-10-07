using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// An importable LIBRARY of artifacts (agents + skills) a team has added — the "where did this come from"
/// root that makes sync idempotent. A GitHub repo / git URL pack records its source (<see cref="Url"/> +
/// <see cref="Reference"/> + <see cref="Subpath"/>) so a re-sync resolves to the SAME pack and upserts its
/// artifacts rather than duplicating them; the per-team <see cref="PackKind.Custom"/> pack carries no remote
/// source and holds locally-authored artifacts.
///
/// <para>An imported <see cref="SkillDefinition"/> (and, later, <c>AgentDefinition</c>) references its pack by
/// id; the pair (pack, source-path) is the unified sync identity. <see cref="LastSyncedSha"/> /
/// <see cref="LastSyncedDate"/> record the last successful sync so the UI can show freshness and a re-sync can
/// short-circuit when the ref is unchanged.</para>
/// </summary>
public class Pack : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    public PackKind Kind { get; set; }

    /// <summary>Human-readable library name (e.g. the repo name) — what the UI groups skills under.</summary>
    public string Name { get; set; } = default!;

    /// <summary>The source location: <c>owner/repo</c> for <see cref="PackKind.Github"/> or a clone URL for <see cref="PackKind.GitUrl"/>. NULL for the <see cref="PackKind.Custom"/> pack. Never carries a credential: it is shown to every team member and is the pack's identity, so a pasted URL's userinfo lives sealed in <see cref="EncryptedCloneUrl"/>.</summary>
    public string? Url { get; set; }

    /// <summary>The URL the last successful import cloned, sealed with <c>IPayloadEncryptor</c> — present only when that URL carried userinfo (a pasted token). Sync and import-from-pack clone from it; nothing returns it. Read and written only through <c>IPackCloneUrlProtector</c>.</summary>
    public string? EncryptedCloneUrl { get; set; }

    /// <summary>The pack that holds this pack's source identity, when an earlier import forked the same repository into two packs (one with a token and one without, or with two tokens). A duplicate keeps syncing from its own source; a new import resolves to the holder. NULL for every other pack.</summary>
    public Guid? DuplicateOfPackId { get; set; }

    /// <summary>The git ref synced (branch / tag / commit). NULL → the source's default branch.</summary>
    public string? Reference { get; set; }

    /// <summary>Directory within the source the pack was discovered under (e.g. "plugins"). NULL → the repo root.</summary>
    public string? Subpath { get; set; }

    /// <summary>The resolved commit sha of the last successful sync — lets a re-sync no-op when the ref hasn't moved. NULL before the first sync.</summary>
    public string? LastSyncedSha { get; set; }

    /// <summary>When the pack was last synced. NULL before the first sync.</summary>
    public DateTimeOffset? LastSyncedDate { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    /// <summary>Soft-delete: removing a pack keeps imported artifacts + their run history intact. NULL = active.</summary>
    public DateTimeOffset? DeletedDate { get; set; }

    /// <summary>Npgsql xmin optimistic-concurrency token (same convention as <see cref="AgentDefinition.Xmin"/>).</summary>
    public uint Xmin { get; set; }
}
