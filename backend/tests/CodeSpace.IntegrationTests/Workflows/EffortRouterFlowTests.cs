using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Tasks.Bounds;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The PR3 L2 effort ROUTER end-to-end, on top of the PR2 projection layer. The REAL <see cref="IEffortRouter"/>
/// (resolved from the container — composing the real heuristic classifier + single-agent recipe + the three
/// bounds presets) produces a <see cref="RoutePlan"/>, which drives the REAL
/// <see cref="ITaskRunSnapshotFactory"/> → the projection registry resolves the single-agent builder → the
/// built definition runs through the REAL engine + executor + fake CLI to Success, with NO <c>workflow</c> /
/// <c>workflow_version</c> row. This proves the L2→L3 seam: route → project → run, no Workflow entity.
///
/// <para><b>Fidelity (Rule 12):</b> tier (a) is HIGH — real router over real registries, real factory + starter,
/// real engine over real Postgres, real executor + LocalProcessRunner spawning a real OS process; only the CLI's
/// intelligence is faked at the binary (<see cref="SubtaskAwareFakeCli"/>). POSIX-only for tier (a) (the fake
/// CLI is a /bin/sh script — Rule 12.1). Tier (b) is the zero-core-edit genericity contract: a FAKE classifier /
/// recipe / bounds registered ONLY in a test DI child scope is picked up by the SAME router with no core edit.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class EffortRouterFlowTests
{
    private readonly PostgresFixture _fixture;

    public EffortRouterFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Routed_single_agent_task_projects_and_runs_to_success_as_a_snapshot_run()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // the agent.code suspend dispatches the REAL AgentRunExecutor + real runner + fake CLI

        var workflowCountBefore = await CountWorkflowsAsync(teamId);
        var versionCountBefore = await CountWorkflowVersionsAsync();

        // ── L2: the REAL router turns an operator-chosen-quick request into a RoutePlan. (Explicit standard/deep
        //    now route the multi-agent map-fanout shape — see PlanMapSynthFanoutFlowTests; quick stays single-agent.) ──
        var request = new EffortRouteRequest
        {
            Seed = new TaskLaunchSeed { Goal = "Work on the auth refactor", SurfaceKind = "test", TeamId = teamId },
            RequestedEffort = TaskEffortModes.Quick,
        };

        var plan = await RouteAsync(request);

        plan.EffortMode.ShouldBe(TaskEffortModes.Quick);
        plan.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent, "the single-agent recipe's default projection");
        plan.NeedsConfirmCard.ShouldBeFalse("an explicit operator tier never confirms");
        plan.Caps.MaxParallelism.ShouldBe(1, "the quick preset's caps reached the plan");

        // ── L3: the RoutePlan drives the projection factory → a snapshot run. ──
        var context = new TaskBuildContext
        {
            Seed = request.Seed,
            Route = plan,
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Confined" },
        };

        var handle = await CreateAndRunAsync(context, teamId, userId);
        await RunEngineAsync(handle.RunId);

        await jobClient.WaitForPendingAsync();

        handle.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == handle.RunId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the routed single-agent task must walk start → agent.code → terminal to Success through the real router → projection → engine → executor → fake CLI; if not, inspect the failed WorkflowRunNode rows + the AgentRun.Error for this run");

        // It is a SNAPSHOT run — no Workflow / WorkflowVersion row for a routed task.
        run.WorkflowId.ShouldBeNull("a routed task run is a snapshot run — not a child of any workflow");
        run.WorkflowVersion.ShouldBeNull("a routed task run has no pinned version");
        (await CountWorkflowsAsync(teamId)).ShouldBe(workflowCountBefore, "no workflow row is created for a routed snapshot run");
        (await CountWorkflowVersionsAsync()).ShouldBe(versionCountBefore, "no workflow_version row is created for a routed snapshot run");

        // EVIDENCE the agent really ran the routed goal.
        var agentRun = await db.AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == handle.RunId);
        agentRun.Status.ShouldBe(AgentRunStatus.Succeeded, "the routed agent.code executed to Succeeded via the real executor + runner");

        var result = JsonSerializer.Deserialize<Messages.Agents.AgentRunResult>(agentRun.ResultJson!, AgentJson.Options)!;
        result.Summary.ShouldBe(SubtaskAwareFakeCli.ExpectedSummaryFor("Work on the auth refactor"),
            customMessage: "the real folded summary must be the fake-CLI transform of the routed goal — a mismatch means the routed goal never reached the real CLI");
    }

    [Fact]
    public async Task Auto_route_over_the_real_registries_always_produces_a_confirm_card_from_the_bounds_presets()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var request = new EffortRouteRequest
        {
            Seed = new TaskLaunchSeed { Goal = "Fix a tiny typo", SurfaceKind = "test", TeamId = teamId },
            // No RequestedEffort ⇒ the auto path: the heuristic classifies, always below the floor.
        };

        var plan = await RouteAsync(request);

        plan.WasAutoClassified.ShouldBeTrue();
        plan.NeedsConfirmCard.ShouldBeTrue("the heuristic is always below the confirm floor");
        plan.Confirm.ShouldNotBeNull();

        // The confirm options are DERIVED from the production bounds registry — the three shipped presets.
        plan.Confirm!.Options.Select(o => o.Mode).ShouldBe(
            new[] { TaskEffortModes.Quick, TaskEffortModes.Standard, TaskEffortModes.Deep }, ignoreOrder: true);
    }

    [Fact]
    public async Task A_fake_classifier_recipe_and_bounds_route_with_zero_core_edit()
    {
        // THE zero-core-edit proof at the DI level. Brand-new strategies are registered ONLY in a child scope
        // (open kinds the production core never names). Re-registering the three registries + the router in the
        // scope rebuilds them over the AUGMENTED IEnumerable<T> (production impls + these fakes) — the production
        // registration is untouched. The SAME EffortRouter picks up the fake recipe's projection + the fake bounds
        // caps, proving a new classification / recipe / bounds strategy needs ZERO production-core edit.
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new FakeBounds()).As<IBoundsPreset>();
            b.RegisterInstance(new FakeRecipe()).As<ITaskRecipe>();
            b.RegisterInstance(new FakeClassifier()).As<IEffortClassifier>();

            b.RegisterType<EffortClassifierRegistry>().As<IEffortClassifierRegistry>().InstancePerLifetimeScope();
            b.RegisterType<TaskRecipeRegistry>().As<ITaskRecipeRegistry>().InstancePerLifetimeScope();
            b.RegisterType<BoundsPresetRegistry>().As<IBoundsPresetRegistry>().InstancePerLifetimeScope();
            b.RegisterType<EffortRouter>().As<IEffortRouter>().InstancePerLifetimeScope();
        });

        // Every registry sees BOTH the production AND the fake kind — additive, not a replacement.
        scope.Resolve<IEffortClassifierRegistry>().All.Select(c => c.Kind).ShouldContain(FakeClassifier.FakeKind);
        scope.Resolve<IEffortClassifierRegistry>().All.Select(c => c.Kind).ShouldContain(HeuristicEffortClassifier.ClassifierKind, "the production classifier stays registered");
        scope.Resolve<ITaskRecipeRegistry>().All.Select(r => r.RecipeKind).ShouldContain(FakeRecipe.FakeKind);
        scope.Resolve<ITaskRecipeRegistry>().All.Select(r => r.RecipeKind).ShouldContain(TaskRecipeKinds.SingleAgent);
        scope.Resolve<IBoundsPresetRegistry>().All.Select(p => p.PresetKind).ShouldContain(FakeBounds.FakeKind);

        var request = new EffortRouteRequest
        {
            Seed = new TaskLaunchSeed { Goal = "route me", SurfaceKind = "test", TeamId = Guid.NewGuid() },
            RequestedEffort = FakeBounds.FakeKind,   // resolve the fake bounds by the effort-mode ≡ preset-kind convention
            RequestedRecipe = FakeRecipe.FakeKind,   // pin the fake recipe → its DefaultProjectionKind
        };

        var plan = await scope.Resolve<IEffortRouter>().RouteAsync(request, CancellationToken.None);

        plan.RecipeKind.ShouldBe(FakeRecipe.FakeKind, "the router resolved the fake recipe by its open kind string with zero core edit");
        plan.ProjectionKind.ShouldBe(FakeRecipe.FakeProjection, "the fake recipe's projection flowed through");
        plan.BoundsPreset.ShouldBe(FakeBounds.FakeKind, "the fake bounds preset resolved by the effort mode");
        plan.Caps.MaxParallelism.ShouldBe(FakeBounds.DistinctiveParallelism, "the fake preset's distinctive caps reached the plan");
    }

    // ─── Fake strategies (production core has never heard of these kinds) ─────

    private sealed class FakeClassifier : IEffortClassifier
    {
        public const string FakeKind = "fake-classifier";
        public string Kind => FakeKind;
        public Task<EffortDecision> ClassifyAsync(EffortRouteRequest request, CancellationToken ct) =>
            Task.FromResult(new EffortDecision { Signals = new EffortSignals(), SuggestedEffort = TaskEffortModes.Deep, SuggestedRecipe = FakeRecipe.FakeKind, Confidence = 0.99, ClassifierKind = FakeKind });
    }

    private sealed class FakeRecipe : ITaskRecipe
    {
        public const string FakeKind = "fake-recipe";
        public const string FakeProjection = "fake-projection";
        public string RecipeKind => FakeKind;
        public IReadOnlyList<string> ServesEfforts => Array.Empty<string>();
        public string GoalFrame => "a fake recipe";
        public string BoundsPreset => FakeBounds.FakeKind;
        public string RecommendedAutonomy => "Confined";
        public string DefaultProjectionKind => FakeProjection;
        public bool RequiresPlanReview => true;
        public IReadOnlyList<string> RecommendedPhaseLabels => new[] { "Fake phase" };
    }

    private sealed class FakeBounds : IBoundsPreset
    {
        public const string FakeKind = "fake-bounds";
        public const int DistinctiveParallelism = 42;
        public string PresetKind => FakeKind;
        public RouteCaps ToCaps() => new() { MaxParallelism = DistinctiveParallelism, MaxRounds = 7 };
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<RoutePlan> RouteAsync(EffortRouteRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IEffortRouter>().RouteAsync(request, CancellationToken.None);
    }

    private async Task<TaskRunHandle> CreateAndRunAsync(TaskBuildContext context, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskRunSnapshotFactory>().CreateAndRunAsync(context, teamId, userId, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task<int> CountWorkflowsAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().Workflow.AsNoTracking().CountAsync(w => w.TeamId == teamId);
    }

    private async Task<int> CountWorkflowVersionsAsync()
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowVersion.AsNoTracking().CountAsync();
    }
}
