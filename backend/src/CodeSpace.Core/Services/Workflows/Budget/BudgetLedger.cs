using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Budget;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Budget;

/// <summary>The budget reservation states (W-hard). Stored as text; live = Reserved|InFlight|Indeterminate — the states that hold cap headroom.</summary>
public static class BudgetReservationStates
{
    public const string Reserved = "Reserved";
    public const string InFlight = "InFlight";
    public const string Settled = "Settled";
    public const string Released = "Released";
    public const string Expired = "Expired";
    public const string Indeterminate = "Indeterminate";
    public const string Reconciled = "Reconciled";

    public static readonly IReadOnlyList<string> Live = new[] { Reserved, InFlight, Indeterminate };
}

/// <summary>
/// P15-5a: the kind PREFIX an <c>Unbudgeted</c> <c>LlmCallScope</c> records under (<c>$"{UnbudgetedPrefix}{scope.Kind}"</c>)
/// — an intentionally uncapped observability row, never an admission claim. Excluded from every committed-sum query
/// below so a plane with no launch cap (operator calibration, a benchmark cell's own cap) can never eat into a
/// DIFFERENT plane's real cap on the same run.
/// </summary>
public static class BudgetKinds
{
    public const string UnbudgetedPrefix = "unbudgeted:";

    /// <summary>
    /// The supervisor's per-attempt admission grain (<c>RealSupervisorActionExecutor</c> mints one per staged
    /// agent, <c>BudgetSettlementService</c> settles it from the decision tape and reconciles its orphans). Named
    /// here rather than repeated as a literal at each of those sites: a rename that reached only some of them
    /// would silently split the ledger into two kinds — reservations nothing settles, and a sweep that finds
    /// nothing to settle. Pinned by test, since the string is durable state in every existing row.
    /// </summary>
    public const string AgentAttempt = "agent-attempt";

    /// <summary>
    /// The QUICK lane's admission grain: one row per agent run, minted by <c>AgentRunExecutor</c> before the coding
    /// CLI starts and settled at its terminal fold. Named "monitored" deliberately — a CLI has no wire ceiling, so
    /// this reservation buys ADMISSION (refuse a launch whose run or team cap is already spent) and TEAM-CAP
    /// accounting, never enforcement of the amount it claims. Pinned by test: the string is durable state in every
    /// existing row, and a rename that reached only the reserve site or only the settle site would split the ledger
    /// into reservations nothing settles.
    /// </summary>
    public const string AgentRunMonitored = "agent-run-monitored";
}

public sealed record BudgetAdmission(bool Admitted, Guid? ReservationId, decimal CommittedUsd, decimal? CapUsd, string? Reason)
{
    /// <summary>A lookup of an existing logical claim, never permission for a second physical provider request.</summary>
    public bool IsReplay { get; init; }
    public string? ReservationState { get; init; }

    /// <summary>WHICH cap refused this admission — null on anything but a refusal. The <see cref="Reason"/> names it in prose; this is the same fact typed, for a caller that must branch on it rather than read it.</summary>
    public BudgetCapGrain? RefusedGrain { get; init; }
}

/// <summary>
/// One still-live claim an agent run holds, as a refusal needs to describe it: which invocation
/// (<see cref="ScopeKey"/>), how much of the cap it is holding, and when it stops being credible as live. Its spend
/// is unknown by construction — a live claim has not settled — so a caller may say what is HELD, never what was spent.
/// </summary>
public sealed record AgentRunClaimHold(string ScopeKey, decimal ReservedUsd, DateTimeOffset? ExpiresAt);

