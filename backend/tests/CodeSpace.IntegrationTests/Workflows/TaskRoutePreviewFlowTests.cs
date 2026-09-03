using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.RoutePreview;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// B1: the READ-ONLY route preview end-to-end. The REAL <see cref="ITaskRoutePreviewService"/> (composing the
/// real seed-provider registry, the real team-scope guard over real Postgres, and the real effort router with its
/// real classifier / recipe / bounds registries) answers "where would this go?" — and, crucially, LEAVES NOTHING
/// BEHIND: no work_session, no workflow_run. Before this, the confirm card the router builds for a low-confidence
/// or risky-side-effect auto route was unreachable — the run had already started by the time anyone could see it.
///
/// <para><b>Fidelity (Rule 12):</b> HIGH — real production classes over a real database; no fake is substituted
/// for anything under test. No model is seeded for the previews, so the structured classifier takes its
/// documented degrade to the deterministic heuristic baseline, which is what makes these assertions stable.
/// Platform-agnostic (no agent is dispatched — Rule 12.1 guard not needed).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TaskRoutePreviewFlowTests
{
    private readonly PostgresFixture _fixture;

    public TaskRoutePreviewFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_auto_preview_returns_the_confirm_card_and_stages_absolutely_nothing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runsBefore = await CountRunsAsync(teamId);
        var sessionsBefore = await CountSessionsAsync(teamId);

        var route = await PreviewAsync(Request(teamId, userId, "Rename a config key in the settings loader"));

        route.WasAutoClassified.ShouldBeTrue();
        route.NeedsConfirmCard.ShouldBeTrue("the classifier degrades to the always-confirm heuristic baseline with no model seeded");
        route.Confirm.ShouldNotBeNull();
        route.Confirm!.Options.ShouldNotBeEmpty("the options are derived from the real bounds registry");
        route.Confirm.SuggestedMode.ShouldBe(route.EffortMode, "the card pre-selects the tier the route actually chose");

        // THE contract of a preview: asking must not be indistinguishable from launching.
        (await CountRunsAsync(teamId)).ShouldBe(runsBefore,
            customMessage: "the preview staged a workflow_run — it must open no session and stage no run; check TaskRoutePreviewService for anything after RouteAsync");
        (await CountSessionsAsync(teamId)).ShouldBe(sessionsBefore,
            customMessage: "the preview opened a work_session — the preview path must never touch IWorkSessionService");
    }

    [Fact]
    public async Task A_risky_goal_previews_the_card_with_the_risk_signal_set()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // The auto path resolves the registry's preferred classifier, which in the fixture is the structured-LLM one
        // over a canned deterministic reply — it reports NO signals, so it cannot exercise the risk escalation. Pin
        // the auto path to the DETERMINISTIC heuristic baseline (the same child-scope override EffortRouterFlowTests
        // uses for its zero-core-edit tier) so the risk keywords actually fire. Everything else stays real: the real
        // router, the real bounds presets, the real seed provider, the real scope guard over real Postgres.
        var route = await PreviewOverHeuristicAsync(Request(teamId, userId, "Drop the legacy tables and deploy the migration to production"));

        route.NeedsConfirmCard.ShouldBeTrue("a risky/irreversible task always confirms — the router escalates on the risk signal regardless of the classifier's own confidence");
        route.Decision!.Signals.RiskySideEffects.ShouldBeTrue("drop/migrate/deploy/production are the risk keywords the classifier reports");
        route.Confirm!.Rationale.ShouldContain("risky side effects", customMessage: "the card must name the risk so the operator can act on it");
    }

    [Fact]
    public async Task An_explicit_tier_previews_with_no_card_at_all()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var route = await PreviewAsync(Request(teamId, userId, "Drop the legacy tables in production", TaskEffortModes.Deep));

        route.EffortMode.ShouldBe(TaskEffortModes.Deep);
        route.WasAutoClassified.ShouldBeFalse("an explicit tier short-circuits the classifier");
        route.NeedsConfirmCard.ShouldBeFalse("an operator who already chose the tier has nothing left to confirm — even for a risky goal");
        route.Confirm.ShouldBeNull();
    }

    [Fact]
    public async Task A_foreign_repository_is_an_indistinguishable_not_found_on_the_preview_path_too()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var foreignRepoId = await SeedRepositoryAsync(otherTeamId);

        var request = Request(teamId, userId, "Preview against someone else's repository") with { RepositoryId = foreignRepoId };

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => PreviewAsync(request));

        ex.Message.ShouldContain("not found or not accessible",
            customMessage: "the preview must reuse the launch path's fail-closed guard — a cross-team repo must never be routable, and its existence must never leak");
    }

    [Fact]
    public async Task A_launch_persists_the_route_it_was_projected_from_on_the_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // provenance assertion only — never dispatch the agent

        var result = await LaunchAsync(new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Record the route this run was projected from",
            RequestedEffort = TaskEffortModes.Quick,
            Autonomy = "Confined",
        });

        var run = await LoadRunAsync(result.RunId);

        run.RoutePlanJson.ShouldNotBeNullOrWhiteSpace(
            customMessage: "the run must record the FULL route it was projected from — projection_kind alone cannot say WHY the run got the depth it got");

        var persisted = JsonSerializer.Deserialize<RoutePlan>(run.RoutePlanJson!, Json)!;

        persisted.EffortMode.ShouldBe(result.Route.EffortMode);
        persisted.RecipeKind.ShouldBe(result.Route.RecipeKind);
        persisted.ProjectionKind.ShouldBe(result.Route.ProjectionKind);
        persisted.BoundsPreset.ShouldBe(result.Route.BoundsPreset);
        persisted.WasAutoClassified.ShouldBe(result.Route.WasAutoClassified);
        persisted.ClassifierConfidence.ShouldBe(result.Route.ClassifierConfidence);
        persisted.Caps.MaxParallelism.ShouldBe(result.Route.Caps.MaxParallelism, "the bounds the run actually runs under are part of the provenance");
        persisted.Decision!.ClassifierKind.ShouldBe(result.Route.Decision!.ClassifierKind, "who decided the tier is the whole point of recording it");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static TaskLaunchRequest Request(Guid teamId, Guid userId, string goal, string? effort = null) => new()
    {
        TeamId = teamId,
        ActorUserId = userId,
        SurfaceKind = TaskLaunchSurfaceKinds.Chat,
        TaskText = goal,
        RequestedEffort = effort,
    };

    private async Task<RoutePlan> PreviewAsync(TaskLaunchRequest request)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<ITaskRoutePreviewService>().PreviewAsync(request, CancellationToken.None)).Route;
    }

    /// <summary>The same preview, with the auto path pinned to the deterministic heuristic classifier (a registry INSTANCE built over it alone wins the resolve; the router + preview service are rebuilt in the scope so they pick it up). Everything else is the production graph.</summary>
    private async Task<RoutePlan> PreviewOverHeuristicAsync(TaskLaunchRequest request)
    {
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier() })).As<IEffortClassifierRegistry>();
            b.RegisterType<EffortRouter>().As<IEffortRouter>().InstancePerLifetimeScope();
            b.RegisterType<TaskRoutePreviewService>().As<ITaskRoutePreviewService>().InstancePerLifetimeScope();
        });

        return (await scope.Resolve<ITaskRoutePreviewService>().PreviewAsync(request, CancellationToken.None)).Route;
    }

    private async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskLaunchService>().LaunchAsync(request, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    private async Task<int> CountRunsAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().CountAsync(r => r.TeamId == teamId);
    }

    private async Task<int> CountSessionsAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkSession.AsNoTracking().CountAsync(s => s.TeamId == teamId);
    }

    /// <summary>Seeds a provider instance + an active repository in the given team — enough for the scope guard to have a real row to accept or reject.</summary>
    private async Task<Guid> SeedRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var instanceId = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "GH", BaseUrl = $"https://gh-{suffix}.local", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = $"acme/api-{suffix}", WebUrl = "https://gh.local/acme/api", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return repoId;
    }
}
