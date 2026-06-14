using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The fail-closed admission gate <see cref="AgentRunService.CreateAsync"/> consults before persisting a new
/// agent run. Today the only limits on a <c>flow.map</c> fan-out are its per-map maxParallelism + the engine's
/// branch ceiling — neither bounds the TOTAL in-flight agent runs across a team or the whole deployment, so
/// several teams (or several workflows on one big team) fanning out <c>agent.code</c> branches can exhaust
/// runner / Hangfire / model quota. This applies backpressure on concurrent (Queued + Running) agent runs at
/// two levels: per-team (so one team can't starve the others) and global (a deployment-wide guard). The
/// reconciler's re-dispatch / re-attach operate on already-admitted runs and are NOT re-gated, so a run is
/// counted toward the cap exactly once.
///
/// SOFT cap, not a hard ceiling — by design. The gate is count-then-create with no transaction / no lock /
/// no DB constraint: <see cref="EnsureAgentRunAdmittedAsync"/> reads the counts, then <see cref="AgentRunService.CreateAsync"/>
/// INSERTs in a separate SaveChanges. Each branch runs in its own DI scope (its own DbContext +
/// AdmissionController), so under concurrent staging the in-flight total may OVERSHOOT the cap by up to the
/// concurrent-staging width: per single fan-out that width is bounded by maxParallelism (≤ <c>MaxParallelismCeiling</c>
/// = 64), but cluster-wide the global overshoot is bounded only by the number of workflows simultaneously
/// fanning out (N fan-outs ⇒ up to N×width racing reads), i.e. NOT hard-bounded. This is acceptable
/// backpressure — a guard against runaway exhaustion, not a precise quota. Serializing the hot creation path
/// to make the bound hard is intentionally NOT done. If a hard global bound is ever required, the cheapest
/// upgrade is wrapping CreateAsync's count+insert in a serializable transaction keyed by a per-team / global
/// <c>pg_advisory_xact_lock</c> (or a partial-unique constraint) so concurrent counts serialize.
/// </summary>
public interface IAdmissionController
{
    /// <summary>
    /// Throw <see cref="AgentRunAdmissionException"/> if the per-team OR the global in-flight count is already at
    /// its cap for <paramref name="teamId"/>; return cleanly when there's headroom. FAIL-CLOSED: if the count
    /// query itself faults, the exception propagates (a creation under an unknown load is refused, not waved
    /// through). Called pre-persist so a rejected run never touches the table. This is the COUNT half of a
    /// count-then-create soft cap — under concurrent staging the count read here can be stale, so the cap may
    /// overshoot (see the class doc); it is best-effort backpressure, not a serialized hard guarantee.
    /// </summary>
    Task EnsureAgentRunAdmittedAsync(Guid teamId, CancellationToken cancellationToken);
}

public sealed class AdmissionController : IAdmissionController, IScopedDependency
{
    // The cap pair is env-overridable (Rule 8) so an operator can tune it without a redeploy — pinned by a unit
    // test. Sizing them needs the REAL flow.map staging model, not the intuition that parallelism bounds the
    // count: a map's branch ceiling is MapPlan.MaxBranchesCeiling = 10_000, and maxParallelism bounds only how
    // many branches RUN AT ONCE — NOT how many are in flight. Every agent.code branch stages a Queued AgentRun
    // and immediately suspends (releasing the parallelism gate fast), so on the first engine pass a map over N
    // elements stages ~N in-flight (Queued) AgentRuns regardless of its parallelism. So PerTeam is the count a
    // single fan-out is allowed to reach: at the default 50, an ordinary single flow.map over 50+ elements
    // (e.g. "fix each of 60 failing tests with an agent") hits the cap and the 51st-onward branches are
    // admission-rejected (each routes to its error edge / the map's continue-on-error). That is a deliberate
    // backpressure default, not a no-op runaway guard — it WILL bite legitimate wide fan-outs, so it is the
    // PRIMARY operator-tunable knob: a team that intends to fan out wider raises PerTeam (the rejection message
    // names the env var). Global=200 is the deployment-wide ceiling — roughly four PerTeam-saturated teams —
    // past which the runner/model quota is at risk. Both are SOFT caps (see the interface doc for the
    // count-then-create overshoot semantics).
    public const string MaxInflightPerTeamEnvVar = "CODESPACE_AGENT_MAX_INFLIGHT_PER_TEAM";
    public const string MaxInflightGlobalEnvVar = "CODESPACE_AGENT_MAX_INFLIGHT_GLOBAL";

