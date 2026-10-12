using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks.Launch;

/// <summary>
/// Default <see cref="ILaunchControlResolver"/>. Each control's disposition follows what the route's lane does with it:
/// the supervisor lane bakes every one; a plan-map lane plans and dispatches inside the model pool, confirms and reviews
/// its plan, and gates the launch's persona; the single-agent lane holds its agent to the model pool, grades the floor
/// and gates the persona. A lane that does not consume a control names it NotApplicable with the reason, never drops it.
/// The operator floor and the tier's mandate both derive from the acceptance verdict the route preview shows: the route
/// builder's <c>OperatorAcceptance</c> advertisement, read against whether this launch binds a repository.
/// </summary>
public sealed class LaunchControlResolver : ILaunchControlResolver, IScopedDependency
{
    /// <summary>Why a route whose builder does not grade an operator command refuses one — the plan-map verdict the route preview shows word for word.</summary>
    public const string FloorNotGradedReason = "The resolved route does not consume an operator-command acceptance floor. Its plan items use separate acceptance contracts.";

    /// <summary>Why a floor no builder would bake is refused — a blank executable, or an argument that is missing or carries a NUL.</summary>
    public const string MalformedFloorReason = "The acceptance argv is malformed — its executable is blank, or an argument is missing (null) or contains a NUL character — so nothing can run it.";

    /// <summary>Why a route that grades its floor only against a repository refuses one on a launch that binds none — the verdict the route preview shows word for word.</summary>
    public const string FloorNeedsRepositoryReason = "This route grades an acceptance check against the branch it publishes, so it needs a repository: bind one, or launch at Quick effort, whose agent runs the check in its own workspace.";

    private readonly ITaskProjectionRegistry _projections;
    private readonly IModelPoolSelector _models;
    private readonly CodeSpaceDbContext _db;

    public LaunchControlResolver(ITaskProjectionRegistry projections, IModelPoolSelector models, CodeSpaceDbContext db)
    {
        _projections = projections;
        _models = models;
        _db = db;
    }

    public async Task<LaunchControlResolution> ResolveAsync(TaskLaunchRequest request, TaskLaunchSeed seed, RoutePlan route, CancellationToken cancellationToken)
    {
        var compatibility = DescribeAcceptance(route, (request.RepositoryId ?? seed.RepositoryId) is not null);
        var (modelPool, clamp) = await DisposeModelPoolAsync(request, route, cancellationToken).ConfigureAwait(false);
        var agentPool = await DisposeAgentPoolAsync(request, route, cancellationToken).ConfigureAwait(false);

        var dispositions = new[]
        {
            modelPool,
            agentPool,
            DisposeOperatorFloor(request, compatibility),
            DisposeTier(request, compatibility),
            SupervisorOnly(LaunchControls.DecisionReviewMode, request.DecisionReviewMode != ReviewMode.None, route, "Only the supervisor lane reviews decisions."),
            SupervisorOnly(LaunchControls.DeliverySpec, request.DeliverySpec is not null, route, "Only the supervisor lane carries a delivery spec."),
            SupervisorOnly(LaunchControls.IntegrateBranches, request.Overrides.IntegrateBranches is not null, route, "Only the supervisor lane reads this flag: a plan-map fan-out always integrates a repo-bound run, and a single agent has nothing to integrate."),
            PlanningOnly(LaunchControls.RequirePlanConfirmation, request.RequirePlanConfirmation == true, route, "This route authors no plan to confirm."),
            PlanningOnly(LaunchControls.PlannerReviewMode, request.PlannerReviewMode != ReviewMode.None, route, "This route authors no plan to review."),
        };

        return new LaunchControlResolution(dispositions.OfType<LaunchControlDisposition>().ToList(), compatibility, clamp);
    }

