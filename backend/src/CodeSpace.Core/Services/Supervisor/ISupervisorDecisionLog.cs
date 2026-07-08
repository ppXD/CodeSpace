using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Owns the durable <c>SupervisorDecisionRecord</c> ledger — the exactly-once, replayable record of a supervisor's
/// decisions (PR-E E1 substrate). The abstraction sits at the concern root (Rule 18.3); this service owns every
/// read/write of the table (Rule 16). Mirrors <c>IToolCallLedgerService</c> EXACTLY: the exactly-once guarantee is
/// INSERT-first (no check-then-act TOCTOU) — a claim INSERTs a Pending row against the unique
/// <c>(supervisor_run_id, idempotency_key)</c> index; two identical concurrent calls both attempt the insert, the DB
/// lets exactly one through, the loser catches the unique violation and reads the winner. Status transitions are
/// status-guarded CAS via <c>ExecuteUpdateAsync</c>, gated by <see cref="SupervisorDecisionStateMachine"/>. Every row
/// carries the run's <c>TeamId</c>; reads are team-scoped.
///
/// <para>The idempotency key is SERVER-derived via <see cref="DeriveIdempotencyKey"/> — the service never accepts a
/// model-supplied key as the dedup key. (E1 accepts the already-derived key from the caller; the caller derives it with
/// the helper. E2 wires the derivation into the supervisor loop.)</para>
/// </summary>
public interface ISupervisorDecisionLog
{
    /// <summary>
    /// Claim the right to execute this decision. INSERTs a Pending row; on the unique-index collision (a concurrent or
    /// prior decision for the same key) re-reads the existing row and returns <see cref="SupervisorDecisionClaimOutcome.Duplicate"/>
    /// (with the prior terminal outcome) when terminal, else <see cref="SupervisorDecisionClaimOutcome.InFlight"/>. Exactly
    /// one caller for a given key ever gets <see cref="SupervisorDecisionClaimOutcome.Proceed"/>.
    /// </summary>
    Task<SupervisorDecisionClaim> TryClaimAsync(Guid supervisorRunId, Guid teamId, string decisionKind, string idempotencyKey, string inputHash, string payloadJson, long fenceEpoch, CancellationToken cancellationToken);

