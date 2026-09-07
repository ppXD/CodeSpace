using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Workflows.Budget;

/// <summary>One durable admission per physical HTTP invocation. This receipt authorizes exactly one Send, not a replay.</summary>
public interface IPhysicalLlmInvocationLedger
{
    Task<BudgetAdmission> AdmitPhysicalAsync(PhysicalLlmAdmission input, CancellationToken cancellationToken);
    Task SettlePhysicalAsync(PhysicalLlmSettlement input, CancellationToken cancellationToken);
}

public sealed record PhysicalLlmAdmission
{
    public required Guid InvocationId { get; init; }
    public required Guid LogicalCallId { get; init; }
    public required Guid CandidateId { get; init; }
    public required int CandidateOrdinal { get; init; }
    public required Guid RunId { get; init; }
    public required Guid TeamId { get; init; }
    public string? NodeId { get; init; }
    public string IterationKey { get; init; } = "";
    public required string Purpose { get; init; }
    public required string Provider { get; init; }
    public required string RequestedModel { get; init; }
    public required decimal EstimateUsd { get; init; }
    public required decimal CapUsd { get; init; }
    public required string PricingSnapshotJson { get; init; }
    public required string PricingVersion { get; init; }
}

public sealed record PhysicalLlmSettlement
{
    public required Guid InvocationId { get; init; }
    public required Guid RunId { get; init; }
    public required Guid TeamId { get; init; }
    public string? ObservedModel { get; init; }
    public LlmUsage Usage { get; init; } = LlmUsage.None;
    public int? HttpStatusCode { get; init; }
    public string? Status { get; init; }
    public string? ErrorCode { get; init; }
}
