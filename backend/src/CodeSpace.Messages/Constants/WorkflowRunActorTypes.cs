namespace CodeSpace.Messages.Constants;

/// <summary>
/// Canonical <c>actor_type</c> values for <c>workflow_run_request</c>. Centralising the
/// strings here makes the source-of-truth explicit and prevents drift across call sites;
/// pinned by <c>WorkflowRunActorTypesTests</c>.
///
/// <para>Semantic: <c>actor_type</c> answers "what KIND of agent initiated this run", while
/// <c>actor_id</c> answers "which specific one". For <c>User</c> actors the id is the user's
/// Guid; for <c>Webhook</c> it's null (the provider isn't a CodeSpace identity); for
/// <c>System</c> (cron, sub-workflow) it's <c>SystemUsers.SeederId</c> or the parent run's
/// originating actor.</para>
/// </summary>
public static class WorkflowRunActorTypes
{
    /// <summary>A real CodeSpace user clicked Run / hit an API. Pair with the user's <c>actor_id</c>.</summary>
    public const string User = "user";

    /// <summary>An inbound provider webhook fired through the dispatcher. <c>actor_id</c> is null.</summary>
    public const string Webhook = "webhook";

    /// <summary>An internal CodeSpace component initiated the run (cron scheduler, sub-workflow invoker).</summary>
    public const string System = "system";
}
