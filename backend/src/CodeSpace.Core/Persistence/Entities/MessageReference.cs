namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// A single typed reference embedded in a <see cref="Message"/> — the generic <c>@</c> system.
///
/// <para><b>Deliberately schema-free on the target.</b> <see cref="RefType"/> is an open
/// string namespace (same design as workflow <c>TypeKey</c> / activation kind elsewhere in
/// this codebase) and <see cref="RefId"/> is the type-specific identifier. Referencing a new
/// kind of thing — <c>@incident</c>, <c>@deploy</c>, <c>@code_location</c>, <c>@calendar_event</c>
/// — needs ZERO migration and ZERO change here: a new <c>RefType</c> value, a frontend chip
/// renderer, and (optionally) a resolver that turns the <see cref="RefId"/> into a display
/// label + in-app deep link. Nothing about chat is hardcoded to "user" or "PR".</para>
///
/// <para>Known ref types at launch (not an enum — just the strings the resolvers understand):
/// <c>user</c>, <c>pull_request</c>, <c>repository</c>, <c>workflow</c>, <c>workflow_run</c>,
/// <c>code_location</c>. The set grows by convention, not by schema.</para>
///
/// <para><b>Why a table and not just inline tokens.</b> The body already carries the inline
/// token so a message renders standalone. This table is the REVERSE index: "every message
/// that mentions PR #123" (backlinks on the PR page), "my unread @mentions" (notification
/// inbox), "what does this workflow get talked about in". All become single indexed lookups
/// on <c>(team_id, ref_type, ref_id)</c> instead of full-text scanning every message body.</para>
/// </summary>
public class MessageReference : IEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    /// <summary>Denormalised team — the reverse-lookup index leads with it so a tenant's
    /// "@mentions of X" query never scans another team's references.</summary>
    public Guid TeamId { get; set; }

    /// <summary>Open namespace: <c>user</c> | <c>pull_request</c> | <c>workflow</c> | … . Not an
    /// enum on purpose — new reference kinds are a string, never a migration.</summary>
    public string RefType { get; set; } = default!;

    /// <summary>Target identifier, interpreted per <see cref="RefType"/> (a user guid, a PR
    /// "repoId#number", a workflow id, a "repoId:sha:path:line" code location, …). Stored as
    /// text so any addressing scheme fits without a column change.</summary>
    public string RefId { get; set; } = default!;

    /// <summary>
    /// Optional cached display context — the label to render in the chip, a deep-link hint,
    /// whatever the resolver wants to memoise so rendering doesn't re-resolve on every view.
    /// jsonb so the shape can evolve per ref type without schema churn.
    /// </summary>
    public string? RefMetadataJson { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    public Message Message { get; set; } = default!;
}
