namespace CodeSpace.Core.Services.Outbox;

/// <summary>
/// Stable string discriminators for outbox messages. Single source of truth — handlers and
/// enqueueing code both reference these constants. Renaming any constant requires a DB
/// migration to update existing rows.
///
/// <para>The outbox exists solely for external side-effects with no recoverable status field
/// on the source entity (currently: webhook registration on provider APIs). Workflow run
/// dispatch uses <c>workflow_run.Status</c> as the queue (PostBoy pattern) via
/// <see cref="Workflows.Dispatch.IWorkflowRunDispatcher"/>, not the outbox.</para>
/// </summary>
public static class OutboxMessageTypes
{
    public const string RegisterWebhook = "RegisterWebhook";
}