    /// <summary>
    /// Whether an acceptance argv is one a builder will actually bake: a non-blank executable and no argument carrying a
    /// NUL — the rule every builder applies before it grades a floor. A malformed floor counts as ABSENT for the tier's
    /// mandate and the auto reroute (it can grade nothing) and is refused by name. The shared argv normaliser being
    /// extracted in parallel (<c>AcceptanceArgv.NormalizeOrNull</c>) replaces this.
    /// </summary>
    internal static bool IsWellFormedFloor(IReadOnlyList<string>? argv) =>
        argv is { Count: > 0 } && !string.IsNullOrWhiteSpace(argv[0]) && argv.All(a => a is not null && !a.Contains('\0'));

    /// <summary>
    /// The route + workspace's operator-command acceptance verdict — the one the route preview shows and the floor's and
    /// the tier's dispositions derive from: unknown for a builder that advertises no adapter, incompatible for one that
    /// grades no operator command (plan-map), and incompatible for an argv adapter that grades only against a repository
    /// (the supervisor) on a launch that binds none. The single agent grades in its own workspace, repository or not.
    /// </summary>
    private TaskAcceptanceCompatibility DescribeAcceptance(RoutePlan route, bool hasRepository)
    {
        var unknown = new TaskAcceptanceCompatibility { ProjectionKind = route.ProjectionKind, Detail = "The resolved route has not advertised an operator-command acceptance adapter; compatibility is unknown." };
        if (!_projections.TryResolve(route.ProjectionKind, out var builder) || builder.OperatorAcceptance.AcceptsCommand is null) return unknown;
        var adapter = builder.OperatorAcceptance;
        if (adapter.AcceptsCommand == false) return unknown with { State = TaskAcceptanceCompatibilityState.Incompatible, Detail = FloorNotGradedReason };
        if (adapter.GradingKind != BenchmarkGradingKind.TestsPass) return unknown with { Detail = "The resolved acceptance adapter does not advertise the proposed argv input format." };
        var compatible = adapter.GradesCommand(hasRepository);
        return new TaskAcceptanceCompatibility
        {
            ProjectionKind = route.ProjectionKind, GradingKind = adapter.GradingKind.ToString(), RequiresRepository = adapter.RequiresRepository,
            State = compatible ? TaskAcceptanceCompatibilityState.Compatible : TaskAcceptanceCompatibilityState.Incompatible,
            Detail = compatible ? "The resolved route and workspace support this acceptance input. Runtime prerequisites and the check result still require independent execution." : FloorNeedsRepositoryReason,
        };
    }

    /// <summary>
    /// The allowed model pool. The supervisor gates every spawn inside it. Plan-map and single-agent hold every agent to it
    /// at dispatch; an operator-PINNED model is run through the same <see cref="AllowedModelPool"/> choice the dispatch
    /// makes, so a pin that has to move is reported Clamped here — and on the single-agent lane, whose one agent IS the
    /// pin, returned for the launch to bake, so the frozen agent config shows the row that runs. Refused when nothing in
    /// the pool can run.
    /// </summary>
    private async Task<(LaunchControlDisposition? Disposition, PooledModelRow? Clamp)> DisposeModelPoolAsync(TaskLaunchRequest request, RoutePlan route, CancellationToken cancellationToken)
    {
        if (request.AllowedModelIds is not { Count: > 0 } pool) return (null, null);
        if (IsSupervisor(route)) return (Applied(LaunchControls.AllowedModelIds), null);
        if (!IsPlanMap(route) && !IsSingleAgent(route)) return (NotApplicable(LaunchControls.AllowedModelIds, $"The '{route.ProjectionKind}' route does not hold its agents to a model pool."), null);

        var pin = new ModelPin(request.Overrides.ModelCredentialModelId, request.Overrides.Model, request.Overrides.ModelCredentialId);
        if (pin is { RowId: null, CredentialId: null } && string.IsNullOrWhiteSpace(pin.Model)) return (Applied(LaunchControls.AllowedModelIds, HeldAtDispatchReason(route)), null);

        var choice = AllowedModelPool.Choose(pool, await _models.ListPoolRowsAsync(request.TeamId, AllowedModelPool.RowsToRead(pool, pin), cancellationToken).ConfigureAwait(false), pin);

        if (choice.Row is null) return (Refused(LaunchControls.AllowedModelIds, choice.Note ?? AllowedModelPool.NothingRunsNote), null);
        if (choice.Note is null) return (Applied(LaunchControls.AllowedModelIds, HeldAtDispatchReason(route)), null);

        return (Clamped(LaunchControls.AllowedModelIds, choice.Note), IsSingleAgent(route) ? choice.Row : null);
    }