    internal const int DefaultMaxInflightPerTeam = 50;
    internal const int DefaultMaxInflightGlobal = 200;

    // A cap of 1 is the floor (fully-serialized); the ceiling stops a fat-fingered env value from effectively
    // disabling the gate while still allowing a very large deployment to lift it deliberately.
    internal const int MaxInflightCeiling = 100_000;

    private static readonly AgentRunStatus[] InflightStatuses = { AgentRunStatus.Queued, AgentRunStatus.Running };

    private readonly CodeSpaceDbContext _db;

    public AdmissionController(CodeSpaceDbContext db)
    {
        _db = db;
    }

    /// <summary>The per-team in-flight cap: the env override (clamped) wins, else the default. Pure + internal so it's unit-pinned (Rule 8).</summary>
    internal static int MaxInflightPerTeam => ParseCap(Environment.GetEnvironmentVariable(MaxInflightPerTeamEnvVar), DefaultMaxInflightPerTeam);

    /// <summary>The deployment-wide in-flight cap: the env override (clamped) wins, else the default. Pure + internal so it's unit-pinned (Rule 8).</summary>
    internal static int MaxInflightGlobal => ParseCap(Environment.GetEnvironmentVariable(MaxInflightGlobalEnvVar), DefaultMaxInflightGlobal);

    /// <summary>Parse + clamp a cap env value. Unset / unparseable ⇒ the default; out-of-range ⇒ clamped to [1, ceiling]. Mirrors WorkflowEngine.ParseMaxParallelism.</summary>
    internal static int ParseCap(string? raw, int @default) =>
        int.TryParse(raw, out var value) ? Math.Clamp(value, 1, MaxInflightCeiling) : @default;

    public async Task EnsureAgentRunAdmittedAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var teamInflight = await CountInflightAsync(teamId, cancellationToken).ConfigureAwait(false);

        var globalInflight = await CountInflightAsync(teamId: null, cancellationToken).ConfigureAwait(false);

        EnsureUnderCaps(teamId, teamInflight, globalInflight);
    }

    /// <summary>
    /// The pure admit/reject decision: throws <see cref="AgentRunAdmissionException"/> naming the breached cap +
    /// its env var when the team OR the deployment is already at its in-flight cap; returns cleanly otherwise.
    /// Reads the caps live (env-overridable). Internal so the branch logic is unit-pinned without a DB (Rule 8).
    /// </summary>
    internal static void EnsureUnderCaps(Guid teamId, int teamInflight, int globalInflight)
    {
        var perTeamCap = MaxInflightPerTeam;

        if (teamInflight >= perTeamCap)
            throw new AgentRunAdmissionException($"Team {teamId} already has {teamInflight} agent run(s) in flight, at its cap of {perTeamCap}. Raise {MaxInflightPerTeamEnvVar} to allow more concurrent runs.");

        var globalCap = MaxInflightGlobal;

        if (globalInflight >= globalCap)
            throw new AgentRunAdmissionException($"The deployment already has {globalInflight} agent run(s) in flight, at the global cap of {globalCap}. Raise {MaxInflightGlobalEnvVar} to allow more concurrent runs.");
    }

    /// <summary>Count Queued + Running agent runs — team-scoped when <paramref name="teamId"/> is set, else deployment-wide. The query is NOT wrapped in a try/catch: a fault propagates (fail-closed) so a creation under unknown load is refused, never silently admitted.</summary>
    private async Task<int> CountInflightAsync(Guid? teamId, CancellationToken cancellationToken) =>
        await _db.AgentRun.AsNoTracking()
            .Where(r => InflightStatuses.Contains(r.Status) && (teamId == null || r.TeamId == teamId))
            .CountAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// A new agent run was refused because admitting it would breach the per-team or global in-flight cap
/// (<see cref="AdmissionController"/>). The D4a policy is REJECT: the over-cap run fails cleanly with a message
/// naming the breached cap + the env var to raise it, rather than being deferred-and-retried (a future
/// enhancement). The engine wraps this into a clean node failure, so an over-cap <c>agent.code</c> branch
/// routes to its error edge / the map's continue-on-error policy instead of crashing the run.
/// </summary>
public sealed class AgentRunAdmissionException : Exception
{
    public AgentRunAdmissionException(string message) : base(message) { }
}
