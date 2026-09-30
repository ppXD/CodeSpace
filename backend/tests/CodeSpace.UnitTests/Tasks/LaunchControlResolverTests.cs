using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Tasks.Launch;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapDynamic;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapSynth;
using CodeSpace.Core.Services.Tasks.Projection.Builders.SingleAgent;
using CodeSpace.Core.Services.Tasks.Projection.Builders.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// Pins <see cref="LaunchControlResolver"/> — the one place a route-dependent launch control learns what the resolved
/// route does with it — over the REAL projection builders (their <c>OperatorAcceptance</c> advertisements decide the
/// floor) and a scripted model pool (the only I/O). Every lane × control lands in exactly one disposition, with a reason
/// wherever it is not a plain apply; nothing the operator set is left out of the list.
/// </summary>
[Trait("Category", "Unit")]
public class LaunchControlResolverTests
{
    private static readonly Guid PooledRow = Guid.NewGuid();
    private static readonly Guid ForeignRow = Guid.NewGuid();
    private static readonly Guid Persona = Guid.NewGuid();
    private static readonly ModelDispatchRef PoolDefault = new() { ModelId = "pool-default", ModelCredentialId = Guid.NewGuid(), Provider = "Anthropic" };

    private static LaunchControlResolver Resolver(ScriptedPool pool) => new(new TaskProjectionRegistry(new IWorkflowDefinitionBuilder[] { new SingleAgentDefinitionBuilder(), new PlanMapSynthDefinitionBuilder(), new PlanMapDynamicDefinitionBuilder(), new SupervisorDefinitionBuilder(), new UnadvertisedBuilder() }), pool);

    private static RoutePlan Route(string projectionKind) => new() { ProjectionKind = projectionKind };

    /// <summary>A launch carrying EVERY route-dependent control — a pooled model row, a pooled persona, a floor, both critics, a delivery spec and the plan gate.</summary>
    private static TaskLaunchRequest EveryControl() => new()
    {
        TeamId = Guid.NewGuid(), ActorUserId = Guid.NewGuid(), SurfaceKind = "chat",
        AllowedModelIds = [PooledRow], AllowedAgentDefinitionIds = [Persona], AcceptanceChecks = ["sh", "check.sh"],
        DecisionReviewMode = ReviewMode.Gate, DeliverySpec = new DeliverySpec { OpenPullRequest = true }, RequirePlanConfirmation = true, PlannerReviewMode = ReviewMode.Improve,
        Overrides = new TaskExecutionOverrides { ModelCredentialModelId = PooledRow, AgentDefinitionId = Persona },
    };

    private static LaunchControlOutcome OutcomeOf(LaunchControlResolution resolution, string control) => resolution.Dispositions.Single(d => d.Control == control).Outcome;

