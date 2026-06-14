using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The fail-closed admission gate every new agent run must pass before it is persisted. Today the only
/// limits on a <c>flow.map</c> fan-out are its per-map maxParallelism + the engine's branch ceiling — neither
/// bounds the TOTAL in-flight agent runs across a team or the whole deployment, so several teams (or several
/// workflows on one big team) fanning out <c>agent.code</c> branches can exhaust runner / Hangfire / model
/// quota. This caps concurrent (Queued + Running) agent runs at two levels: per-team (so one team can't starve
/// the others) and global (a deployment-wide ceiling). It is the single chokepoint <see cref="AgentRunService.CreateAsync"/>
/// consults BEFORE inserting the row — the reconciler's re-dispatch / re-attach operate on already-admitted
/// runs and are NOT re-gated, so a run is counted exactly once.
/// </summary>
public interface IAdmissionController
{
    /// <summary>
    /// Throw <see cref="AgentRunAdmissionException"/> if admitting one more agent run for <paramref name="teamId"/>
    /// would breach the per-team OR the global in-flight cap; return cleanly when there's headroom. FAIL-CLOSED:
    /// if the count query itself faults, the exception propagates (a creation under an unknown load is refused,
    /// not waved through). Called pre-persist so a rejected run never touches the table.
    /// </summary>
    Task EnsureAgentRunAdmittedAsync(Guid teamId, CancellationToken cancellationToken);
}

public sealed class AdmissionController : IAdmissionController, IScopedDependency
{
    // The cap pair is env-overridable (Rule 8) so an operator can tune it without a redeploy — pinned by a unit
    // test. Defaults are chosen to NOT break an ordinary large fan-out: a single flow.map's branch ceiling is 256
    // but its DEFAULT parallelism keeps far fewer than 50 agent runs in flight at once, so PerTeam=50 admits a
    // normal team's concurrent work while still catching a runaway (many maps × many teams). Global=200 is the
    // deployment-wide ceiling — roughly four saturated teams — past which the runner/model quota is at risk.
    // An operator who legitimately runs wider raises the matching env var; the rejection message names it.
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
