using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// A reusable Skill — a named, progressively-disclosed capability (the SKILL.md noun) an Agent persona can
/// bind. Mirrors <see cref="AgentDefinition"/>'s storage gene: only the keys we route/query/@ on are
/// first-class columns (<see cref="Slug"/> / <see cref="Name"/> / <see cref="Description"/> = the always-cheap
/// Level-1 index), the SKILL.md instruction body is <see cref="Body"/> (Level 2, loaded on use), and the
/// original frontmatter is preserved VERBATIM in <see cref="RawFrontmatterJson"/> so unknown/future keys never
/// need a migration (format-preserving import). Bundled Level-3 resources (references/, scripts/) are NOT
/// stored here yet — a later slice adds the resource manifest.
///
/// <para>Lives in the team's skill library (a "skill store"): both locally-authored (<see cref="SkillDefinitionOrigin.Authored"/>)
/// and pack-imported (<see cref="SkillDefinitionOrigin.Imported"/>) skills are rows, so selecting a skill for an
/// agent is a library pick. <see cref="PackId"/> + <see cref="SourcePath"/> are the unified sync identity: a
/// re-sync upserts the row keyed on that pair, never duplicating. Harness-AGNOSTIC — each harness projects the
/// bound skill into its own native layout at run.</para>
/// </summary>
public class SkillDefinition : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>The stable handle / @-key, unique per team among non-deleted rows (a soft-deleted slug is reusable).</summary>
    public string Slug { get; set; } = default!;

    public string Name { get; set; } = default!;

    /// <summary>The trigger/router text ("Use when…") — Level 1, always available so the agent knows WHEN the skill applies. NULL only for a malformed import (surfaced as un-importable in preview).</summary>
    public string? Description { get; set; }

    /// <summary>The SKILL.md instruction body (Level 2) — the markdown after the frontmatter, loaded on use.</summary>
    public string Body { get; set; } = "";

    /// <summary>Grouping label for the library UI (from frontmatter, the pack's folder, or "Uncategorized"). NULL = ungrouped.</summary>
    public string? Category { get; set; }

    /// <summary>The original parsed frontmatter VERBATIM — lossless forward-compat; unknown keys pass through. jsonb, default "{}".</summary>
    public string RawFrontmatterJson { get; set; } = "{}";

    public SkillDefinitionOrigin Origin { get; set; } = SkillDefinitionOrigin.Authored;

    /// <summary>The <see cref="Pack"/> this skill was imported from (its library). NULL for an authored skill. FK to pack.</summary>
    public Guid? PackId { get; set; }

    /// <summary>Path within the pack (e.g. "skills/test-driven-development/SKILL.md"). NULL for an authored skill. With <see cref="PackId"/> this is the unified sync identity.</summary>
    public string? SourcePath { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    /// <summary>Soft-delete: a removed skill keeps any agent bindings + history intact. NULL = active.</summary>
    public DateTimeOffset? DeletedDate { get; set; }

    /// <summary>Npgsql xmin optimistic-concurrency token (same convention as <see cref="AgentDefinition.Xmin"/>).</summary>
    public uint Xmin { get; set; }
}