    /// <summary>
    /// Single-winner CAS claiming a decision for execution: Pending → Running, team-scoped. This is the must-fix-#2 gate
    /// — it flips the row out of the claimable state BEFORE the side effect runs, so the synchronous path does NOT rely
    /// on the INSERT alone. Of N concurrent executors of the same claimed (run, key) exactly one update affects 1 row
    /// (true → run the side effect once); every loser affects 0 (false → re-read + replay). Returns whether THIS caller won.
    /// </summary>
    Task<bool> TryBeginExecutionAsync(Guid decisionId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>Status-guarded CAS Running → terminal (Succeeded/Failed/Expired), team-scoped (defense-in-depth). Stores the execution outcome/error. Throws when the transition is illegal or lost the CAS. Gated by <see cref="SupervisorDecisionStateMachine"/>.</summary>
    Task RecordTerminalAsync(Guid decisionId, Guid teamId, SupervisorDecisionStatus status, string? outcomeJson, string? error, CancellationToken cancellationToken);

    /// <summary>Team-scoped audit/replay read of a run's decision rows, ordered by <c>Sequence</c> (the replay tape). A foreign run id returns empty.</summary>
    Task<IReadOnlyList<SupervisorDecisionRecord>> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// The SETTLED half of <see cref="GetForRunAsync"/>'s tape, already mapped to the noun <see cref="SupervisorPriorDecision"/>
    /// readers like <see cref="SupervisorOutcome.ReadFinalIntegratedBranch"/> consume — for a caller that only needs the
    /// run's durable facts (the Room's Open-PR action, its capability-resolver gating) and not the full live-turn
    /// rehydrate fold (agent-results / acceptance-grade / ask-human enrichment, which <c>SupervisorTurnService</c> owns).
    /// </summary>
    Task<IReadOnlyList<SupervisorPriorDecision>> GetTerminalDecisionsAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Fold a SETTLED-outcome enrichment into a SETTLED decision's recorded outcome — an ask_human answer (PR-E E4)
    /// OR a spawn/retry decision's settled agent results (SOTA #2): rewrite the row's <c>OutcomeJson</c> with
    /// <paramref name="foldedOutcomeJson"/>, team-scoped. A status-AGNOSTIC outcome enrichment — NOT a lifecycle
    /// transition: the decision is already terminal, and a durable fact (the human's answer from the resolved Action
    /// wait, or the spawned agents' terminal results) is being recorded onto the audit row so the ledger is the
    /// durable record the decider reads next turn. Idempotent — re-writing the same bytes is a no-op.
    /// </summary>
    Task UpdateOutcomeAsync(Guid decisionId, Guid teamId, string foldedOutcomeJson, CancellationToken cancellationToken);

    /// <summary>
    /// Reaper sweep (the maintenance job's worker): durably expire every stale UNDECIDED decision — rows still
    /// <c>Pending</c> created before <paramref name="olderThan"/>. Each candidate gets a per-row status-guarded CAS
    /// <c>Pending → Expired</c> (mirrors <see cref="RecordTerminalAsync"/>'s discipline). Team-AGNOSTIC (an internal job,
    /// no actor) but every CAS is per-row single-winner, so two concurrent sweeps expire each row exactly once. Bounded
    /// per sweep (a backlog continues on the next tick; the cap is logged, never silently truncated). Returns the count
    /// durably expired (the CAS winners).
    /// </summary>
    Task<int> ExpireStalePendingAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}

public sealed class SupervisorDecisionLog : ISupervisorDecisionLog, IScopedDependency
{
    /// <summary>The audit reason stamped on a row the reaper expires — surfaced to the replay path on a re-claim.</summary>
    public const string StalePendingError = "supervisor decision expired (stale Pending swept by the reaper)";

    /// <summary>Per-sweep cap so a large backlog can't run one reaper tick forever; the next tick continues. Capping is logged (never silently truncated).</summary>
    public const int ExpiryBatchSize = 200;

    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<SupervisorDecisionLog> _logger;

    public SupervisorDecisionLog(CodeSpaceDbContext db, ILogger<SupervisorDecisionLog> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Server-derived idempotency key: <c>decisionKind:SHA-256(canonical(payloadJson))</c>, optionally bound to a caller
    /// <paramref name="turnDiscriminator"/> (the supervisor turn/round — so the SAME decision payload in a later turn is
    /// a distinct, re-executable decision). NEVER derived from a model-supplied key — the inputs are server-side. Pure +
    /// deterministic so it's unit-pinned. <paramref name="payloadJson"/> is hashed verbatim (the caller canonicalizes
    /// the JSON before passing it — same canonical bytes → same hash → same key).
    /// </summary>
    public static string DeriveIdempotencyKey(string decisionKind, string payloadJson, string? turnDiscriminator = null)
    {
        var seed = turnDiscriminator is null ? payloadJson : $"{turnDiscriminator}\n{payloadJson}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

        return $"{decisionKind}:{hash}";
    }

    /// <summary>Lower-case hex SHA-256 of the canonical payload bytes (64 chars) — the key already binds this, surfaced separately for the audit column.</summary>
    public static string HashPayload(string payloadJson) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));

    public async Task<SupervisorDecisionClaim> TryClaimAsync(Guid supervisorRunId, Guid teamId, string decisionKind, string idempotencyKey, string inputHash, string payloadJson, long fenceEpoch, CancellationToken cancellationToken)
    {
        var row = new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            SupervisorRunId = supervisorRunId,
            DecisionKind = decisionKind,
            IdempotencyKey = idempotencyKey,
            InputHash = inputHash,
            PayloadJson = payloadJson,
            Status = SupervisorDecisionStatus.Pending,
            FenceEpoch = fenceEpoch,
        };

        _db.SupervisorDecisionRecord.Add(row);

