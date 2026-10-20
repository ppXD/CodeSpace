using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

public interface IExternalTriggerIngress : IScopedDependency
{
    Task<ExternalTriggerOutcome> ReceiveAsync(ExternalTriggerCall call, CancellationToken ct);
}

public sealed record ExternalTriggerCall(string Slug, string? PresentedKey, string? IdempotencyKey, string RawBody, bool IdempotencyRejected);

public sealed record ExternalTriggerOutcome(ExternalTriggerOutcomeKind Kind, Guid RunId, IReadOnlyList<string> Errors);

public enum ExternalTriggerOutcomeKind { Accepted, Deduplicated, NotFound, Unauthorized, InvalidPayload }
