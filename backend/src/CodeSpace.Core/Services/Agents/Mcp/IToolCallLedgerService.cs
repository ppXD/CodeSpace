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

    /// <summary>Status-guarded CAS Pending → AwaitingApproval (durable mid-turn HITL, item D2 — mirrors <see cref="RecordTerminalAsync"/>'s discipline), team-scoped. Stamps <c>ApprovalToken</c> + <c>ApprovalDeadlineAt</c> so the row is resolvable BEFORE the card posts (the token is the authority; the card's message id is a best-effort follow-up). Returns false when the CAS is lost (the row already moved — e.g. a re-claim of an already-parked call), so the handler re-reads + re-blocks rather than posting a second card.</summary>
    Task<bool> TryBeginApprovalAsync(Guid ledgerId, Guid teamId, string approvalToken, DateTimeOffset deadlineAt, CancellationToken cancellationToken);

    /// <summary>Stamp the posted approval-card message id on an AwaitingApproval row (best-effort, team-scoped) — the token + deadline already make the row resolvable, so a lost CAS here is harmless. Guards on <c>ApprovalMessageId IS NULL</c> so exactly one card is ever recorded per (run, key).</summary>
    Task SetApprovalMessageAsync(Guid ledgerId, Guid teamId, Guid messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Single-winner CAS claiming an APPROVED approval row for execution: AwaitingApproval → Running, guarded on
    /// <c>Status == AwaitingApproval AND ApprovedAt IS NOT NULL</c> (team-scoped). This is the exactly-once-after-approve
    /// gate — it flips the row out of the approvable state BEFORE the side effect runs, so exactly one of N concurrent
    /// executors of the same approved (run, key) wins (returns true) and runs <c>tool.CallAsync</c>; every loser sees the
    /// row already Running/terminal, gets false, and re-reads + replays rather than re-running the side effect. Returns
    /// false when the row is not an approved AwaitingApproval (not yet approved, already claimed, or already terminal).
    /// </summary>
    Task<bool> TryBeginExecutionAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>Team-scoped focused read of one row's {Status, ApprovedAt, ResultJson, Error} — the post-wake authority a blocked handler re-reads to decide the outcome. Null when the (ledger, team) row is absent (a foreign id finds nothing — fail-closed).</summary>
    Task<ToolCallApprovalState?> ReadApprovalStateAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Reaper sweep (item D3): durably expire every UNDECIDED approval past its deadline so a re-call gets a clean
    /// terminal instead of re-opening forever. Candidate set: <c>Status == AwaitingApproval AND ApprovedAt == null AND
    /// ApprovalDeadlineAt != null AND ApprovalDeadlineAt &lt; now</c>; each candidate gets a per-row status-guarded CAS
    /// <c>AwaitingApproval → Expired</c> (mirrors <see cref="RecordTerminalAsync"/>'s discipline) guarded ALSO on
    /// <c>ApprovedAt == null</c>, so an approved-but-not-yet-executed row (its execution claim in flight) is NEVER
    /// expired. Returns only the rows whose CAS won (affected == 1), each with its <c>ApprovalMessageId</c> for the card
    /// mirror. Team-agnostic (an internal job, no actor) but every CAS is per-row + single-winner, so two concurrent
    /// sweeps expire each row exactly once. Bounded per run (a backlog continues on the next tick); the cap is logged,
    /// never silently truncated.
    /// </summary>
    Task<IReadOnlyList<ExpiredToolApproval>> ExpireStaleApprovalsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Team-scoped audit read of a run's ledger rows, newest first (like <see cref="AgentRunService"/>.GetEventsAsync — a foreign run id returns empty).</summary>
    Task<IReadOnlyList<ToolCallLedger>> GetForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken);
}

public sealed class ToolCallLedgerService : IToolCallLedgerService, IScopedDependency
{
    /// <summary>The audit reason stamped on a row the reaper expires — load-bearing string the replay path surfaces to the model on a re-call.</summary>
    public const string ApprovalExpiredError = "approval expired (no decision before the deadline)";

    /// <summary>Per-sweep cap so a large backlog can't run one reaper tick forever; the next tick continues. Capping is logged (never silently truncated).</summary>
    public const int ExpiryBatchSize = 200;

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

            return await ReadExistingClaimAsync(agentRunId, idempotencyKey, cancellationToken).ConfigureAwait(false);
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

    public async Task<bool> TryBeginApprovalAsync(Guid ledgerId, Guid teamId, string approvalToken, DateTimeOffset deadlineAt, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Status-guarded CAS Pending → AwaitingApproval (mirrors RecordTerminalAsync's ExecuteUpdate discipline). The
        // Status == Pending guard is the single-winner: a concurrent transition (a re-claim that already parked, a
        // racing terminal) leaves the row not-Pending so this update affects 0 rows → false, and the caller re-reads
        // + re-blocks instead of posting a second card.
        var flipped = await _db.ToolCallLedger
            .Where(l => l.Id == ledgerId && l.TeamId == teamId && l.Status == ToolCallLedgerStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, ToolCallLedgerStatus.AwaitingApproval)
                .SetProperty(l => l.ApprovalToken, approvalToken)
                .SetProperty(l => l.ApprovalDeadlineAt, deadlineAt)
                .SetProperty(l => l.LastModifiedDate, now), cancellationToken)
            .ConfigureAwait(false);

        if (flipped > 0) _logger.LogInformation("Tool call ledger awaiting approval. LedgerId={LedgerId}", ledgerId);

        return flipped > 0;
    }

