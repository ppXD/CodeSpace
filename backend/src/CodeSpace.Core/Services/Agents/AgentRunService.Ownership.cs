using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CodeSpace.Core.Services.Agents;

public sealed partial class AgentRunService
{
    public async Task<AgentRunOwnerToken?> ClaimOwnershipAsync(Guid runId, CancellationToken cancellationToken)
    {
        EnsureIndependentOwnershipTransaction();
        var run = await GetAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run.Status != AgentRunStatus.Queued) return null;
        await _authority.EnsureAgentActionAsync(runId, run.TeamId, cancellationToken).ConfigureAwait(false);
        var ownerId = Guid.NewGuid();
        var duration = AgentRunLiveness.LeaseDuration;
        const string sql = "WITH locked AS MATERIALIZED (SELECT id FROM agent_run WHERE id = {0} FOR UPDATE) UPDATE agent_run AS target SET status = 'Running', owner_id = {1}, reattach_reservation_id = NULL, fence_epoch = target.fence_epoch + 1, started_at = clock_timestamp(), heartbeat_at = clock_timestamp(), lease_expires_at = clock_timestamp() + {2} FROM locked WHERE target.id = locked.id AND target.status = 'Queued' RETURNING target.fence_epoch AS \"Value\"";
        var epoch = await WriteOwnershipAsync(new(runId, ownerId, null), sql, [runId, ownerId, duration], cancellationToken).ConfigureAwait(false);
        return epoch is { } value ? new(runId, ownerId, value) : null;
    }

    public Task<AgentRunReattachReservation?> ReserveReattachAsync(Guid runId, CancellationToken cancellationToken) => ReserveReattachCoreAsync(runId, false, null, cancellationToken);

    public Task<AgentRunReattachReservation?> ReserveReattachAsync(AgentRunReconciliationCandidate candidate, CancellationToken cancellationToken) => ReserveReattachCoreAsync(candidate.RunId, false, candidate, cancellationToken);

    private async Task<AgentRunReattachReservation?> ReserveReattachCoreAsync(Guid runId, bool legacyOnly, AgentRunReconciliationCandidate? candidate, CancellationToken cancellationToken)
    {
        EnsureIndependentOwnershipTransaction();
        var reservationId = Guid.NewGuid();
        var duration = AgentRunLiveness.LeaseDuration;
        const string sql = "WITH locked AS MATERIALIZED (SELECT id FROM agent_run WHERE id = {0} FOR UPDATE) UPDATE agent_run AS target SET owner_id = NULL, reattach_reservation_id = {1}, fence_epoch = target.fence_epoch + 1, reattach_attempts = target.reattach_attempts + 1, heartbeat_at = clock_timestamp(), lease_expires_at = clock_timestamp() + {2} FROM locked WHERE target.id = locked.id AND target.status = 'Running' AND (NOT {3} OR (target.owner_id IS NULL AND target.reattach_reservation_id IS NULL)) AND (NOT {4} OR (target.owner_id IS NOT DISTINCT FROM {5}::uuid AND target.reattach_reservation_id IS NOT DISTINCT FROM {6}::uuid AND target.fence_epoch = {7} AND target.runner_handle IS NOT DISTINCT FROM CAST({8} AS jsonb) AND target.reattach_attempts = {9})) AND (target.lease_expires_at <= clock_timestamp() OR (target.lease_expires_at IS NULL AND COALESCE(target.heartbeat_at, target.started_at, target.created_date) <= clock_timestamp() - {2})) RETURNING target.fence_epoch AS \"Value\"";
        var epoch = await WriteOwnershipAsync(new(runId, null, reservationId), sql, [runId, reservationId, duration, legacyOnly, candidate != null, (object?)candidate?.OwnerId ?? DBNull.Value, (object?)candidate?.ReservationId ?? DBNull.Value, candidate?.Epoch ?? 0, (object?)candidate?.RunnerHandleJson ?? DBNull.Value, candidate?.ReattachAttempts ?? 0], cancellationToken).ConfigureAwait(false);
        return epoch is { } value ? new(runId, reservationId, value) : null;
    }

    public async Task<AgentRunOwnerToken?> ActivateReattachAsync(AgentRunReattachReservation reservation, CancellationToken cancellationToken)
    {
        EnsureIndependentOwnershipTransaction();
        if (reservation.RunId == Guid.Empty || reservation.ReservationId == Guid.Empty || reservation.Epoch <= 0) return null;
        var ownerId = Guid.NewGuid();
        var duration = AgentRunLiveness.LeaseDuration;
        const string sql = "WITH locked AS MATERIALIZED (SELECT id FROM agent_run WHERE id = {0} FOR UPDATE) UPDATE agent_run AS target SET owner_id = {1}, heartbeat_at = clock_timestamp(), lease_expires_at = clock_timestamp() + {2} FROM locked WHERE target.id = locked.id AND target.status = 'Running' AND target.owner_id IS NULL AND target.reattach_reservation_id = {3} AND target.fence_epoch = {4} AND target.lease_expires_at > clock_timestamp() RETURNING target.fence_epoch AS \"Value\"";
        var epoch = await WriteOwnershipAsync(new(reservation.RunId, ownerId, reservation.ReservationId), sql, [reservation.RunId, ownerId, duration, reservation.ReservationId, reservation.Epoch], cancellationToken).ConfigureAwait(false);
        return epoch is { } value ? new(reservation.RunId, ownerId, value) : null;
    }

    public async Task AssertOwnershipAsync(AgentRunOwnerToken owner, CancellationToken cancellationToken)
    {
        if (owner.OwnerId == Guid.Empty || owner.Epoch <= 0) throw new AgentRunOwnershipLostException(owner.RunId);
        const string sql = "WITH locked AS MATERIALIZED (SELECT owner_id, fence_epoch, status, lease_expires_at FROM agent_run WHERE id = {0} FOR UPDATE) SELECT count(*)::integer AS \"Value\" FROM locked WHERE owner_id = {1} AND fence_epoch = {2} AND status = 'Running' AND lease_expires_at > clock_timestamp()";
        var matches = await _db.Database.SqlQueryRaw<int>(sql, owner.RunId, owner.OwnerId, owner.Epoch).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (matches.Single() != 1) throw new AgentRunOwnershipLostException(owner.RunId);
    }

    public async Task HeartbeatAsync(AgentRunOwnerToken owner, CancellationToken cancellationToken)
    {
        var duration = AgentRunLiveness.LeaseDuration;
        var changed = await _db.Database.ExecuteSqlInterpolatedAsync($"WITH locked AS MATERIALIZED (SELECT id FROM agent_run WHERE id = {owner.RunId} FOR UPDATE) UPDATE agent_run AS target SET heartbeat_at = clock_timestamp(), lease_expires_at = clock_timestamp() + {duration} FROM locked WHERE target.id = locked.id AND target.status = 'Running' AND target.owner_id = {owner.OwnerId} AND target.fence_epoch = {owner.Epoch} AND target.lease_expires_at > clock_timestamp()", cancellationToken).ConfigureAwait(false);
        if (changed == 1) return;
        // A final heartbeat racing this owner's successful terminal write must stop renewing, not cancel its completion notifier.
        if (await _db.AgentRun.AsNoTracking().AnyAsync(r => r.Id == owner.RunId && r.OwnerId == owner.OwnerId && r.FenceEpoch == owner.Epoch && r.Status != AgentRunStatus.Running && r.Status != AgentRunStatus.Queued, cancellationToken).ConfigureAwait(false)) return;
        throw new AgentRunOwnershipLostException(owner.RunId);
    }

    public async Task SetRunnerHandleAsync(AgentRunOwnerToken owner, string handleJson, CancellationToken cancellationToken)
    {
        var changed = await _db.Database.ExecuteSqlInterpolatedAsync($"WITH locked AS MATERIALIZED (SELECT id FROM agent_run WHERE id = {owner.RunId} FOR UPDATE) UPDATE agent_run AS target SET runner_handle = CAST({handleJson} AS jsonb), spool_cleanup_attempts = 0, spool_cleanup_last_attempt_at = NULL, spool_cleanup_next_attempt_at = NULL, spool_cleanup_last_error_code = NULL FROM locked WHERE target.id = locked.id AND target.status = 'Running' AND target.owner_id = {owner.OwnerId} AND target.fence_epoch = {owner.Epoch} AND target.lease_expires_at > clock_timestamp()", cancellationToken).ConfigureAwait(false);
        if (changed != 1) throw new AgentRunOwnershipLostException(owner.RunId);
    }

    public async Task SetSandboxConfinementAsync(AgentRunOwnerToken owner, string confinementJson, CancellationToken cancellationToken)
    {
        var changed = await _db.Database.ExecuteSqlInterpolatedAsync($"WITH locked AS MATERIALIZED (SELECT id FROM agent_run WHERE id = {owner.RunId} FOR UPDATE) UPDATE agent_run AS target SET sandbox_confinement = CAST({confinementJson} AS jsonb) FROM locked WHERE target.id = locked.id AND target.status = 'Running' AND target.owner_id = {owner.OwnerId} AND target.fence_epoch = {owner.Epoch} AND target.lease_expires_at > clock_timestamp()", cancellationToken).ConfigureAwait(false);
        if (changed != 1) throw new AgentRunOwnershipLostException(owner.RunId);
    }

    private void EnsureIndependentOwnershipTransaction()
    {
        if (_db.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null) throw new InvalidOperationException("Execution ownership must commit independently before a worker can perform external actions.");
    }

    private async Task<long?> WriteOwnershipAsync(OwnershipReceipt receipt, string sql, object[] parameters, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var epochs = await _db.Database.SqlQueryRaw<long>(sql, parameters).ToListAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return epochs.Count == 1 ? epochs[0] : null;
        }
        catch (Exception original)
        {
            // Only this call's pre-minted identity can establish a committed claim. Unknown stays an exception;
            // a later job cannot use a run id to recover another call's token.
            try
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var connection = (NpgsqlConnection)((ICloneable)_db.Database.GetDbConnection()).Clone();
                await connection.OpenAsync(recovery.Token).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "WITH locked AS MATERIALIZED (SELECT owner_id, reattach_reservation_id, fence_epoch, status, lease_expires_at FROM agent_run WHERE id = @id FOR UPDATE) SELECT owner_id, reattach_reservation_id, fence_epoch, status, lease_expires_at > clock_timestamp() FROM locked";
                command.Parameters.AddWithValue("id", receipt.RunId);
                await using var row = await command.ExecuteReaderAsync(recovery.Token).ConfigureAwait(false);
                if (await row.ReadAsync(recovery.Token).ConfigureAwait(false))
                {
                    var ownerId = row.IsDBNull(0) ? (Guid?)null : row.GetGuid(0);
                    var reservationId = row.IsDBNull(1) ? (Guid?)null : row.GetGuid(1);
                    var matches = receipt.OwnerId is { } owner ? ownerId == owner && (receipt.ReservationId is null || reservationId == receipt.ReservationId) : ownerId is null && reservationId == receipt.ReservationId;
                    if (matches && row.GetString(3) == nameof(AgentRunStatus.Running) && !row.IsDBNull(4) && row.GetBoolean(4)) return row.GetInt64(2);
                }
            }
            catch (Exception recoveryFailure) { _logger.LogWarning(recoveryFailure, "Agent run {RunId} ownership acknowledgement remains unknown after {FailureType}; no execution is authorized", receipt.RunId, original.GetType().Name); }
            throw;
        }
    }

    private async Task EnsureLegacyWriterAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (await _db.AgentRun.AsNoTracking().AnyAsync(r => r.Id == runId && (r.OwnerId != null || r.ReattachReservationId != null), cancellationToken).ConfigureAwait(false)) throw new AgentRunOwnershipLostException(runId);
    }

    private sealed record OwnershipReceipt(Guid RunId, Guid? OwnerId, Guid? ReservationId);
}
