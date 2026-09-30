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
using CodeSpace.UnitTests.Infrastructure;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// Pins <see cref="LaunchControlResolver"/> — the one place a route-dependent launch control learns what the resolved
/// route does with it — over the REAL projection builders (their <c>OperatorAcceptance</c> advertisements decide the
/// floor and the tier's mandate) and a scripted model pool (the only I/O). Every lane × control lands in exactly one
/// disposition, with a reason wherever it is not a plain apply; nothing the operator set is left out of the list.
/// </summary>
[Trait("Category", "Unit")]
public class LaunchControlResolverTests
{
    private static readonly Guid Persona = Guid.NewGuid();
    private static readonly Guid CredA = Guid.NewGuid();
    private static readonly PooledModelRow Pooled = Row(Guid.NewGuid(), "pooled-model", CredA, "Direct key");
    private static readonly PooledModelRow Outside = Row(Guid.NewGuid(), "outside-model", CredA, "Direct key");
    private static readonly TaskLaunchSeed Seed = new() { Goal = "g", SurfaceKind = "chat", TeamId = Guid.NewGuid() };

    private static PooledModelRow Row(Guid id, string model, Guid credential, string credentialName, bool usable = true) =>
        new() { Id = id, ModelId = model, ModelCredentialId = credential, CredentialName = credentialName, Provider = "Anthropic", Usable = usable };

    private static LaunchControlResolver Resolver(ScriptedPool pool) => new(new TaskProjectionRegistry(new IWorkflowDefinitionBuilder[] { new SingleAgentDefinitionBuilder(), new PlanMapSynthDefinitionBuilder(), new PlanMapDynamicDefinitionBuilder(), new SupervisorDefinitionBuilder(), new UnadvertisedBuilder() }), pool, EmptyTestDb.New());

    private static RoutePlan Route(string projectionKind) => new() { ProjectionKind = projectionKind };

    /// <summary>A repo-bound Delivery launch carrying EVERY route-dependent control — a pooled model row, a pooled persona, a floor, both critics, a delivery spec, the integrate opt-in and the plan gate.</summary>
    private static TaskLaunchRequest EveryControl() => new()
    {
        TeamId = Guid.NewGuid(), ActorUserId = Guid.NewGuid(), SurfaceKind = "chat", RepositoryId = Guid.NewGuid(), Tier = QualityTier.Delivery,
        AllowedModelIds = [Pooled.Id], AllowedAgentDefinitionIds = [Persona], AcceptanceChecks = ["sh", "check.sh"],
        DecisionReviewMode = ReviewMode.Gate, DeliverySpec = new DeliverySpec { OpenPullRequest = true }, RequirePlanConfirmation = true, PlannerReviewMode = ReviewMode.Improve,
        Overrides = new TaskExecutionOverrides { ModelCredentialModelId = Pooled.Id, AgentDefinitionId = Persona, IntegrateBranches = true },
    };

    private static Task<LaunchControlResolution> ResolveAsync(ScriptedPool pool, TaskLaunchRequest request, string lane) => Resolver(pool).ResolveAsync(request, Seed, Route(lane), CancellationToken.None);

    private static LaunchControlDisposition Of(LaunchControlResolution resolution, string control) => resolution.Dispositions.Single(d => d.Control == control);

