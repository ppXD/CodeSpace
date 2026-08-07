using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Dtos.Workflows.Planning;

namespace CodeSpace.Core.Services.Workflows.Planning;

/// <summary>
/// The planning orchestrator (Rule 16 — the handler delegates here). Flat pipeline: gate the flag → plan →
/// project → validate → return. Default-OFF: when the flag is off, the planner is never invoked and a clean
/// disabled result is returned (no throw). The projector's contract is "always emits a valid definition", so
/// a validation failure here is an internal bug surfaced loudly via <see cref="WorkflowValidationException"/>.
/// </summary>
public sealed class WorkflowPlanningService : IWorkflowPlanningService, IScopedDependency
{
    // Single-impl assumption: today the only IWorkflowPlanner is LlmWorkflowPlanner (structured_llm). When a 2nd
    // backend lands (agent_planner / template), this bare injection becomes a silent Autofac last-wins — add an
    // IWorkflowPlannerRegistry (mirroring IAgentHarnessRegistry) keyed by a Kind on the request and inject THAT.
    private readonly IWorkflowPlanner _planner;
    private readonly IWorkflowPlanProjector _projector;
    private readonly DefinitionValidator _validator;
    private readonly IRepoGroundingProvider _grounding;

    public WorkflowPlanningService(IWorkflowPlanner planner, IWorkflowPlanProjector projector, DefinitionValidator validator, IRepoGroundingProvider grounding)
    {
        _planner = planner;
        _projector = projector;
        _validator = validator;
        _grounding = grounding;
    }

    public async Task<PlanWorkflowFromTaskResult> PlanFromTaskAsync(WorkflowPlanRequest request, CancellationToken cancellationToken)
    {
        // Service-level grounding (most generic — every planner backend consumes request.GroundingContext). Team
        // from request.TeamId (sourced from ICurrentTeam upstream, never the wire); a repo outside the team → null.
        // reference: null — this lane authors a REUSABLE definition; a pin belongs to a RUN (resolved at ITS
        // launch), so baking one here would permanently stale-pin every future run of the definition.
        var grounding = await _grounding.BuildGroundingAsync(request.RepositoryId, request.TeamId, reference: null, cancellationToken).ConfigureAwait(false);

        var plan = await _planner.PlanAsync(request with { GroundingContext = grounding }, cancellationToken).ConfigureAwait(false);

        var definition = ProjectFor(request, plan);

        EnsureValidProjection(definition);

        return new PlanWorkflowFromTaskResult { Plan = plan, Definition = definition };
    }

    /// <summary>Pick the projection the operator asked for: the L3 coordinated <c>flow.loop</c> variant when <c>Coordinated</c>, else the one-shot graph (the default — byte-identical to the original).</summary>
    private WorkflowDefinition ProjectFor(WorkflowPlanRequest request, PlannedWorkflow plan) =>
        request.Coordinated
            ? _projector.ProjectCoordinated(plan, request.Coordination ?? new CoordinationOptions())
            : _projector.Project(plan);

    /// <summary>The projector promises a valid definition. If it ever doesn't, fail loudly — that's a projector bug, not operator input.</summary>
    private void EnsureValidProjection(WorkflowDefinition definition)
    {
        var result = _validator.Validate(definition);

        if (!result.IsValid)
            throw new WorkflowValidationException(result.Errors);
    }

}
