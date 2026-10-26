using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

internal sealed record ExternalTriggerVerificationTicket(Guid TriggerId, Guid TeamId, Guid WorkflowId, string NodeId, string KeyDigest);

public sealed class ExternalTriggerVerificationContext : IScopedDependency
{
    internal ExternalTriggerVerificationTicket? Ticket { get; set; }
}