    [Theory]
    // lane,                                  models,    personas,  floor,     tier,      decision critic, delivery,        integrate,       plan gate,       plan critic
    [InlineData(TaskProjectionKinds.Supervisor, "Applied", "Applied", "Applied", "Applied", "Applied", "Applied", "Applied", "Applied", "Applied")]
    [InlineData(TaskProjectionKinds.SingleAgent, "Applied", "Applied", "Applied", "Applied", "NotApplicable", "NotApplicable", "NotApplicable", "NotApplicable", "NotApplicable")]
    [InlineData(TaskProjectionKinds.PlanMapSynth, "Applied", "Applied", "Refused", "Applied", "NotApplicable", "NotApplicable", "NotApplicable", "Applied", "Applied")]
    [InlineData(TaskProjectionKinds.PlanMapDynamic, "Applied", "Applied", "Refused", "Applied", "NotApplicable", "NotApplicable", "NotApplicable", "Applied", "Applied")]
    public async Task Every_control_the_launch_carries_gets_exactly_one_disposition_per_lane(string lane, string models, string personas, string floor, string tier, string decisionCritic, string delivery, string integrate, string planGate, string planCritic)
    {
        var resolution = await ResolveAsync(new ScriptedPool(Pooled), EveryControl(), lane);

        resolution.Dispositions.Select(d => d.Control).ShouldBe(new[] { LaunchControls.AllowedModelIds, LaunchControls.AllowedAgentDefinitionIds, LaunchControls.AcceptanceChecks, LaunchControls.Tier, LaunchControls.DecisionReviewMode, LaunchControls.DeliverySpec, LaunchControls.IntegrateBranches, LaunchControls.RequirePlanConfirmation, LaunchControls.PlannerReviewMode },
            customMessage: "a control the launch carried is missing from the list — that is the silent drop this resolver exists to end");

        var expected = new[] { models, personas, floor, tier, decisionCritic, delivery, integrate, planGate, planCritic };
        resolution.Dispositions.Select(d => d.Outcome.ToString()).ShouldBe(expected);

        resolution.Dispositions.Where(d => d.Outcome is LaunchControlOutcome.NotApplicable or LaunchControlOutcome.Refused or LaunchControlOutcome.Clamped).ShouldAllBe(d => !string.IsNullOrWhiteSpace(d.Reason), "every disposition short of a plain apply says why");
    }

    [Fact]
    public async Task A_launch_carrying_no_route_dependent_control_reports_none_and_never_consults_the_pool()
    {
        var pool = new ScriptedPool();

        var resolution = await ResolveAsync(pool, new TaskLaunchRequest { TeamId = Guid.NewGuid(), ActorUserId = Guid.NewGuid(), SurfaceKind = "chat" }, TaskProjectionKinds.SingleAgent);

        resolution.Dispositions.ShouldBeEmpty();
        resolution.ModelClamp.ShouldBeNull();
        pool.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(TaskProjectionKinds.SingleAgent, true)]    // the one agent IS the pin, so the launch bakes the pooled row
    [InlineData(TaskProjectionKinds.PlanMapSynth, false)]  // branches are held at dispatch; the synthesis keeps its model
    public async Task A_pinned_model_row_outside_the_pool_is_clamped_to_the_pools_default_naming_model_and_credential(string lane, bool bakedAtLaunch)
    {
        var request = EveryControl() with { Overrides = new TaskExecutionOverrides { ModelCredentialModelId = Outside.Id } };

        var resolution = await ResolveAsync(new ScriptedPool(Pooled, Outside), request, lane);

        var models = Of(resolution, LaunchControls.AllowedModelIds);
        models.Outcome.ShouldBe(LaunchControlOutcome.Clamped);
        models.Reason.ShouldBe("Model 'outside-model' on 'Direct key' is not in this run's allowed model pool; running 'pooled-model' on 'Direct key' instead.", "the reason names the model and credential — never a row id");
        (resolution.ModelClamp == Pooled).ShouldBe(bakedAtLaunch);
        (resolution.ModelClamp is null).ShouldBe(!bakedAtLaunch);
    }

    [Fact]
    public async Task A_pinned_row_is_honoured_over_a_same_named_pooled_row_on_another_credential()
    {
        // The dispatch used to resolve a NAME, so the smaller-id twin on another key silently replaced the pin while this
        // disposition said Applied. The launch now makes the dispatch's own row-exact choice.
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }.OrderBy(id => id).ToArray();
        var twin = Row(ids[0], "sonnet", Guid.NewGuid(), "Gateway key");
        var pinned = Row(ids[1], "sonnet", CredA, "Direct key");
        var request = EveryControl() with { AllowedModelIds = [twin.Id, pinned.Id], Overrides = new TaskExecutionOverrides { ModelCredentialModelId = pinned.Id } };