        try
        {
            // INSERT-first against the unique (supervisor_run_id, idempotency_key) index — the serialization point. Two
            // identical concurrent decisions both reach here; the DB lets exactly one INSERT win.
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return SupervisorDecisionClaim.Proceed(row.Id);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the claim race — a concurrent or prior decision already owns this (run, key). Re-read the winner and
            // either return its terminal outcome (Duplicate) or signal it's still in flight — NEVER double-execute.
            _db.ChangeTracker.Clear();

            return await ReadExistingClaimAsync(supervisorRunId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> TryBeginExecutionAsync(Guid decisionId, Guid teamId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Single-winner CAS Pending → Running (mirrors RecordTerminalAsync's ExecuteUpdate discipline). The Status ==
        // Pending guard is the must-fix-#2 gate: it flips the row out of the claimable state BEFORE the side effect runs,
        // so of N executors racing the same claimed (run, key) exactly one update affects 1 row (true → run the side
        // effect once), every loser affects 0 (false → re-read + replay). This is the single-winner guarantee the INSERT
        // alone cannot provide for the synchronous execution path.
        var claimed = await _db.SupervisorDecisionRecord
            .Where(d => d.Id == decisionId && d.TeamId == teamId && d.Status == SupervisorDecisionStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, SupervisorDecisionStatus.Running)
                .SetProperty(d => d.LastModifiedDate, now), cancellationToken)
            .ConfigureAwait(false);

        if (claimed > 0) _logger.LogInformation("Supervisor decision claimed for execution. DecisionId={DecisionId}", decisionId);

        return claimed > 0;
    }

    public async Task RecordTerminalAsync(Guid decisionId, Guid teamId, SupervisorDecisionStatus status, string? outcomeJson, string? error, CancellationToken cancellationToken)
    {
        if (!SupervisorDecisionStateMachine.IsTerminal(status))
            throw new SupervisorDecisionTransitionException($"SupervisorDecision terminal status must be terminal — got {status}.");

        // Read the current status FRESH + untracked (team-scoped — defense-in-depth), then flip via a status-guarded CAS
        // (NOT a tracked save on the xmin token — same rationale as ToolCallLedgerService.RecordTerminalAsync).
        var current = await _db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.Id == decisionId && d.TeamId == teamId)
            .Select(d => (SupervisorDecisionStatus?)d.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new SupervisorDecisionTransitionException($"SupervisorDecision {decisionId} not found.");

        if (!SupervisorDecisionStateMachine.IsLegalTransition(current, status))
            throw new SupervisorDecisionTransitionException($"Illegal SupervisorDecision transition {current} → {status} (decision {decisionId}).");

        var now = DateTimeOffset.UtcNow;

        var flipped = await _db.SupervisorDecisionRecord
            .Where(d => d.Id == decisionId && d.TeamId == teamId && d.Status == current)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, status)
                .SetProperty(d => d.OutcomeJson, outcomeJson)
                .SetProperty(d => d.Error, error)
                .SetProperty(d => d.LastModifiedDate, now), cancellationToken)
            .ConfigureAwait(false);

        if (flipped == 0)
            throw new SupervisorDecisionTransitionException($"SupervisorDecision {decisionId} was no longer {current} at terminal record — a concurrent transition won the race.");

        _logger.LogInformation("Supervisor decision recorded terminal. DecisionId={DecisionId} Status={Status}", decisionId, status);
    }

    public async Task<IReadOnlyList<SupervisorDecisionRecord>> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == supervisorRunId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<SupervisorPriorDecision>> GetTerminalDecisionsAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await GetForRunAsync(supervisorRunId, teamId, cancellationToken).ConfigureAwait(false);

