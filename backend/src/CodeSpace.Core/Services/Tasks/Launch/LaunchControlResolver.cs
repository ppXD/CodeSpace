using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Launch;

/// <summary>
/// Default <see cref="ILaunchControlResolver"/>. Each control's disposition follows what the route's lane does with it:
/// the supervisor lane bakes every one; a plan-map lane plans and dispatches inside the model pool, confirms and reviews
/// its plan, and gates the launch's persona; the single-agent lane holds its agent to the model pool, grades the floor
/// and gates the persona. A lane that does not consume a control names it NotApplicable with the reason, never drops it.
/// The operator floor follows the builder's own <c>OperatorAcceptance</c> advertisement — the fact the preview reports.
/// </summary>
public sealed class LaunchControlResolver : ILaunchControlResolver, IScopedDependency
{
    /// <summary>Why a route whose builder does not grade an operator command refuses one — word for word the route preview's acceptance verdict for that route.</summary>
    public const string FloorNotGradedReason = "The resolved route does not consume an operator-command acceptance floor. Its plan items use separate acceptance contracts.";

    /// <summary>Why a route whose builder never advertised an operator-command adapter refuses a check: nothing on it promises to run the argv.</summary>
    public const string FloorUnadvertisedReason = "The resolved route has not advertised an operator-command acceptance adapter, so nothing on it would run this check.";

    private readonly ITaskProjectionRegistry _projections;
    private readonly IModelPoolSelector _models;

    public LaunchControlResolver(ITaskProjectionRegistry projections, IModelPoolSelector models)
    {
        _projections = projections;
        _models = models;
    }

    public async Task<LaunchControlResolution> ResolveAsync(TaskLaunchRequest request, RoutePlan route, CancellationToken cancellationToken)
    {
        var (modelPool, clamp) = await DisposeModelPoolAsync(request, route, cancellationToken).ConfigureAwait(false);

        var dispositions = new[]
        {
            modelPool,
            DisposeAgentPool(request, route),
            DisposeOperatorFloor(request, route),
            SupervisorOnly(LaunchControls.DecisionReviewMode, request.DecisionReviewMode != ReviewMode.None, route, "Only the supervisor lane reviews decisions."),
            SupervisorOnly(LaunchControls.DeliverySpec, request.DeliverySpec is not null, route, "Only the supervisor lane carries a delivery spec."),
            PlanningOnly(LaunchControls.RequirePlanConfirmation, request.RequirePlanConfirmation == true, route, "This route authors no plan to confirm."),
            PlanningOnly(LaunchControls.PlannerReviewMode, request.PlannerReviewMode != ReviewMode.None, route, "This route authors no plan to review."),
        };

        return new LaunchControlResolution(dispositions.OfType<LaunchControlDisposition>().ToList(), AcceptsOperatorCommand(route) == true, clamp);
    }

    /// <summary>
    /// The allowed model pool. The supervisor gates every spawn inside it. Plan-map and single-agent hold every agent to it
    /// at dispatch (a model outside it runs the pool's default row); an operator-PINNED model outside the pool is reported
    /// Clamped here, and on the single-agent lane — whose one agent IS the pin — the clamp is returned for the launch to
    /// bake, so the frozen agent config shows the model that runs. Refused when nothing in the pool resolves any more.
    /// </summary>
    private async Task<(LaunchControlDisposition? Disposition, ModelDispatchRef? Clamp)> DisposeModelPoolAsync(TaskLaunchRequest request, RoutePlan route, CancellationToken cancellationToken)
    {
        if (request.AllowedModelIds is not { Count: > 0 } pool) return (null, null);
        if (IsSupervisor(route)) return (Applied(LaunchControls.AllowedModelIds), null);
        if (!IsPlanMap(route) && !IsSingleAgent(route)) return (NotApplicable(LaunchControls.AllowedModelIds, $"The '{route.ProjectionKind}' route does not hold its agents to a model pool."), null);

        if (await DescribeUnpooledPinAsync(request, pool, cancellationToken).ConfigureAwait(false) is not { } pin) return (Applied(LaunchControls.AllowedModelIds, HeldAtDispatchReason(route)), null);

        if (await _models.ResolvePoolDefaultAsync(request.TeamId, pool, cancellationToken).ConfigureAwait(false) is not { } fallback)
            return (Refused(LaunchControls.AllowedModelIds, "None of the allowed models resolves to an enabled model under an active credential."), null);

        var runs = IsSingleAgent(route) ? "the agent runs" : "every branch runs";

        return (Clamped(LaunchControls.AllowedModelIds, $"The pinned model {pin} is not in the allowed model pool; {runs} the pool's default, '{fallback.ModelId}', instead."), IsSingleAgent(route) ? fallback : null);
    }