    private static string HeldAtDispatchReason(RoutePlan route) => IsSingleAgent(route)
        ? "The agent's model is held to the pool at dispatch; a model outside it runs the pool's default instead."
        : "The planner is offered only these models, and every branch is held to them at dispatch; a model outside them runs the pool's default instead.";

    /// <summary>
    /// The allowed persona pool. The supervisor gates every spawn inside it. Neither plan-map nor single-agent authors a
    /// persona, so the launch's own persona is the only one any agent there runs as, and the pool is its gate: pooled ⇒
    /// applied, excluded ⇒ refused (the operator excluded it), none named ⇒ not applicable.
    /// </summary>
    private async Task<LaunchControlDisposition?> DisposeAgentPoolAsync(TaskLaunchRequest request, RoutePlan route, CancellationToken cancellationToken)
    {
        if (request.AllowedAgentDefinitionIds is not { Count: > 0 } pool) return null;
        if (IsSupervisor(route)) return Applied(LaunchControls.AllowedAgentDefinitionIds);
        if (!IsPlanMap(route) && !IsSingleAgent(route)) return NotApplicable(LaunchControls.AllowedAgentDefinitionIds, $"The '{route.ProjectionKind}' route does not dispatch personas from a pool.");

        if (request.Overrides.AgentDefinitionId is not { } persona)
            return NotApplicable(LaunchControls.AllowedAgentDefinitionIds, IsPlanMap(route) ? "Plan-map branches carry no persona: the launch names none, and the planner never assigns one." : "The agent carries no persona: the launch names none.");

        if (pool.Contains(persona)) return Applied(LaunchControls.AllowedAgentDefinitionIds);

        return Refused(LaunchControls.AllowedAgentDefinitionIds, $"{await PersonaSubjectAsync(request.TeamId, persona, cancellationToken).ConfigureAwait(false)} is not in the allowed agent pool, and every agent on this route runs as it.");
    }

    /// <summary>The launch's persona as the operator knows it — by name, the way a model note names a model, never by id. A persona this team cannot see stays unnamed.</summary>
    private async Task<string> PersonaSubjectAsync(Guid teamId, Guid persona, CancellationToken cancellationToken)
    {
        var name = await _db.AgentDefinition.AsNoTracking()
            .Where(a => a.Id == persona && a.TeamId == teamId && a.Scope == DefinitionScope.Working && a.DeletedDate == null)
            .Select(a => a.Name)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return name is null ? "The launch's persona" : $"The launch's persona '{name}'";
    }

    /// <summary>The operator's executable acceptance floor, derived from the route + workspace's acceptance verdict: applied where it is compatible, refused — with the verdict's own words — where it is not, and refused by name when no builder would bake the argv at all.</summary>
    private static LaunchControlDisposition? DisposeOperatorFloor(TaskLaunchRequest request, TaskAcceptanceCompatibility compatibility)
    {
        if (request.AcceptanceChecks is not { Count: > 0 } argv) return null;
        if (!IsWellFormedFloor(argv)) return Refused(LaunchControls.AcceptanceChecks, MalformedFloorReason);

        return compatibility.State == TaskAcceptanceCompatibilityState.Compatible ? Applied(LaunchControls.AcceptanceChecks) : Refused(LaunchControls.AcceptanceChecks, compatibility.Detail);
    }