    public async Task SetApprovalMessageAsync(Guid ledgerId, Guid teamId, Guid messageId, CancellationToken cancellationToken) =>
        // Best-effort, idempotent (the approval_message_id IS NULL guard → exactly one card recorded per row). The
        // token + deadline stamped by TryBeginApprovalAsync already make the row resolvable, so a lost CAS is harmless.
        await _db.ToolCallLedger
            .Where(l => l.Id == ledgerId && l.TeamId == teamId && l.ApprovalMessageId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.ApprovalMessageId, messageId)
                .SetProperty(l => l.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

    public async Task<bool> TryBeginExecutionAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Single-winner CAS AwaitingApproval → Running, guarded ALSO on ApprovedAt IS NOT NULL so only an APPROVED row is
        // claimed (mirrors RecordTerminalAsync's ExecuteUpdate discipline). This flips the row out of the approvable
        // state BEFORE the side effect runs: of N executors racing the same approved (run, key) exactly one update
        // affects 1 row (true → run the side effect once), every loser affects 0 (false → re-read + replay). This is the
        // exactly-once-after-approve gate the terminal CAS alone cannot provide, since that runs AFTER the side effect.
        var claimed = await _db.ToolCallLedger
            .Where(l => l.Id == ledgerId && l.TeamId == teamId && l.Status == ToolCallLedgerStatus.AwaitingApproval && l.ApprovedAt != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, ToolCallLedgerStatus.Running)
                .SetProperty(l => l.LastModifiedDate, now), cancellationToken)
            .ConfigureAwait(false);

        if (claimed > 0) _logger.LogInformation("Tool call ledger claimed for execution. LedgerId={LedgerId}", ledgerId);

        return claimed > 0;
    }

    public async Task<ToolCallApprovalState?> ReadApprovalStateAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.ToolCallLedger.AsNoTracking()
            .Where(l => l.Id == ledgerId && l.TeamId == teamId)
            .Select(l => new ToolCallApprovalState { Status = l.Status, ApprovedAt = l.ApprovedAt, ResultJson = l.ResultJson, Error = l.Error })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ExpiredToolApproval>> ExpireStaleApprovalsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Candidate set (bounded): undecided approvals past their deadline. ApprovedAt == null is LOAD-BEARING — an
        // approved-but-not-yet-executed row belongs to an in-flight execution claim and must NEVER be expired. Take
        // ExpiryBatchSize + 1 so a full page tells us the sweep was capped (logged below — no silent truncation).
        var candidates = await _db.ToolCallLedger.AsNoTracking()
            .Where(l => l.Status == ToolCallLedgerStatus.AwaitingApproval && l.ApprovedAt == null && l.ApprovalDeadlineAt != null && l.ApprovalDeadlineAt < now)
            .OrderBy(l => l.ApprovalDeadlineAt)
            .Take(ExpiryBatchSize + 1)
            .Select(l => new { l.Id, l.TeamId, l.ApprovalMessageId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var capped = candidates.Count > ExpiryBatchSize;

        var expired = new List<ExpiredToolApproval>(Math.Min(candidates.Count, ExpiryBatchSize));

        foreach (var c in candidates.Take(ExpiryBatchSize))
            if (await TryExpireOneAsync(c.Id, now, cancellationToken).ConfigureAwait(false))
                expired.Add(new ExpiredToolApproval { LedgerId = c.Id, TeamId = c.TeamId, ApprovalMessageId = c.ApprovalMessageId });

        if (expired.Count > 0) _logger.LogInformation("Tool call approval reaper expired {Expired} stale approval(s)", expired.Count);

        if (capped) _logger.LogWarning("Tool call approval reaper hit the per-sweep cap of {Cap} — a backlog remains for the next tick", ExpiryBatchSize);

        return expired;
    }

    // Per-row single-winner CAS AwaitingApproval → Expired (mirrors RecordTerminalAsync's ExecuteUpdate discipline).
    // Guarded ALSO on ApprovedAt == null so an approved row that was concurrently stamped between the candidate read and
    // this update is NOT expired (the not-expire-approved guard, load-bearing in BOTH the query and here). affected == 1
    // means this sweep won the row; 0 means a concurrent sweep / approve / handler already moved it — skip it cleanly.
    private async Task<bool> TryExpireOneAsync(Guid ledgerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var affected = await _db.ToolCallLedger
            .Where(l => l.Id == ledgerId && l.Status == ToolCallLedgerStatus.AwaitingApproval && l.ApprovedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, ToolCallLedgerStatus.Expired)
                .SetProperty(l => l.Error, ApprovalExpiredError)
                .SetProperty(l => l.LastModifiedDate, now), cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    public async Task<IReadOnlyList<ToolCallLedger>> GetForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.ToolCallLedger.AsNoTracking()
            .Where(l => l.AgentRunId == agentRunId && l.TeamId == teamId)
            .OrderByDescending(l => l.CreatedDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<ToolCallClaim> ReadExistingClaimAsync(Guid agentRunId, string idempotencyKey, CancellationToken cancellationToken)
    {
        // Keyed on the unique index (agent_run_id, idempotency_key) — which is team-AGNOSTIC, so the winner's row is
        // ALWAYS found here regardless of which team won. Filtering by TeamId too would let a cross-team race on the
        // same (run, key) find nothing and throw a 500; the unique index already makes (run, key) globally unique, so
        // the predicate is exactly the index and TeamId is read off the found row (it's invariant per run anyway).
        var existing = await _db.ToolCallLedger.AsNoTracking()
            .SingleOrDefaultAsync(l => l.AgentRunId == agentRunId && l.IdempotencyKey == idempotencyKey, cancellationToken).ConfigureAwait(false)
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
