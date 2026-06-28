using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// A reusable Agent persona — the canonical "Agent" noun: a named, importable, @-mentionable,
/// directly-runnable capability (system prompt + model + tools + skills + MCP + default autonomy). An
/// <c>agent.code</c> node references one of these (with optional inline overrides resolved into the
/// <c>AgentTask</c> at dispatch); the harness projects it. **Harness-AGNOSTIC by design — no harness
/// column** — so the same persona runs on any compatible harness (Codex today, Claude Code next).
///
/// <para>Generic + scaling: only the keys we route/query/@ on are first-class columns; the original
/// artifact's full frontmatter is preserved VERBATIM in <see cref="RawFrontmatterJson"/> so new/unknown
/// keys never need a migration (format-preserving import). The list/structured fields are stored as JSON
/// strings (jsonb columns) — modelled into DTOs by the service layer in a later slice, kept whole here so
/// the shape evolves without schema churn (same convention as <see cref="AgentRun.TaskJson"/>).</para>
/// </summary>
public class AgentDefinition : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>The @-mention handle / stable key, unique per team among non-deleted rows.</summary>
    public string Slug { get; set; } = default!;

    public string Name { get; set; } = default!;

    /// <summary>Routing/trigger description ("use PROACTIVELY when…") — drives auto-invocation, the UI, and harness-compat badges.</summary>
    public string? Description { get; set; }

    /// <summary>The persona's instructions (the imported <c>.md</c> body).</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>Model id, or NULL to let the chosen harness/CLI pick its own default (the Model=empty rule). Set only to override.</summary>
    public string? Model { get; set; }

    /// <summary>Default <c>ModelCredential</c> this persona authenticates with — a REFERENCE (id), resolved + decrypted just-in-time at run. NULL → the run falls back to a team default / operator-global key. A node-level override wins over this.</summary>
    public Guid? ModelCredentialId { get; set; }

    /// <summary>Default autonomy level name (guarded / suggest / autonomous); NULL → guarded. Stored as a string so this entity doesn't depend on the Autonomy-track enum; the dial parses it.</summary>
    public string? DefaultAutonomy { get; set; }

    /// <summary>Tool allow-list as JSON (array of names/patterns); NULL = the harness's default toolset (distinct from "[]" = no tools). jsonb.</summary>
    public string? ToolsJson { get; set; }

    /// <summary>MCP server references / configs as JSON; the harness injects them at run. jsonb, default "[]".</summary>
    public string McpServersJson { get; set; } = "[]";

    /// <summary>The original parsed frontmatter VERBATIM — lossless forward-compat; unknown keys pass through. jsonb, default "{}".</summary>
    public string RawFrontmatterJson { get; set; } = "{}";

    public AgentDefinitionOrigin Origin { get; set; } = AgentDefinitionOrigin.Authored;

    /// <summary>The agent pack this was imported from (git source + ref), for re-sync. NULL for an authored agent. Soft ref (no FK — the pack table is a later slice).</summary>
    public Guid? PackId { get; set; }

    /// <summary>Path within the pack (e.g. "agents/backend-architect.md"). NULL for an authored agent.</summary>
    public string? SourcePath { get; set; }

    /// <summary>Working (on the bench, runnable) vs Store (a Library snapshot, instantiated into working copies). Defaults to Working — a forgotten stamp lands on the bench, never an invisible store row.</summary>
    public DefinitionScope Scope { get; set; } = DefinitionScope.Working;

    /// <summary>For a Working copy instantiated from a Store snapshot: that snapshot's id (soft ref, no FK). NULL on snapshots, authored, and grandfathered rows.</summary>
    public Guid? SourceDefinitionId { get; set; }

    /// <summary>The Store snapshot's content version captured when this copy was instantiated (the LHS of the sync comparison). NULL until instantiated-from-store.</summary>
    public string? SourceVersion { get; set; }

    /// <summary>A Store snapshot's CURRENT content version — a per-file content hash (the RHS of the sync comparison). NULL on Working rows.</summary>
    public string? ContentVersion { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    /// <summary>Soft-delete: a removed persona keeps its run history intact. NULL = active.</summary>
    public DateTimeOffset? DeletedDate { get; set; }

    /// <summary>Npgsql xmin optimistic-concurrency token (same convention as <see cref="AgentRun.Xmin"/>).</summary>
    public uint Xmin { get; set; }
}