public interface IBudgetLedger
{
    /// <summary>Atomically reserve an estimate under BOTH the run's cap and the team's (P15-5b-ii), serialized per run and per team. This bounds admission commitments; it bounds the eventual provider bill only when the estimate is a trustworthy upper bound. Idempotent for the same run, team and reservation identity. A null <paramref name="capUsd"/> records an unbounded observability claim that never refuses (an <c>Unbudgeted</c> plane) — never a real admission gate, and never admitted against the team cap either.</summary>
    Task<BudgetAdmission> ReserveAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal estimateUsd, decimal? capUsd, string priceVersion, Guid? parentReservationId, DateTimeOffset? expiresAt, CancellationToken cancellationToken);

    /// <summary>Record known actual spend exactly. Null actual keeps the reserved claim and records Indeterminate, never an invented bill. A later known receipt supersedes uncertain or released bookkeeping; a confirmed settlement is idempotent.</summary>
    Task SettleAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal? actualUsd, CancellationToken cancellationToken);

    /// <summary>
    /// Close every still-live <see cref="BudgetKinds.AgentRunMonitored"/> claim ONE agent run holds — every attempt's
    /// and every revise round's, found by scope-key PREFIX, so a caller closes them all without knowing how many
    /// there were. The query lives here rather than at each terminal writer: the executor's fold, an operator cancel
    /// and the reconciler's abandon all reach a terminal by different routes, and a claim left live by any of them
    /// rides to its deadline and is counted against the team's window at its full RESERVE.
    ///
    /// <para><paramref name="actualUsd"/> is the observed spend when something priced it, else null — which the settle
    /// records as Indeterminate at the reserve, the pessimism an unknown bill already gets. Never an invented figure,
    /// and never applied to more than one row (see the implementation).</para>
    /// </summary>
    /// <param name="agentRunId">
    /// An AGENT RUN's id, NOT a workflow run's — the only agent-run-grain method on this interface, because the
    /// scope keys it closes are minted per agent run (<c>{agentRunId:N}/e{epoch}[/r{round}]</c>) and span every
    /// workflow-run-keyed row those attempts wrote. Passing a workflow run id here matches nothing.
    /// </param>
    Task CloseAgentRunClaimsByPrefixAsync(Guid agentRunId, Guid teamId, decimal? actualUsd, CancellationToken cancellationToken);

    /// <summary>
    /// The still-live monitored claims of one agent run whose scope key is NOT <paramref name="exceptScopeKey"/> —
    /// what an EARLIER attempt of the same run is still holding when a later one is being refused. Diagnostic only:
    /// it is read on the refusal path so the operator is told a previous attempt holds the cap with unknown spend,
    /// rather than "cost cap reached" for money nothing has spent. The caller owns the scope-key grammar and the words.
    /// </summary>
    Task<IReadOnlyList<AgentRunClaimHold>> LiveAgentRunClaimsAsync(Guid agentRunId, Guid teamId, string exceptScopeKey, CancellationToken cancellationToken);

    /// <summary>Release an unused reservation (the attempt never ran) — its headroom returns to the cap. Idempotent; a settled reservation is never released.</summary>
    Task ReleaseAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, CancellationToken cancellationToken);

    /// <summary>Expire live reservations past their deadline (the orphan-recovery sweep): Reserved/InFlight → Indeterminate when a settlement may still surface, pessimistically HOLDING their headroom until reconciled. Returns how many moved.</summary>
    Task<int> ExpireOverdueAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>The run's committed total: settled + live reserved — what the invariant compares against the cap.</summary>
    Task<decimal> CommittedUsdAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>The TEAM's committed total across every run since <paramref name="since"/> — the same settled + live arithmetic, over the team cap's rolling window instead of one run. Rows created before the window are spend the cap has already forgiven.</summary>
    Task<decimal> CommittedTeamUsdAsync(Guid teamId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// W-hard slice 2: pessimistically reconcile a KIND PREFIX's dangling reservations — INDETERMINATE rows (the
    /// expiry sweep's output), and still-live rows whose run is terminal without an in-band settlement.
    /// Each lands <see cref="BudgetReservationStates.Reconciled"/> while its actual remains null and the reserve
    /// continues to hold headroom. This closes recovery bookkeeping without claiming a confirmed bill. Cap math is unchanged in the only direction that matters (headroom is never silently freed);
    /// what this closes is the FOREVER-LIVE orphan an in-flight teardown leaves behind.
    /// </summary>
    Task<int> ReconcileDanglingAsync(string kindPrefix, int batchSize, CancellationToken cancellationToken);
}

