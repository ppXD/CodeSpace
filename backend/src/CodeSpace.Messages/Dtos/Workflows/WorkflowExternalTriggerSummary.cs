namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// Credentials are absent because keys are obtainable only through the permission-gated reveal endpoint.
/// </summary>
public sealed record WorkflowExternalTriggerSummary(Guid Id, Guid WorkflowId, string NodeId, string Slug);

public sealed record WorkflowExternalTriggersResult(IReadOnlyList<WorkflowExternalTriggerSummary> Triggers);