    [Theory]
    // lane,                                  model pool,     persona pool,   floor,                         decision critic,  delivery spec,  plan gate,      plan critic
    [InlineData(TaskProjectionKinds.Supervisor, "Applied", "Applied", "Applied", "Applied", "Applied", "Applied", "Applied")]
    [InlineData(TaskProjectionKinds.SingleAgent, "Applied", "Applied", "Applied", "NotApplicable", "NotApplicable", "NotApplicable", "NotApplicable")]
    [InlineData(TaskProjectionKinds.PlanMapSynth, "Applied", "Applied", "Refused", "NotApplicable", "NotApplicable", "Applied", "Applied")]
    [InlineData(TaskProjectionKinds.PlanMapDynamic, "Applied", "Applied", "Refused", "NotApplicable", "NotApplicable", "Applied", "Applied")]
    public async Task Every_control_the_launch_carries_gets_exactly_one_disposition_per_lane(string lane, string models, string personas, string floor, string decisionCritic, string delivery, string planGate, string planCritic)
    {
        var resolution = await Resolver(new ScriptedPool()).ResolveAsync(EveryControl(), Route(lane), CancellationToken.None);

        resolution.Dispositions.Select(d => d.Control).ShouldBe(new[] { LaunchControls.AllowedModelIds, LaunchControls.AllowedAgentDefinitionIds, LaunchControls.AcceptanceChecks, LaunchControls.DecisionReviewMode, LaunchControls.DeliverySpec, LaunchControls.RequirePlanConfirmation, LaunchControls.PlannerReviewMode },
            customMessage: "a control the launch carried is missing from the list — that is the silent drop this resolver exists to end");

        OutcomeOf(resolution, LaunchControls.AllowedModelIds).ToString().ShouldBe(models);
        OutcomeOf(resolution, LaunchControls.AllowedAgentDefinitionIds).ToString().ShouldBe(personas);
        OutcomeOf(resolution, LaunchControls.AcceptanceChecks).ToString().ShouldBe(floor);
        OutcomeOf(resolution, LaunchControls.DecisionReviewMode).ToString().ShouldBe(decisionCritic);
        OutcomeOf(resolution, LaunchControls.DeliverySpec).ToString().ShouldBe(delivery);
        OutcomeOf(resolution, LaunchControls.RequirePlanConfirmation).ToString().ShouldBe(planGate);
        OutcomeOf(resolution, LaunchControls.PlannerReviewMode).ToString().ShouldBe(planCritic);

        resolution.Dispositions.Where(d => d.Outcome is LaunchControlOutcome.NotApplicable or LaunchControlOutcome.Refused or LaunchControlOutcome.Clamped).ShouldAllBe(d => !string.IsNullOrWhiteSpace(d.Reason), "every disposition short of a plain apply says why");
        resolution.GradesOperatorFloor.ShouldBe(floor == "Applied", "the mandate reads the same advertisement the floor's disposition does");
    }

    [Fact]
    public async Task A_launch_carrying_no_route_dependent_control_reports_none_and_never_consults_the_pool()
    {
        var pool = new ScriptedPool();

        var resolution = await Resolver(pool).ResolveAsync(new TaskLaunchRequest { TeamId = Guid.NewGuid(), ActorUserId = Guid.NewGuid(), SurfaceKind = "chat" }, Route(TaskProjectionKinds.SingleAgent), CancellationToken.None);

        resolution.Dispositions.ShouldBeEmpty();
        resolution.ModelClamp.ShouldBeNull();
        pool.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(TaskProjectionKinds.SingleAgent, true)]    // the one agent IS the pin, so the launch bakes the pooled row
    [InlineData(TaskProjectionKinds.PlanMapSynth, false)]  // branches are held at dispatch; the synthesis keeps its model
    public async Task A_pinned_model_row_outside_the_pool_is_clamped_to_the_pools_default(string lane, bool bakedAtLaunch)
    {
        var request = EveryControl() with { Overrides = new TaskExecutionOverrides { ModelCredentialModelId = ForeignRow } };

        var resolution = await Resolver(new ScriptedPool(poolDefault: PoolDefault)).ResolveAsync(request, Route(lane), CancellationToken.None);

        var models = resolution.Dispositions.Single(d => d.Control == LaunchControls.AllowedModelIds);
        models.Outcome.ShouldBe(LaunchControlOutcome.Clamped);
        models.Reason.ShouldContain(ForeignRow.ToString(), customMessage: "the reason names the pin that did not fit");
        models.Reason.ShouldContain("pool-default", customMessage: "and the model that runs instead");
        (resolution.ModelClamp is not null).ShouldBe(bakedAtLaunch);
    }

    [Fact]
    public async Task A_pinned_model_name_is_looked_up_among_the_pools_rows_not_the_whole_team()
    {
        // Names repeat across credentials, so a pinned NAME is pooled only when a pooled ROW carries it.
        var request = EveryControl() with { Overrides = new TaskExecutionOverrides { Model = "claude-sonnet" } };
        var pooled = new ScriptedPool(byName: new ModelDispatchRef { ModelId = "claude-sonnet", ModelCredentialId = Guid.NewGuid(), Provider = "Anthropic" });

        var resolution = await Resolver(pooled).ResolveAsync(request, Route(TaskProjectionKinds.SingleAgent), CancellationToken.None);

        OutcomeOf(resolution, LaunchControls.AllowedModelIds).ShouldBe(LaunchControlOutcome.Applied);
        resolution.ModelClamp.ShouldBeNull();
        pooled.LastAllowedRowIds.ShouldBe(new[] { PooledRow }, "the name lookup is bounded to the operator's pool");
    }

