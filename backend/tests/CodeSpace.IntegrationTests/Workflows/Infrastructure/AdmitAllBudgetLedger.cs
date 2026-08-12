using CodeSpace.Core.Services.Workflows.Budget;

namespace CodeSpace.Tests.Fakes;

/// <summary>A guard-neutral budget ledger: admits everything, settles nothing — the W-hard LLM budget guard only activates when a pushed scope carries a cap, which these fixtures never do; the fake keeps the ctor honest without a DbContext.</summary>
public sealed class AdmitAllBudgetLedger : IBudgetLedger
{
    public Task<BudgetAdmission> ReserveAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal estimateUsd, decimal capUsd, string priceVersion, Guid? parentReservationId, DateTimeOffset? expiresAt, CancellationToken cancellationToken) =>
        Task.FromResult(new BudgetAdmission(true, Guid.NewGuid(), 0m, capUsd, null));

    public Task SettleAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal? actualUsd, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ReleaseAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<int> ExpireOverdueAsync(int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<decimal> CommittedUsdAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) => Task.FromResult(0m);
    public Task<int> ReconcileDanglingAsync(string kindPrefix, int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);

}