    /// <summary>
    /// The quality tier's floor mandate (P3.2), read off the same route + workspace verdict as the floor: Delivery /
    /// Unattended where this launch's workspace lets the route grade an operator floor — the supervisor's terminal stop
    /// with a repository, the single agent's own oracle anywhere — must carry a well-formed one, or a caller could claim
    /// Delivery-grade verification while skipping the one lever that gates the result; there is no sensible
    /// server-authored argv to default to. Where the route grades a floor only against a repository this launch does not
    /// bind, no check could satisfy the mandate, so the one remedy is to bind a repository or step down to Prototype —
    /// never "author a check" the floor would then refuse. A plan-map route grades none (its items carry their own
    /// contracts), so nothing is asked of it; the tier's output-review floor applies on every route. Refused with the same
    /// code as every refusal, so the preview predicts it too.
    /// </summary>
    private static LaunchControlDisposition? DisposeTier(TaskLaunchRequest request, TaskAcceptanceCompatibility compatibility)
    {
        if (request.Tier is not { } tier || tier is not (QualityTier.Delivery or QualityTier.Unattended)) return null;
        if (NeedsRepository(compatibility)) return Refused(LaunchControls.Tier, $"{tier} quality needs an acceptance check this route can grade, and it grades one only against a repository: bind a repository, or launch at Prototype quality for a self-reported result.");
        if (compatibility.State != TaskAcceptanceCompatibilityState.Compatible) return Applied(LaunchControls.Tier, "This route grades no operator floor, so the tier asks it for none; the tier's output-review floor still applies.");
        if (IsWellFormedFloor(request.AcceptanceChecks)) return Applied(LaunchControls.Tier);

        return Refused(LaunchControls.Tier, $"{tier} quality requires an executable acceptance check (acceptanceChecks) — author one, or launch at Prototype quality for a self-reported result.");
    }

    /// <summary>The route grades an operator floor, but only against a repository this launch does not bind.</summary>
    private static bool NeedsRepository(TaskAcceptanceCompatibility compatibility) => compatibility is { State: TaskAcceptanceCompatibilityState.Incompatible, RequiresRepository: true };

    /// <summary>A control only the supervisor lane consumes — applied there, not applicable everywhere else.</summary>
    private static LaunchControlDisposition? SupervisorOnly(string control, bool requested, RoutePlan route, string reason) =>
        !requested ? null : IsSupervisor(route) ? Applied(control) : NotApplicable(control, reason);

    /// <summary>A control over an authored plan — applied on the lanes that author one (supervisor, plan-map), not applicable on the rest.</summary>
    private static LaunchControlDisposition? PlanningOnly(string control, bool requested, RoutePlan route, string reason) =>
        !requested ? null : IsSupervisor(route) || IsPlanMap(route) ? Applied(control) : NotApplicable(control, reason);

    private static bool IsSupervisor(RoutePlan route) => route.ProjectionKind == TaskProjectionKinds.Supervisor;

    private static bool IsPlanMap(RoutePlan route) => route.ProjectionKind is TaskProjectionKinds.PlanMapSynth or TaskProjectionKinds.PlanMapDynamic;

    private static bool IsSingleAgent(RoutePlan route) => route.ProjectionKind == TaskProjectionKinds.SingleAgent;

    private static LaunchControlDisposition Applied(string control, string? reason = null) => new() { Control = control, Outcome = LaunchControlOutcome.Applied, Reason = reason };

    private static LaunchControlDisposition Clamped(string control, string reason) => new() { Control = control, Outcome = LaunchControlOutcome.Clamped, Reason = reason };

    private static LaunchControlDisposition NotApplicable(string control, string reason) => new() { Control = control, Outcome = LaunchControlOutcome.NotApplicable, Reason = reason };

    private static LaunchControlDisposition Refused(string control, string reason) => new() { Control = control, Outcome = LaunchControlOutcome.Refused, Reason = reason };
}