    [Fact]
    public async Task A_pin_outside_a_pool_that_resolves_nothing_is_refused()
    {
        var request = EveryControl() with { Overrides = new TaskExecutionOverrides { ModelCredentialModelId = ForeignRow } };

        var resolution = await Resolver(new ScriptedPool(poolDefault: null)).ResolveAsync(request, Route(TaskProjectionKinds.SingleAgent), CancellationToken.None);

        OutcomeOf(resolution, LaunchControls.AllowedModelIds).ShouldBe(LaunchControlOutcome.Refused);
        resolution.ModelClamp.ShouldBeNull();
    }

    [Theory]
    [InlineData(TaskProjectionKinds.SingleAgent)]
    [InlineData(TaskProjectionKinds.PlanMapSynth)]
    public async Task A_persona_the_pool_excludes_refuses_and_no_persona_is_not_applicable(string lane)
    {
        var excluded = await Resolver(new ScriptedPool()).ResolveAsync(EveryControl() with { Overrides = new TaskExecutionOverrides { AgentDefinitionId = Guid.NewGuid() } }, Route(lane), CancellationToken.None);
        var none = await Resolver(new ScriptedPool()).ResolveAsync(EveryControl() with { Overrides = new TaskExecutionOverrides() }, Route(lane), CancellationToken.None);

        OutcomeOf(excluded, LaunchControls.AllowedAgentDefinitionIds).ShouldBe(LaunchControlOutcome.Refused, "every agent on this lane runs as the launch's persona — the operator excluded it");
        OutcomeOf(none, LaunchControls.AllowedAgentDefinitionIds).ShouldBe(LaunchControlOutcome.NotApplicable, "no agent here carries a persona for the pool to admit or refuse");
    }

    [Fact]
    public async Task A_plan_map_floor_is_refused_with_the_previews_own_acceptance_verdict()
    {
        var resolution = await Resolver(new ScriptedPool()).ResolveAsync(EveryControl(), Route(TaskProjectionKinds.PlanMapSynth), CancellationToken.None);

        resolution.Dispositions.Single(d => d.Control == LaunchControls.AcceptanceChecks).Reason.ShouldBe(LaunchControlResolver.FloorNotGradedReason);
        resolution.GradesOperatorFloor.ShouldBeFalse();
    }

    [Fact]
    public async Task A_route_whose_builder_advertises_nothing_refuses_the_floor_and_holds_no_pool()
    {
        var resolution = await Resolver(new ScriptedPool()).ResolveAsync(EveryControl(), Route(UnadvertisedBuilder.Kind), CancellationToken.None);

        resolution.Dispositions.Single(d => d.Control == LaunchControls.AcceptanceChecks).Reason.ShouldBe(LaunchControlResolver.FloorUnadvertisedReason);
        OutcomeOf(resolution, LaunchControls.AllowedModelIds).ShouldBe(LaunchControlOutcome.NotApplicable, "an unknown lane is never claimed to hold its agents to the pool");
        resolution.GradesOperatorFloor.ShouldBeFalse();
    }

    /// <summary>The pool's only two lookups the resolver may make, scripted; anything else is a resolver reaching past its contract.</summary>
    private sealed class ScriptedPool(ModelDispatchRef? byName = null, ModelDispatchRef? poolDefault = null) : IModelPoolSelector
    {
        public int Calls { get; private set; }
        public IReadOnlyList<Guid>? LastAllowedRowIds { get; private set; }

        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken)
        {
            Calls++;
            LastAllowedRowIds = allowedRowIds;
            return Task.FromResult(byName);
        }

        public Task<ModelDispatchRef?> ResolvePoolDefaultAsync(Guid teamId, IReadOnlyList<Guid> allowedRowIds, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(poolDefault);
        }

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A registered projection that advertises no operator-command adapter — the interface default.</summary>
    private sealed class UnadvertisedBuilder : IWorkflowDefinitionBuilder
    {
        public const string Kind = "unadvertised-lane";
        public string ProjectionKind => Kind;
        public WorkflowDefinition Build(TaskBuildContext context) => throw new NotSupportedException();
    }
}