        var resolution = await ResolveAsync(new ScriptedPool(twin, pinned), request, TaskProjectionKinds.SingleAgent);

        Of(resolution, LaunchControls.AllowedModelIds).Outcome.ShouldBe(LaunchControlOutcome.Applied);
        resolution.ModelClamp.ShouldBeNull("an honoured pin is baked as authored");
    }

    [Fact]
    public async Task A_pool_that_resolves_nothing_refuses_a_pinned_launch()
    {
        var disabled = Pooled with { Usable = false };
        var request = EveryControl() with { Overrides = new TaskExecutionOverrides { ModelCredentialModelId = Outside.Id } };

        var resolution = await ResolveAsync(new ScriptedPool(disabled, Outside), request, TaskProjectionKinds.SingleAgent);

        Of(resolution, LaunchControls.AllowedModelIds).Outcome.ShouldBe(LaunchControlOutcome.Refused);
        resolution.ModelClamp.ShouldBeNull();
    }

    [Theory]
    [InlineData(TaskProjectionKinds.SingleAgent)]
    [InlineData(TaskProjectionKinds.PlanMapSynth)]
    public async Task A_persona_the_pool_excludes_refuses_and_no_persona_is_not_applicable(string lane)
    {
        var outsider = Guid.NewGuid();
        var excluded = await ResolveAsync(new ScriptedPool(Pooled), EveryControl() with { Overrides = new TaskExecutionOverrides { AgentDefinitionId = outsider } }, lane);
        var none = await ResolveAsync(new ScriptedPool(Pooled), EveryControl() with { Overrides = new TaskExecutionOverrides() }, lane);

        Of(excluded, LaunchControls.AllowedAgentDefinitionIds).Outcome.ShouldBe(LaunchControlOutcome.Refused, "every agent on this lane runs as the launch's persona — the operator excluded it");
        Of(excluded, LaunchControls.AllowedAgentDefinitionIds).Reason.ShouldNotContain(outsider.ToString(), customMessage: "a persona is named the way a model is — never by its id");
        Of(none, LaunchControls.AllowedAgentDefinitionIds).Outcome.ShouldBe(LaunchControlOutcome.NotApplicable, "no agent here carries a persona for the pool to admit or refuse");
    }

    [Fact]
    public async Task A_plan_map_floor_is_refused_with_the_verdict_the_preview_shows()
    {
        var resolution = await ResolveAsync(new ScriptedPool(Pooled), EveryControl(), TaskProjectionKinds.PlanMapSynth);

        Of(resolution, LaunchControls.AcceptanceChecks).Reason.ShouldBe(LaunchControlResolver.FloorNotGradedReason);
        resolution.AcceptanceCompatibility.State.ShouldBe(TaskAcceptanceCompatibilityState.Incompatible);
        resolution.AcceptanceCompatibility.Detail.ShouldBe(LaunchControlResolver.FloorNotGradedReason, "the preview's verdict and the refusal are one fact");
    }

    [Fact]
    public async Task A_repo_less_argv_floor_on_the_supervisor_lane_is_refused_with_the_verdict_the_preview_shows()
    {
        // The supervisor grades its floor at the terminal stop against the branch the run publishes; a repository-free
        // run has no branch, so the floor would be recorded as ungraded. One verdict drives the preview and the refusal.
        var resolution = await ResolveAsync(new ScriptedPool(Pooled), EveryControl() with { RepositoryId = null }, TaskProjectionKinds.Supervisor);

        resolution.AcceptanceCompatibility.State.ShouldBe(TaskAcceptanceCompatibilityState.Incompatible);
        resolution.AcceptanceCompatibility.RequiresRepository.ShouldBe(true);
        var floor = Of(resolution, LaunchControls.AcceptanceChecks);
        floor.Outcome.ShouldBe(LaunchControlOutcome.Refused);
        floor.Reason.ShouldBe(resolution.AcceptanceCompatibility.Detail);
        floor.Reason.ShouldBe(LaunchControlResolver.FloorNeedsRepositoryReason);
    }

    [Fact]
    public async Task A_repo_less_argv_floor_on_the_single_agent_lane_is_applied_because_the_agent_grades_it_in_its_own_workspace()
    {
        // The executor grades a repository-free agent's argv in the scratch workspace it ran in (the local acceptance
        // verifier), so a repo-less Quick launch with a floor — and at Delivery — is gradable, not a dead end.
        var resolution = await ResolveAsync(new ScriptedPool(Pooled), EveryControl() with { RepositoryId = null }, TaskProjectionKinds.SingleAgent);

        resolution.AcceptanceCompatibility.State.ShouldBe(TaskAcceptanceCompatibilityState.Compatible);
        resolution.AcceptanceCompatibility.RequiresRepository.ShouldBe(false);
        Of(resolution, LaunchControls.AcceptanceChecks).Outcome.ShouldBe(LaunchControlOutcome.Applied);
        Of(resolution, LaunchControls.Tier).Outcome.ShouldBe(LaunchControlOutcome.Applied, "the floor the Delivery tier asks for is one this lane grades");
    }

    [Theory]
    [InlineData(false)]   // no check: the tier must not send the operator to author one the floor would then refuse
    [InlineData(true)]    // a check: refused for the same missing repository, and no refusal contradicts another
    public async Task A_repo_less_supervisor_launch_at_delivery_has_one_remedy_bind_a_repository_or_step_down_to_prototype(bool withFloor)
    {
        var request = EveryControl() with { RepositoryId = null, AcceptanceChecks = withFloor ? ["sh", "check.sh"] : null };

        var resolution = await ResolveAsync(new ScriptedPool(Pooled), request, TaskProjectionKinds.Supervisor);

        var tier = Of(resolution, LaunchControls.Tier);
        tier.Outcome.ShouldBe(LaunchControlOutcome.Refused);
        tier.Reason.ShouldNotBeNull().ShouldContain("bind a repository", Case.Insensitive);
        tier.Reason.ShouldContain("Prototype");
        tier.Reason.ShouldNotContain("acceptanceChecks", customMessage: "authoring a check cannot satisfy a route that grades one only against a repository");

        var refused = resolution.Dispositions.Where(d => d.Outcome == LaunchControlOutcome.Refused).ToList();
        refused.Count.ShouldBe(withFloor ? 2 : 1);
        refused.ShouldAllBe(d => d.Reason!.Contains("repository"), "every refusal names the same missing repository — never two remedies that send the operator in circles");
    }

    [Theory]
    [InlineData("")]         // a blank executable
    [InlineData(" ")]
    [InlineData("sh\0x")]    // a NUL no process argv can carry
    public async Task A_malformed_floor_is_refused_by_name_and_does_not_satisfy_the_tiers_mandate(string executable)
    {
        var resolution = await ResolveAsync(new ScriptedPool(Pooled), EveryControl() with { AcceptanceChecks = [executable, "check.sh"] }, TaskProjectionKinds.SingleAgent);

        Of(resolution, LaunchControls.AcceptanceChecks).Reason.ShouldBe(LaunchControlResolver.MalformedFloorReason, "the builders drop this argv, so the caller learns why it grades nothing");
        Of(resolution, LaunchControls.Tier).Outcome.ShouldBe(LaunchControlOutcome.Refused, "a floor no builder bakes cannot be the Delivery floor");
        LaunchControlResolver.IsWellFormedFloor([executable, "check.sh"]).ShouldBeFalse();
    }

    [Theory]
    // Quick grades its single agent with the operator's argv exactly as Deep grades its terminal stop, so claiming
    // Delivery there without one is the same unverified claim — it used to launch, because the mandate asked "is this
    // the supervisor?" rather than "does this route grade a floor?". It used to be a 400 the preview never predicted, too.
    [InlineData(QualityTier.Delivery, TaskProjectionKinds.SingleAgent, false, "Refused")]
    [InlineData(QualityTier.Unattended, TaskProjectionKinds.SingleAgent, false, "Refused")]
    [InlineData(QualityTier.Delivery, TaskProjectionKinds.Supervisor, false, "Refused")]
    [InlineData(QualityTier.Unattended, TaskProjectionKinds.Supervisor, false, "Refused")]
    [InlineData(QualityTier.Delivery, TaskProjectionKinds.SingleAgent, true, "Applied")]
    [InlineData(QualityTier.Unattended, TaskProjectionKinds.Supervisor, true, "Applied")]
    [InlineData(QualityTier.Delivery, TaskProjectionKinds.PlanMapSynth, false, "Applied")]    // plan-map grades no operator floor: demanding one would make it unlaunchable
    [InlineData(QualityTier.Unattended, TaskProjectionKinds.PlanMapDynamic, false, "Applied")]
    [InlineData(QualityTier.Prototype, TaskProjectionKinds.SingleAgent, false, null)]         // self-report stands; nothing to report
    [InlineData(null, TaskProjectionKinds.Supervisor, false, null)]
    public async Task The_tiers_floor_mandate_is_a_disposition_the_preview_can_predict(QualityTier? tier, string lane, bool withFloor, string? expected)
    {
        var request = new TaskLaunchRequest { TeamId = Guid.NewGuid(), ActorUserId = Guid.NewGuid(), SurfaceKind = "chat", RepositoryId = Guid.NewGuid(), Tier = tier, AcceptanceChecks = withFloor ? ["sh", "check.sh"] : null };

        var resolution = await ResolveAsync(new ScriptedPool(), request, lane);

        resolution.Dispositions.SingleOrDefault(d => d.Control == LaunchControls.Tier)?.Outcome.ToString().ShouldBe(expected);
        if (expected == "Refused") Of(resolution, LaunchControls.Tier).Reason.ShouldContain("acceptanceChecks", Case.Insensitive, "the operator needs an actionable name for the missing lever");
    }

    [Fact]
    public async Task A_route_whose_builder_advertises_nothing_refuses_the_floor_and_holds_no_pool()
    {
        var resolution = await ResolveAsync(new ScriptedPool(Pooled), EveryControl(), UnadvertisedBuilder.Kind);

        resolution.AcceptanceCompatibility.State.ShouldBe(TaskAcceptanceCompatibilityState.Unknown);
        Of(resolution, LaunchControls.AcceptanceChecks).Reason.ShouldBe(resolution.AcceptanceCompatibility.Detail);
        Of(resolution, LaunchControls.AllowedModelIds).Outcome.ShouldBe(LaunchControlOutcome.NotApplicable, "an unknown lane is never claimed to hold its agents to the pool");
        Of(resolution, LaunchControls.Tier).Outcome.ShouldBe(LaunchControlOutcome.Applied, "a builder that advertises no adapter grades no floor, so the mandate asks nothing of it");
    }

    /// <summary>The pool's one lookup the resolver may make — its rows, scripted; anything else is a resolver reaching past its contract.</summary>
    private sealed class ScriptedPool(params PooledModelRow[] rows) : IModelPoolSelector
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<PooledModelRow>> ListPoolRowsAsync(Guid teamId, IReadOnlyList<Guid> rowIds, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<PooledModelRow>>(rows.Where(r => rowIds.Contains(r.Id)).ToList());
        }

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotSupportedException();
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
