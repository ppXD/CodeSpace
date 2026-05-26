namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Per-workflow source configuration.
///
/// An "activation" is anything that can produce a WorkflowRunRequest for a workflow:
/// a manual UI click, a scheduled cron firing, an HTTP API call, a webhook delivery,
/// a child-workflow call, a replay. Each row pairs a workflow with one source kind +
/// its filter / config (e.g. "GitHub PRs from owner=octocat, opened only").
///
/// The string TypeKey discriminator (e.g. "provider.github.pull_request",
/// "schedule.cron", "manual") keeps the source list open-ended — new sources add zero
/// schema churn. ConfigJson holds source-specific filter parameters; its schema lives
/// in the source-type's plugin manifest, not in the DB.
/// </summary>
public class WorkflowActivation : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid WorkflowId { get; set; }

    /// <summary>
    /// Source discriminator. Examples: "manual", "schedule.cron", "api",
    /// "provider.github.pull_request", "provider.gitlab.merge_request".
    /// Dotted namespace ⇒ greppable + extensible without enum churn.
    /// </summary>
    public string TypeKey { get; set; } = default!;

    /// <summary>
    /// Source-type-specific filter / parameters. Schema is owned by the source plugin
    /// (e.g. cron expression for schedule.cron, repo+branch+event-name filter for
    /// provider.github.*). Persisted as jsonb.
    /// </summary>
    public string ConfigJson { get; set; } = "{}";

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }

    public Workflow Workflow { get; set; } = default!;
}
