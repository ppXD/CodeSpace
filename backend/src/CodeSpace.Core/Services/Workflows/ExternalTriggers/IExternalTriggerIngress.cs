using CodeSpace.Core.DependencyInjection;
using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

public interface IExternalTriggerIngress : IScopedDependency
{
    /// <summary>Resolves and authenticates the endpoint before any body is read.</summary>
    Task<ExternalTriggerVerification> VerifyAsync(string slug, string? presentedKey, CancellationToken ct);

    Task<ExternalTriggerOutcome> ReceiveAsync(ExternalTriggerCall call, CancellationToken ct);
}

/// <summary>The authenticated endpoint identity retained between body read and delivery.</summary>
public sealed record ExternalTriggerVerification(
    ExternalTriggerOutcomeKind Kind,
    Guid TriggerId,
    Guid ActivationId,
    Guid WorkflowId,
    Guid TeamId,
    int WorkflowVersion,
    string ActivationSnapshotJson,
    JsonElement InputSchema);

public sealed record ExternalTriggerCall(string Slug, string? PresentedKey, string? IdempotencyKey, string RawBody, bool IdempotencyRejected);

public sealed record ExternalTriggerOutcome(ExternalTriggerOutcomeKind Kind, Guid RunId, IReadOnlyList<string> Errors);

public enum ExternalTriggerOutcomeKind { Accepted, Deduplicated, NotFound, Unauthorized, Forbidden, Unprocessable, InvalidPayload, RateLimited }