/// <summary>
/// Serializes admission, settlement and release per run. Unknown cost retains its reserved estimate without
/// presenting that estimate as actual spend. Conditional writes prevent stale tracked entities and recovery
/// sweeps from overwriting a provider receipt. Reservations are estimates unless their caller proves an upper bound.
/// </summary>
public sealed partial class BudgetLedger : IBudgetLedger, IPhysicalLlmInvocationLedger, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ITeamCostCapResolver _teamCaps;

    public BudgetLedger(CodeSpaceDbContext db, ITeamCostCapResolver teamCaps)
    {
        _db = db;
        _teamCaps = teamCaps;
    }

    public async Task<BudgetAdmission> ReserveAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal estimateUsd, decimal? capUsd, string priceVersion, Guid? parentReservationId, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimateUsd);
        if (capUsd is < 0) throw new ArgumentOutOfRangeException(nameof(capUsd));
        await using var tx = await ScopedTransaction.OwnOrJoinAsync(_db.Database, cancellationToken).ConfigureAwait(false);

        await TakeAdmissionLocksAsync(workflowRunId, teamId, capUsd, cancellationToken).ConfigureAwait(false);

        var existing = await _db.BudgetReservation.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId && r.Kind == kind && r.ScopeKey == scopeKey)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            var sameTeam = existing.TeamId == teamId;
            var matches = sameTeam && existing.CapUsd == capUsd && existing.ReservedUsd == estimateUsd && existing.PriceVersion == priceVersion && existing.ParentReservationId == parentReservationId && DatabaseTimestamp(existing.ExpiresAt) == DatabaseTimestamp(expiresAt);
            var total = await CommittedInTxAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BudgetAdmission(matches, sameTeam ? existing.Id : null, total, capUsd, matches ? "already-reserved" : "reservation-intent-mismatch") { IsReplay = true, ReservationState = sameTeam ? existing.State : null };
        }

        var committed = await CommittedInTxAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        if (await RefusalAsync(teamId, estimateUsd, capUsd, committed, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return refusal;
        }

        var reservation = new BudgetReservation
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = workflowRunId,
            ParentReservationId = parentReservationId,
            Kind = kind,
            ScopeKey = scopeKey,
            State = BudgetReservationStates.Reserved,
            ReservedUsd = estimateUsd,
            CapUsd = capUsd,
            PriceVersion = priceVersion,
            ExpiresAt = expiresAt,
        };

        _db.BudgetReservation.Add(reservation);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new BudgetAdmission(true, reservation.Id, committed + estimateUsd, capUsd, null) { ReservationState = BudgetReservationStates.Reserved };
    }

    public async Task SettleAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal? actualUsd, CancellationToken cancellationToken)
    {
        if (actualUsd is < 0) throw new ArgumentOutOfRangeException(nameof(actualUsd));
        await using var tx = await ScopedTransaction.OwnOrJoinAsync(_db.Database, cancellationToken).ConfigureAwait(false);
        await TakeRunLockAsync(workflowRunId, cancellationToken).ConfigureAwait(false);
        var query = _db.BudgetReservation.Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && r.Kind == kind && r.ScopeKey == scopeKey);

        if (actualUsd is { } actual)
        {
            // A real receipt is stronger evidence than an earlier claim that an attempt never ran. In particular,
            // settle-after-release must record money already spent, while release-after-settle must be a no-op.
            await query.Where(r => r.State != BudgetReservationStates.Settled)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.State, BudgetReservationStates.Settled).SetProperty(r => r.SettledUsd, actual).SetProperty(r => r.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await query.Where(r => r.State != BudgetReservationStates.Settled)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.State, r => r.State == BudgetReservationStates.Reconciled ? BudgetReservationStates.Reconciled : BudgetReservationStates.Indeterminate).SetProperty(r => r.SettledUsd, (decimal?)null).SetProperty(r => r.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseAgentRunClaimsByPrefixAsync(Guid agentRunId, Guid teamId, decimal? actualUsd, CancellationToken cancellationToken)
    {
        // Ordered by id so a run's claims always close in the same sequence: a failure part-way leaves a PARTIAL
        // close (the rest stay live for the expiry sweep, which is the same pessimism they would have had anyway),
        // and a deterministic order makes the survivors the same set on every retry instead of an arbitrary one.
        var live = await OpenAgentRunClaimsAsync(agentRunId, teamId, cancellationToken).ConfigureAwait(false);

        // An observed figure is ONE invocation's bill. When a run left several claims live, no row can be given it
        // without inventing the others', so they all settle Indeterminate at their own reserve instead.
        var observed = live.Count == 1 ? actualUsd : null;

        foreach (var row in live)
            await SettleAsync(row.WorkflowRunId, teamId, row.Kind, row.ScopeKey, observed, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AgentRunClaimHold>> LiveAgentRunClaimsAsync(Guid agentRunId, Guid teamId, string exceptScopeKey, CancellationToken cancellationToken) =>
        (await OpenAgentRunClaimsAsync(agentRunId, teamId, cancellationToken).ConfigureAwait(false))
            .Where(row => row.ScopeKey != exceptScopeKey && BudgetReservationStates.Live.Contains(row.State))
            .Select(row => new AgentRunClaimHold(row.ScopeKey, row.ReservedUsd, row.ExpiresAt))
            .ToList();

    /// <summary>Every not-yet-Settled monitored row of one agent run, across its attempts and rounds. <c>Settled</c> is the one state nothing here may touch or count — a confirmed receipt.</summary>
    private async Task<IReadOnlyList<OpenAgentRunClaim>> OpenAgentRunClaimsAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var prefix = agentRunId.ToString("N");
        var unbudgeted = $"{BudgetKinds.UnbudgetedPrefix}{BudgetKinds.AgentRunMonitored}";

        return await _db.BudgetReservation.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.ScopeKey.StartsWith(prefix) && (r.Kind == BudgetKinds.AgentRunMonitored || r.Kind == unbudgeted) && r.State != BudgetReservationStates.Settled)
            .OrderBy(r => r.Id)
            .Select(r => new OpenAgentRunClaim(r.WorkflowRunId, r.Kind, r.ScopeKey, r.State, r.ReservedUsd, r.ExpiresAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record OpenAgentRunClaim(Guid WorkflowRunId, string Kind, string ScopeKey, string State, decimal ReservedUsd, DateTimeOffset? ExpiresAt);

    public async Task ReleaseAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, CancellationToken cancellationToken)
    {
        await using var tx = await ScopedTransaction.OwnOrJoinAsync(_db.Database, cancellationToken).ConfigureAwait(false);
        await TakeRunLockAsync(workflowRunId, cancellationToken).ConfigureAwait(false);
        await _db.BudgetReservation.Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && r.Kind == kind && r.ScopeKey == scopeKey && (r.State == BudgetReservationStates.Reserved || r.State == BudgetReservationStates.InFlight))
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.State, BudgetReservationStates.Released).SetProperty(r => r.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ExpireOverdueAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var overdue = _db.BudgetReservation.Where(r => (r.State == BudgetReservationStates.Reserved || r.State == BudgetReservationStates.InFlight) && r.ExpiresAt != null && r.ExpiresAt < now);
        var ids = await overdue.OrderBy(r => r.ExpiresAt).Select(r => r.Id).Take(batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);

        // Re-evaluate the eligible state in the UPDATE, after any competing row lock. Never write a stale EF entity.
        return await overdue.Where(r => ids.Contains(r.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.State, BudgetReservationStates.Indeterminate).SetProperty(r => r.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    public async Task<decimal> CommittedUsdAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.BudgetReservation.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && r.State != BudgetReservationStates.Released && r.State != BudgetReservationStates.Expired && !r.Kind.StartsWith(BudgetKinds.UnbudgetedPrefix))
            .SumAsync(r => r.SettledUsd ?? r.ReservedUsd, cancellationToken).ConfigureAwait(false);

    private async Task<decimal> CommittedInTxAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.BudgetReservation
            .Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && r.State != BudgetReservationStates.Released && r.State != BudgetReservationStates.Expired && !r.Kind.StartsWith(BudgetKinds.UnbudgetedPrefix))
            .SumAsync(r => r.SettledUsd ?? r.ReservedUsd, cancellationToken).ConfigureAwait(false);

    public async Task<decimal> CommittedTeamUsdAsync(Guid teamId, DateTimeOffset since, CancellationToken cancellationToken) =>
        await _db.BudgetReservation.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.CreatedDate >= since && r.State != BudgetReservationStates.Released && r.State != BudgetReservationStates.Expired && !r.Kind.StartsWith(BudgetKinds.UnbudgetedPrefix))
            .SumAsync(r => r.SettledUsd ?? r.ReservedUsd, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The FIRST cap this admission passes, or null when it clears every grain that applies. Run before team,
    /// because the run cap is the tighter and more actionable of the two: a launch over its own cap should say so
    /// rather than blaming the team's month.
    /// </summary>
    private async Task<BudgetAdmission?> RefusalAsync(Guid teamId, decimal estimateUsd, decimal? capUsd, decimal committed, CancellationToken cancellationToken)
    {
        // An Unbudgeted observability claim (see BudgetKinds.UnbudgetedPrefix) declares no cap at all, and is
        // excluded from every committed sum including the team's — so it can neither refuse nor be refused.
        if (capUsd is not { } cap) return null;

        if (committed + estimateUsd > cap)
            return new BudgetAdmission(false, null, committed, capUsd, BudgetCapRefusal.Reason(BudgetCapGrain.Run, committed + estimateUsd, cap)) { RefusedGrain = BudgetCapGrain.Run };

        return await TeamRefusalAsync(teamId, estimateUsd, capUsd, committed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// P15-5b-ii: the same invariant one grain up. Read inside the admission transaction, behind the team lock
    /// taken above, so two connections cannot each see the other's headroom as free and jointly overshoot.
    /// <see cref="BudgetAdmission.CommittedUsd"/> stays the RUN's total on a team refusal — it is the run's own
    /// accounting figure, and the team's numbers belong to the reason (and to the Room's budget block).
    /// </summary>
    private async Task<BudgetAdmission?> TeamRefusalAsync(Guid teamId, decimal estimateUsd, decimal? capUsd, decimal committed, CancellationToken cancellationToken)
    {
        if (await _teamCaps.ResolveAsync(teamId, cancellationToken).ConfigureAwait(false) is not { } teamCap) return null;

        var teamCommitted = await CommittedTeamUsdAsync(teamId, teamCap.WindowStart(DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        if (teamCommitted + estimateUsd <= teamCap.CapUsd) return null;

        var reason = BudgetCapRefusal.Reason(teamCap.Grain, teamCommitted + estimateUsd, teamCap.CapUsd, teamCap.Window);

        return new BudgetAdmission(false, null, committed, capUsd, reason) { RefusedGrain = teamCap.Grain };
    }

    public async Task<int> ReconcileDanglingAsync(string kindPrefix, int batchSize, CancellationToken cancellationToken)
    {
        var terminal = new[] { WorkflowRunStatus.Success, WorkflowRunStatus.Failure, WorkflowRunStatus.Cancelled };

        var dangling = _db.BudgetReservation.Where(r => r.Kind.StartsWith(kindPrefix)
            && (r.State == BudgetReservationStates.Indeterminate
                || ((r.State == BudgetReservationStates.Reserved || r.State == BudgetReservationStates.InFlight)
                    && _db.WorkflowRun.Any(w => w.Id == r.WorkflowRunId && terminal.Contains(w.Status)))));
        var ids = await dangling.OrderBy(r => r.CreatedDate).Select(r => r.Id).Take(batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);

        // Reconciled closes recovery bookkeeping, not billing uncertainty. The nullable actual stays unknown;
        // committed arithmetic still counts ReservedUsd and a late actual receipt remains admissible.
        return await dangling.Where(r => ids.Contains(r.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.State, BudgetReservationStates.Reconciled).SetProperty(r => r.SettledUsd, (decimal?)null).SetProperty(r => r.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    // Npgsql/PostgreSQL timestamps store microseconds. Normalize the request to that same precision before
    // comparing a replay; the original caller may retain a sub-microsecond .NET tick that cannot round-trip.
    private static DateTimeOffset? DatabaseTimestamp(DateTimeOffset? value) => value is { } time ? new DateTimeOffset(time.UtcTicks - time.UtcTicks % 10, TimeSpan.Zero) : null;

    /// <summary>Serialize admission, settlement and release per run — pg_advisory_xact_lock releases with the transaction.</summary>
    private async Task TakeRunLockAsync(Guid workflowRunId, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({workflowRunId.ToString()}, 42))", cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// LOCK ORDER — RUN FIRST, THEN TEAM. Fixed, and the only order anything in this class takes: settlement and
    /// release take the run lock alone, so no path can ever hold the team lock while waiting for a run's. Reversing
    /// it here would let one admission hold run A while waiting on the team, and another hold the team while
    /// waiting on run A.
    ///
    /// <para>The team lock is skipped for an <c>Unbudgeted</c> claim (null cap): it is admitted against nothing, so
    /// taking a team-wide lock for it would serialize every real admission in the team behind an observability row.</para>
    ///
    /// <para>The proof holds WITHIN ONE TRANSACTION, and since these methods join an ambient one
    /// (<c>ScopedTransaction.OwnOrJoinAsync</c>) that transaction is the whole command's: an advisory xact lock is
    /// released by the transaction, not by the method. So a single command that reserves for TWO runs of one team
    /// re-derives the hazard — the first reserve still holds team-T when the second waits on run-B, while another
    /// command holds run-B and waits on team-T. No caller does that today (a reserve is per-run, and the sweeps that
    /// touch many runs settle and release, which take the run lock alone); a command that wants to must reserve each
    /// run in its own transaction.</para>
    /// </summary>
    private async Task TakeAdmissionLocksAsync(Guid workflowRunId, Guid teamId, decimal? capUsd, CancellationToken cancellationToken)
    {
        await TakeRunLockAsync(workflowRunId, cancellationToken).ConfigureAwait(false);

        if (capUsd is null) return;

        await TakeTeamLockAsync(teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serialize admission across the team's runs — a DIFFERENT hash seed from the run lock so a team id and a run id can never collide onto one key.</summary>
    private async Task TakeTeamLockAsync(Guid teamId, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({teamId.ToString()}, 43))", cancellationToken).ConfigureAwait(false);
}
