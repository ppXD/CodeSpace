using System.Security.Cryptography;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Runtime;

/// <summary>
/// Restores each sealed row a previous-version worker could still execute to the shape the previous build wrote: the
/// exact payload in the public column and no ciphertext. That is every non-terminal run payload (a pending child is
/// started, a suspended one resumed, from its payload) and every Queued agent task. A Running agent task stays sealed:
/// Running is reached only from Queued, so no worker of either build launches it again, and a current worker that
/// re-attaches it — a rollback aborted, a rolling worker deploy — rebuilds the launch's redactor from the sealed copy.
/// Each row is a compare-and-swap on the ciphertext it opened and on the row still being in that state, so a worker
/// that rewrote or claimed the row meanwhile wins and the row is counted as raced.
///
/// <para>An agent task that carries a workflow-agent acceptance contract was staked from its MASKED task, and the
/// completion composer re-derives the unit's contract hash from <c>task_jsonb</c> of the unit's first attempt. Restoring
/// that first attempt's exact goal would leave its receipt attesting a hash the ledger never staked, so the unit is
/// re-staked from the restored task in the same transaction — exactly what the previous build staked for that row. A
/// retry's restore needs nothing: the composer keys the unit on the first attempt, which this pass does not touch.</para>
/// </summary>
public sealed class SealedPayloadUnsealService : ISealedPayloadUnsealService, IScopedDependency
{
    private static readonly WorkflowRunStatus[] LiveRunStatuses = [WorkflowRunStatus.Pending, WorkflowRunStatus.Enqueued, WorkflowRunStatus.Running, WorkflowRunStatus.Suspended];

    private readonly CodeSpaceDbContext _db;
    private readonly WorkflowSensitivePayloadSealer _sealer;
    private readonly ICompletionContractStore _contracts;
    private readonly ILogger<SealedPayloadUnsealService> _logger;

    public SealedPayloadUnsealService(CodeSpaceDbContext db, WorkflowSensitivePayloadSealer sealer, ICompletionContractStore contracts, ILogger<SealedPayloadUnsealService> logger)
    {
        _db = db;
        _sealer = sealer;
        _contracts = contracts;
        _logger = logger;
    }

    public async Task<UnsealNonTerminalPayloadsResponse> UnsealNonTerminalAsync(CancellationToken cancellationToken)
    {
        var agentTasks = await UnsealAgentTasksAsync(cancellationToken).ConfigureAwait(false);
        var runPayloads = await UnsealRunPayloadsAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogWarning("Unsealed non-terminal payloads for a worker rollback: {AgentTasks} agent tasks, {RunPayloads} run payloads restored; {Raced} raced, {Unopenable} unopenable", agentTasks.Restored, runPayloads.Restored, agentTasks.Raced + runPayloads.Raced, agentTasks.Unopenable + runPayloads.Unopenable);

        return new UnsealNonTerminalPayloadsResponse { AgentTasks = agentTasks.Restored, RunPayloads = runPayloads.Restored, Raced = agentTasks.Raced + runPayloads.Raced, Unopenable = agentTasks.Unopenable + runPayloads.Unopenable };
    }

    // ── Agent tasks ───────────────────────────────────────────────────────────

    private async Task<UnsealTally> UnsealAgentTasksAsync(CancellationToken cancellationToken)
    {
        var rows = await _db.AgentRun.AsNoTracking()
            .Where(r => r.SealedTaskCiphertext != null && r.Status == AgentRunStatus.Queued)
            .Select(r => new SealedAgentTask(r.Id, r.TeamId, r.WorkflowRunId, r.NodeId, r.IterationKey, r.CreatedDate, r.TaskJson, r.SealedTaskCiphertext!))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var tally = new UnsealTally();

        foreach (var row in rows) tally.Count(await UnsealAgentTaskAsync(row, cancellationToken).ConfigureAwait(false));

        return tally;
    }

    private async Task<UnsealOutcome> UnsealAgentTaskAsync(SealedAgentTask row, CancellationToken cancellationToken)
    {
        if (TryOpen(row.TaskJson, row.Ciphertext, "agent run", row.Id) is not { } exact) return UnsealOutcome.Unopenable;

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var restored = await _db.AgentRun
            .Where(r => r.Id == row.Id && r.SealedTaskCiphertext == row.Ciphertext && r.Status == AgentRunStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TaskJson, exact).SetProperty(r => r.SealedTaskCiphertext, (string?)null), cancellationToken).ConfigureAwait(false);