    /// <summary>The operator's pinned agent model, described, when it lies outside the pool: a pinned credentialed-model row the pool does not list, or a pinned model name no pooled row carries (names repeat across credentials, so a name is looked up among the pool's rows). Null when nothing is pinned or the pin is pooled.</summary>
    private async Task<string?> DescribeUnpooledPinAsync(TaskLaunchRequest request, IReadOnlyList<Guid> pool, CancellationToken cancellationToken)
    {
        if (request.Overrides.ModelCredentialModelId is { } row) return pool.Contains(row) ? null : $"(row {row})";
        if (string.IsNullOrWhiteSpace(request.Overrides.Model)) return null;

        return await _models.ResolveDispatchAsync(request.TeamId, request.Overrides.Model, pool, cancellationToken).ConfigureAwait(false) is null ? $"'{request.Overrides.Model}'" : null;
    }

    private static string HeldAtDispatchReason(RoutePlan route) => IsSingleAgent(route)
        ? "The agent's model is held to the pool at dispatch; a model outside it runs the pool's default instead."
        : "The planner is offered only these models, and every branch is held to them at dispatch; a model outside them runs the pool's default instead.";

    /// <summary>
    /// The allowed persona pool. The supervisor gates every spawn inside it. Neither plan-map nor single-agent authors a
    /// persona, so the launch's own persona is the only one any agent there runs as, and the pool is its gate: pooled ⇒
    /// applied, excluded ⇒ refused (the operator excluded it), none named ⇒ not applicable.
    /// </summary>
    private static LaunchControlDisposition? DisposeAgentPool(TaskLaunchRequest request, RoutePlan route)
    {
        if (request.AllowedAgentDefinitionIds is not { Count: > 0 } pool) return null;
        if (IsSupervisor(route)) return Applied(LaunchControls.AllowedAgentDefinitionIds);
        if (!IsPlanMap(route) && !IsSingleAgent(route)) return NotApplicable(LaunchControls.AllowedAgentDefinitionIds, $"The '{route.ProjectionKind}' route does not dispatch personas from a pool.");

        if (request.Overrides.AgentDefinitionId is not { } persona)
            return NotApplicable(LaunchControls.AllowedAgentDefinitionIds, IsPlanMap(route) ? "Plan-map branches carry no persona: the launch names none, and the planner never assigns one." : "The agent carries no persona: the launch names none.");

        return pool.Contains(persona) ? Applied(LaunchControls.AllowedAgentDefinitionIds) : Refused(LaunchControls.AllowedAgentDefinitionIds, $"The launch's persona {persona} is not in the allowed agent pool, and every agent on this route runs as it.");
    }

    /// <summary>The operator's executable acceptance floor: applied where the route's builder grades an operator command, refused where it advertises that it does not — or advertises nothing.</summary>
    private LaunchControlDisposition? DisposeOperatorFloor(TaskLaunchRequest request, RoutePlan route)
    {
        if (request.AcceptanceChecks is not { Count: > 0 }) return null;

        return AcceptsOperatorCommand(route) switch
        {
            true => Applied(LaunchControls.AcceptanceChecks),
            false => Refused(LaunchControls.AcceptanceChecks, FloorNotGradedReason),
            null => Refused(LaunchControls.AcceptanceChecks, FloorUnadvertisedReason),
        };
    }

    private bool? AcceptsOperatorCommand(RoutePlan route) => _projections.TryResolve(route.ProjectionKind, out var builder) ? builder.OperatorAcceptance.AcceptsCommand : null;

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