        return rows.Where(r => SupervisorDecisionStateMachine.IsTerminal(r.Status)).Select(ToPriorDecision).ToList();
    }

    private static SupervisorPriorDecision ToPriorDecision(SupervisorDecisionRecord row) => new()
    {
        Id = row.Id,
        Sequence = row.Sequence,
        DecisionKind = row.DecisionKind,
        Status = row.Status,
        PayloadJson = row.PayloadJson,
        OutcomeJson = row.OutcomeJson,
        Error = row.Error,
    };

    public async Task UpdateOutcomeAsync(Guid decisionId, Guid teamId, string foldedOutcomeJson, CancellationToken cancellationToken)
    {
        // A targeted outcome enrichment (NOT a status CAS — the row stays terminal). Only rewrite when the bytes
        // actually differ, so a re-fold on every rehydrate is a no-op (idempotent, allocation-light). Team-scoped.
        var affected = await _db.SupervisorDecisionRecord
            .Where(d => d.Id == decisionId && d.TeamId == teamId && d.OutcomeJson != foldedOutcomeJson)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.OutcomeJson, foldedOutcomeJson)
                .SetProperty(d => d.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (affected > 0) _logger.LogInformation("Supervisor folded a settled outcome enrichment into decision {DecisionId}", decisionId);
    }

    public async Task<int> ExpireStalePendingAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        // Candidate set (bounded): stale undecided rows still Pending past the retention window. Take ExpiryBatchSize + 1
        // so a full page tells us the sweep was capped (logged below — no silent truncation). Team-agnostic (no actor).
        var candidates = await _db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.Status == SupervisorDecisionStatus.Pending && d.CreatedDate < olderThan)
            .OrderBy(d => d.CreatedDate)
            .Take(ExpiryBatchSize + 1)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var capped = candidates.Count > ExpiryBatchSize;

        var expired = 0;

        foreach (var id in candidates.Take(ExpiryBatchSize))
            if (await TryExpireOneAsync(id, cancellationToken).ConfigureAwait(false))
                expired++;

        if (expired > 0) _logger.LogInformation("Supervisor decision reaper expired {Expired} stale Pending decision(s)", expired);

        if (capped) _logger.LogWarning("Supervisor decision reaper hit the per-sweep cap of {Cap} — a backlog remains for the next tick", ExpiryBatchSize);

        return expired;
    }

    // Per-row single-winner CAS Pending → Expired (mirrors RecordTerminalAsync's ExecuteUpdate discipline). The Status ==
    // Pending guard means affected == 1 only if this sweep won the row; 0 means a concurrent sweep / claim already moved
    // it (e.g. into Running) — skip it cleanly, an executing decision is never expired out from under its claimer.
    private async Task<bool> TryExpireOneAsync(Guid decisionId, CancellationToken cancellationToken)
    {
        var affected = await _db.SupervisorDecisionRecord
            .Where(d => d.Id == decisionId && d.Status == SupervisorDecisionStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, SupervisorDecisionStatus.Expired)
                .SetProperty(d => d.Error, StalePendingError)
                .SetProperty(d => d.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    private async Task<SupervisorDecisionClaim> ReadExistingClaimAsync(Guid supervisorRunId, string idempotencyKey, CancellationToken cancellationToken)
    {
        // Keyed on the unique index (supervisor_run_id, idempotency_key) — which is team-AGNOSTIC, so the winner's row is
        // ALWAYS found here regardless of which team won. Filtering by TeamId too would let a cross-team race on the same
        // (run, key) find nothing and throw — the unique index already makes (run, key) globally unique (mirrors
        // ToolCallLedgerService.ReadExistingClaimAsync).
        var existing = await _db.SupervisorDecisionRecord.AsNoTracking()
            .SingleOrDefaultAsync(d => d.SupervisorRunId == supervisorRunId && d.IdempotencyKey == idempotencyKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"SupervisorDecision row for run {supervisorRunId} key was missing after a unique-violation race.");

        return SupervisorDecisionStateMachine.IsTerminal(existing.Status)
            ? SupervisorDecisionClaim.Duplicate(existing.Id, existing.Status, existing.OutcomeJson, existing.Error)
            : SupervisorDecisionClaim.InFlight(existing.Id);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}

/// <summary>A SupervisorDecisionRecord row was asked to make a transition its lifecycle doesn't allow, or a status-guarded CAS lost the race. Mirrors <c>ToolCallLedgerTransitionException</c>.</summary>
public sealed class SupervisorDecisionTransitionException : Exception
{
    public SupervisorDecisionTransitionException(string message) : base(message) { }
}
