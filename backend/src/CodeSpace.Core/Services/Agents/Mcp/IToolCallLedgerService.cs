using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// Owns the durable <c>ToolCallLedger</c> — the exactly-once + audit record of side-effecting MCP tool calls. The
/// MCP handler stays thin (Rule 16): it derives the server-side key + redacts the result, this service owns every
/// read/write of the table. The exactly-once guarantee is INSERT-first (no check-then-act TOCTOU): a claim INSERTs a
/// Pending row against the unique <c>(agent_run_id, idempotency_key)</c> index — two identical concurrent calls both
/// attempt the insert, the DB lets exactly one through, the loser catches the unique violation and reads the winner.
/// Status transitions are status-guarded CAS via <see cref="DbContext"/>.ExecuteUpdateAsync (mirrors
/// <see cref="AgentRunService"/>'s discipline). Every row carries the run's <c>TeamId</c>; reads are team-scoped.
/// </summary>
public interface IToolCallLedgerService
{
    /// <summary>
    /// Claim the right to execute this call. INSERTs a Pending row; on the unique-index collision (a concurrent or
    /// prior call for the same key) re-reads the existing row and returns <see cref="ToolCallClaimOutcome.Duplicate"/>
    /// (with the prior terminal result) when terminal, else <see cref="ToolCallClaimOutcome.InFlight"/>. Exactly one
    /// caller for a given key ever gets <see cref="ToolCallClaimOutcome.Proceed"/>.
    /// </summary>
    Task<ToolCallClaim> TryClaimAsync(Guid agentRunId, Guid teamId, string toolKind, string idempotencyKey, string inputHash, long fenceEpoch, CancellationToken cancellationToken);

    /// <summary>Status-guarded CAS Pending → terminal (mirrors <see cref="AgentRunService"/> completion), team-scoped (defense-in-depth — the design mandates all reads team-scoped). Stores the ALREADY-REDACTED result/error. Throws when the transition is illegal or lost the CAS.</summary>
    Task RecordTerminalAsync(Guid ledgerId, Guid teamId, ToolCallLedgerStatus status, string? resultJson, string? error, CancellationToken cancellationToken);

    /// <summary>Team-scoped audit read of a run's ledger rows, newest first (like <see cref="AgentRunService"/>.GetEventsAsync — a foreign run id returns empty).</summary>
    Task<IReadOnlyList<ToolCallLedger>> GetForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken);
}

public sealed class ToolCallLedgerService : IToolCallLedgerService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<ToolCallLedgerService> _logger;

    public ToolCallLedgerService(CodeSpaceDbContext db, ILogger<ToolCallLedgerService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ToolCallClaim> TryClaimAsync(Guid agentRunId, Guid teamId, string toolKind, string idempotencyKey, string inputHash, long fenceEpoch, CancellationToken cancellationToken)
    {
        var row = new ToolCallLedger
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            AgentRunId = agentRunId,
            ToolKind = toolKind,
            IdempotencyKey = idempotencyKey,
            InputHash = inputHash,
            Status = ToolCallLedgerStatus.Pending,
            FenceEpoch = fenceEpoch,
        };

        _db.ToolCallLedger.Add(row);

        try
        {
            // INSERT-first against the unique (agent_run_id, idempotency_key) index — the serialization point. Two
            // identical concurrent calls both reach here; the DB lets exactly one INSERT win.
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return ToolCallClaim.Proceed(row.Id);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the claim race — a concurrent or prior call already owns this (run, key). Re-read the winner
            // (mirrors ChatBotService's create-race recovery) and either return its terminal result (Duplicate) or
            // signal it's still in flight — NEVER double-run the side effect.
            _db.ChangeTracker.Clear();

            return await ReadExistingClaimAsync(agentRunId, teamId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RecordTerminalAsync(Guid ledgerId, Guid teamId, ToolCallLedgerStatus status, string? resultJson, string? error, CancellationToken cancellationToken)
    {
        if (!ToolCallLedgerStateMachine.IsTerminal(status))
            throw new ToolCallLedgerTransitionException($"ToolCallLedger terminal status must be terminal — got {status}.");

        // Read the current status FRESH + untracked (team-scoped — defense-in-depth), then flip via a status-guarded
        // CAS (NOT a tracked save on the xmin token — same rationale as AgentRunService.CompleteCoreAsync: a concurrent
        // transition wins the CAS and this side loses cleanly, rather than a tracked save stranding the row on
        // optimistic concurrency).
        var current = await _db.ToolCallLedger.AsNoTracking()
            .Where(l => l.Id == ledgerId && l.TeamId == teamId)
            .Select(l => (ToolCallLedgerStatus?)l.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ToolCallLedgerTransitionException($"ToolCallLedger {ledgerId} not found.");

        if (!ToolCallLedgerStateMachine.IsLegalTransition(current, status))
            throw new ToolCallLedgerTransitionException($"Illegal ToolCallLedger transition {current} → {status} (ledger {ledgerId}).");

        var now = DateTimeOffset.UtcNow;

        var flipped = await _db.ToolCallLedger
            .Where(l => l.Id == ledgerId && l.TeamId == teamId && l.Status == current)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, status)
                .SetProperty(l => l.ResultJson, resultJson)
                .SetProperty(l => l.Error, error)
                .SetProperty(l => l.LastModifiedDate, now), cancellationToken)
            .ConfigureAwait(false);

        if (flipped == 0)
            throw new ToolCallLedgerTransitionException($"ToolCallLedger {ledgerId} was no longer {current} at terminal record — a concurrent transition won the race.");

        _logger.LogInformation("Tool call ledger recorded terminal. LedgerId={LedgerId} Status={Status}", ledgerId, status);
    }

    public async Task<IReadOnlyList<ToolCallLedger>> GetForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.ToolCallLedger.AsNoTracking()
            .Where(l => l.AgentRunId == agentRunId && l.TeamId == teamId)
            .OrderByDescending(l => l.CreatedDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<ToolCallClaim> ReadExistingClaimAsync(Guid agentRunId, Guid teamId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var existing = await _db.ToolCallLedger.AsNoTracking()
            .SingleOrDefaultAsync(l => l.AgentRunId == agentRunId && l.TeamId == teamId && l.IdempotencyKey == idempotencyKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ToolCallLedger row for run {agentRunId} key was missing after a unique-violation race.");

        return ToolCallLedgerStateMachine.IsTerminal(existing.Status)
            ? ToolCallClaim.Duplicate(existing.Id, existing.Status, existing.ResultJson, existing.Error)
            : ToolCallClaim.InFlight(existing.Id);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}

/// <summary>A ToolCallLedger row was asked to make a transition its lifecycle doesn't allow, or a status-guarded CAS lost the race. Mirrors <see cref="AgentRunTransitionException"/>.</summary>
public sealed class ToolCallLedgerTransitionException : Exception
{
    public ToolCallLedgerTransitionException(string message) : base(message) { }
}