        if (restored != 1) return UnsealOutcome.Raced;

        await RestakeFirstAttemptAsync(row, exact, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return UnsealOutcome.Restored;
    }

    private async Task RestakeFirstAttemptAsync(SealedAgentTask row, string exactTaskJson, CancellationToken cancellationToken)
    {
        if (row.WorkflowRunId is not { } workflowRunId) return;

        var task = JsonSerializer.Deserialize<AgentTask>(exactTaskJson, AgentJson.Options)!;
        var requirements = AgentAcceptanceContract.WorkflowAgentRequirements(task, row.NodeId, row.IterationKey, workflowRunId);

        if (requirements.Count == 0 || await HasEarlierAttemptAsync(row, workflowRunId, cancellationToken).ConfigureAwait(false)) return;

        await _contracts.UpsertRequirementsAsync(workflowRunId, row.TeamId, requirements, cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> HasEarlierAttemptAsync(SealedAgentTask row, Guid workflowRunId, CancellationToken cancellationToken) =>
        _db.AgentRun.AnyAsync(r => r.WorkflowRunId == workflowRunId && r.TeamId == row.TeamId && r.NodeId == row.NodeId && r.IterationKey == row.IterationKey && r.CreatedDate < row.CreatedDate, cancellationToken);

    // ── Run payloads ──────────────────────────────────────────────────────────

    private async Task<UnsealTally> UnsealRunPayloadsAsync(CancellationToken cancellationToken)
    {
        var rows = await _db.WorkflowRunRequest.AsNoTracking()
            .Where(q => q.SealedPayloadCiphertext != null && _db.WorkflowRun.Any(r => r.RunRequestId == q.Id && LiveRunStatuses.Contains(r.Status)))
            .Select(q => new SealedRunPayload(q.Id, q.NormalizedPayloadJson, q.SealedPayloadCiphertext!))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var tally = new UnsealTally();

        foreach (var row in rows) tally.Count(await UnsealRunPayloadAsync(row, cancellationToken).ConfigureAwait(false));

        return tally;
    }

    private async Task<UnsealOutcome> UnsealRunPayloadAsync(SealedRunPayload row, CancellationToken cancellationToken)
    {
        if (TryOpen(row.PayloadJson, row.Ciphertext, "run request", row.Id) is not { } exact) return UnsealOutcome.Unopenable;

        var restored = await _db.WorkflowRunRequest
            .Where(q => q.Id == row.Id && q.SealedPayloadCiphertext == row.Ciphertext && _db.WorkflowRun.Any(r => r.RunRequestId == q.Id && LiveRunStatuses.Contains(r.Status)))
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.NormalizedPayloadJson, exact).SetProperty(q => q.SealedPayloadCiphertext, (string?)null), cancellationToken).ConfigureAwait(false);

        return restored == 1 ? UnsealOutcome.Restored : UnsealOutcome.Raced;
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    /// <summary>The exact payload, or null when this pod's Data Protection keys cannot open the ciphertext — that row stays sealed and is named in the log, never guessed at.</summary>
    private string? TryOpen(string publicJson, string ciphertext, string kind, Guid id)
    {
        try
        {
            return _sealer.Open(publicJson, ciphertext).Json;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or JsonException)
        {
            _logger.LogError(ex, "The sealed payload of {Kind} {Id} could not be opened; it stays sealed", kind, id);
            return null;
        }
    }

    private sealed record SealedAgentTask(Guid Id, Guid TeamId, Guid? WorkflowRunId, string? NodeId, string IterationKey, DateTimeOffset CreatedDate, string TaskJson, string Ciphertext);

    private sealed record SealedRunPayload(Guid Id, string PayloadJson, string Ciphertext);

    private enum UnsealOutcome { Restored, Raced, Unopenable }

    private sealed class UnsealTally
    {
        public int Restored { get; private set; }
        public int Raced { get; private set; }
        public int Unopenable { get; private set; }

        public void Count(UnsealOutcome outcome)
        {
            if (outcome == UnsealOutcome.Restored) Restored++;
            else if (outcome == UnsealOutcome.Raced) Raced++;
            else Unopenable++;
        }
    }
}
