using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.Core.Services.Tasks.Launch;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The PR4 L1 LAUNCH surface end-to-end, on top of the PR2 projection + PR3 router layers. The REAL
/// <see cref="ITaskLaunchService"/> (composing the real seed-provider registry + the real effort router + the
/// real snapshot factory) takes a <see cref="TaskLaunchRequest"/> (team + actor sourced from the current
/// context, never the wire), seeds → routes → projects → starts a snapshot run; the REAL engine + executor +
/// fake CLI run the single-agent <c>agent.code</c> to Success, with NO <c>workflow</c> / <c>workflow_version</c>
/// row. Proves the whole L1→L2→L3 spine in one call.
///
/// <para><b>Fidelity (Rule 12):</b> the run tiers are HIGH — real launch service over real registries, real
/// factory + starter, real engine over real Postgres, real executor + LocalProcessRunner spawning a real OS
/// process; only the CLI's intelligence is faked at the binary (<see cref="SubtaskAwareFakeCli"/>). POSIX-only
/// for the run tiers (the fake CLI is a /bin/sh script — Rule 12.1). The team-scope reject + the genericity
/// contract tiers need no agent and run everywhere.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TaskLaunchFlowTests
{
    private readonly PostgresFixture _fixture;

    public TaskLaunchFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Chat_launch_at_quick_effort_projects_and_runs_a_real_agent_to_success_with_no_workflow_row()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // the agent.code suspend dispatches the REAL AgentRunExecutor + real runner + fake CLI

        var workflowCountBefore = await CountWorkflowsAsync(teamId);
        var versionCountBefore = await CountWorkflowVersionsAsync();

        // A chat task at an explicit quick effort, analysis-only (no repo → no clone, the proven fake-CLI shape).
        // The launch service derives the seed, routes (explicit tier ⇒ no confirm; quick ⇒ single-agent), projects
        // single-agent, and starts the snapshot run. (Explicit standard routes the multi-agent map-fanout shape
        // — covered by PlanMapSynthFanoutFlowTests; deep routes the supervisor lane — covered by
        // SupervisorProjectionFlowTests; this single-agent E2E pins the quick tier.)
        var request = new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Work on the auth refactor",
            RequestedEffort = TaskEffortModes.Quick,
            Autonomy = "Confined",
            Overrides = new TaskExecutionOverrides { Harness = "codex-cli", RunnerKind = "local" },
        };

        var result = await LaunchAsync(request);

        result.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent);
        result.SurfaceKind.ShouldBe(TaskLaunchSurfaceKinds.Chat);
        result.Route.EffortMode.ShouldBe(TaskEffortModes.Quick);
        result.Route.NeedsConfirmCard.ShouldBeFalse("an explicit operator tier never confirms");
        result.LinkedEntity.ShouldBeNull("a free-text chat task has no source entity");

        await RunEngineAsync(result.RunId);
        await jobClient.WaitForPendingAsync();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == result.RunId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the launched chat task must walk start → agent.code → terminal to Success through the real launch → route → projection → engine → executor → fake CLI; if not, inspect the failed WorkflowRunNode rows + the AgentRun.Error for this run");

        // It is a SNAPSHOT run — no Workflow / WorkflowVersion row for a launched task (PR1's promise, end to end).
        run.WorkflowId.ShouldBeNull("a launched task run is a snapshot run — not a child of any workflow");
        run.WorkflowVersion.ShouldBeNull("a launched task run has no pinned version");
        (await CountWorkflowsAsync(teamId)).ShouldBe(workflowCountBefore, "no workflow row is created for a launched snapshot run");
        (await CountWorkflowVersionsAsync()).ShouldBe(versionCountBefore, "no workflow_version row is created for a launched snapshot run");

        // EVIDENCE the agent really ran the launched goal through the real CLI.
        var agentRun = await db.AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == result.RunId);
        agentRun.Status.ShouldBe(AgentRunStatus.Succeeded, "the launched agent.code executed to Succeeded via the real executor + runner");

        var folded = JsonSerializer.Deserialize<Messages.Agents.AgentRunResult>(agentRun.ResultJson!, AgentJson.Options)!;
        folded.Summary.ShouldBe(SubtaskAwareFakeCli.ExpectedSummaryFor("Work on the auth refactor"),
            customMessage: "the real folded summary must be the fake-CLI transform of the launched goal — a mismatch means the goal never reached the real CLI");
    }

    [Fact]
    public async Task Launching_with_a_cost_cap_tightens_the_route_caps_through_the_full_command_path()
    {
        // F1: the operator's safety-budget cap flows the WHOLE command path — LaunchTaskCommand.Caps → the handler's
        // BuildCapsOverride → TaskLaunchRequest.CapsOverride → BuildRouteRequest → the router merge → Route.Caps.
        // Dispatched via the mediator (NOT the service directly) so the new command→request wiring is exercised, not
        // bypassed. The downstream (Route.Caps → supervisor config → SupervisorBounds force-stop) is tested already.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // route assertion only — don't run the agent

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var result = await scope.Resolve<IMediator>().Send(new LaunchTaskCommand
        {
            TaskText = "Work on the auth refactor",
            Effort = TaskEffortModes.Quick,
            Autonomy = "Confined",
            Harness = "codex-cli",
            RunnerKind = "local",
            Caps = new TaskCapsOverride { MaxCostUsd = 3.25m, MaxParallelism = 2 },
        }, CancellationToken.None);

        result.Route.Caps.MaxCostUsd.ShouldBe(3.25m, "the operator cost cap reached the route through the handler (was dropped — CapsOverride=null — before F1)");
        result.Route.Caps.MaxParallelism.ShouldBe(2, "the operator parallelism cap reached the route too");
    }

    [Fact]
    public async Task Chat_launch_with_no_effort_auto_classifies_runs_and_rides_a_confirm_card_along()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        // No RequestedEffort ⇒ the auto path: the heuristic classifies (always below the confirm floor). PR4 does
        // NOT block on confirm — the run STILL launches; the confirm card rides along on the result as the
        // operator's escalation affordance.
        var request = new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Fix a tiny typo in the README",
            Autonomy = "Confined",
            Overrides = new TaskExecutionOverrides { Harness = "codex-cli", RunnerKind = "local" },
        };

        var result = await LaunchAsync(request);

        // The run launched regardless of the confirm card (always-run).
        result.RunId.ShouldNotBe(Guid.Empty);
        result.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent);

        // The escalation affordance rides along for the UI.
        result.Route.WasAutoClassified.ShouldBeTrue();
        result.Route.NeedsConfirmCard.ShouldBeTrue("the heuristic is always below the confirm floor");
        result.Route.Confirm.ShouldNotBeNull("the confirm card rides along on the auto path");
        result.Route.Confirm!.Options.ShouldNotBeEmpty();

        await RunEngineAsync(result.RunId);
        await jobClient.WaitForPendingAsync();

        var run = await LoadRunAsync(result.RunId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "PR4 always runs even with a confirm card pending — the auto-classified task still walks to Success; the card is an affordance, not a gate");
    }

    [Fact]
    public async Task A_same_team_repo_is_accepted_and_round_trips_into_the_projected_agent_config()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedRepositoryAsync(teamId);

        // The launch passes the team-scope check (same-team repo) and projects the agent.code with the bound repo.
        // We assert the ACCEPT path + the repo round-trip into the frozen snapshot — without running (a real clone
        // of a seed-only repo would fail; the run tiers above cover the analysis-only Success path).
        var request = new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Touch the bound repo",
            RepositoryId = repoId,
            RequestedEffort = TaskEffortModes.Quick,
        };

        var result = await LaunchAsync(request);

        result.RunId.ShouldNotBe(Guid.Empty);

        var run = await LoadRunAsync(result.RunId);
        run.DefinitionSnapshotJson.ShouldNotBeNull("the launched definition is frozen inline on the run");

        // The repo flowed seed → profile → projected agent.code input.
        var agentRepo = ReadAgentRepositoryId(run.DefinitionSnapshotJson!);
        agentRepo.ShouldBe(repoId.ToString(), "the same-team repo round-trips into the projected agent.code repositoryId input");
    }

    [Fact]
    public async Task A_cross_team_repo_is_rejected_fail_closed_and_never_leaked()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // A repo that belongs to ANOTHER team. The launch resolves the seed, then the team-scope check rejects it
        // with a clear not-found — indistinguishable from a missing repo, so a foreign repo never leaks.
        var foreignRepoId = await SeedRepositoryAsync(otherTeamId);

        var request = new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Try to reach a foreign repo",
            RepositoryId = foreignRepoId,
            RequestedEffort = TaskEffortModes.Standard,
        };

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => LaunchAsync(request));

        ex.Message.ShouldContain("not found or not accessible",
            customMessage: "a cross-team repo must surface as a generic not-found — never reveal the repo exists in another team");

        // No run was created for the rejected launch.
        (await CountRunsForTeamAsync(teamId)).ShouldBe(0, "the launch must reject BEFORE starting any run");
    }

    [Fact]
    public async Task A_fake_surface_provider_resolves_routes_and_runs_with_zero_core_edit_and_round_trips_its_linked_entity()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        // THE zero-core-edit proof for the LAUNCH layer. A brand-new launch SURFACE is registered ONLY here (a
        // fake seed provider for an open surface string the production core has never heard of). It derives its
        // goal entirely from the SurfacePayload (the folded LaunchContext.Raw) + attaches a LinkedEntityRef —
        // proving the core NEVER reads the payload, only the provider does. The SAME ITaskLaunchService resolves
        // it through the registry, routes, projects + runs to Success, and the linked entity round-trips into the
        // result — without one line changed in the launch service / registry. A new surface is purely "register a
        // provider", exactly like adding an IAgentHarness.
        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        // A child DI scope that ADDS the fake provider to the surface variant axis + re-registers the registry +
        // launch service over the AUGMENTED IEnumerable<ITaskLaunchSeedProvider> (the production ChatSeedProvider
        // + this fake). The production registration is untouched.
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new FakeSurfaceSeedProvider()).As<ITaskLaunchSeedProvider>();
            b.RegisterType<TaskLaunchSeedProviderRegistry>().As<ITaskLaunchSeedProviderRegistry>().InstancePerLifetimeScope();
            b.RegisterType<TaskLaunchService>().As<ITaskLaunchService>().InstancePerLifetimeScope();
        });

        // The registry sees BOTH the production chat provider AND the test-only fake — additive, not a replacement.
        var registry = scope.Resolve<ITaskLaunchSeedProviderRegistry>();
        registry.All.Select(p => p.SurfaceKind).ShouldContain(TaskLaunchSurfaceKinds.Chat, "the production chat provider is still registered");
        registry.All.Select(p => p.SurfaceKind).ShouldContain(FakeSurfaceSeedProvider.Surface, "the test-only fake provider joined the same variant axis");

        // The opaque per-surface payload the CORE never reads — only the fake provider does. It carries the goal +
        // the linked-entity id.
        var payload = new Dictionary<string, JsonElement>
        {
            [FakeSurfaceSeedProvider.Surface] = JsonSerializer.SerializeToElement(new { goal = "Fake surface goal", entityId = "ISSUE-99" }),
        };

        var request = new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = FakeSurfaceSeedProvider.Surface,
            RequestedEffort = TaskEffortModes.Quick,
            Autonomy = "Confined",
            Overrides = new TaskExecutionOverrides { Harness = "codex-cli", RunnerKind = "local" },
            SurfacePayload = payload,
        };

        var result = await scope.Resolve<ITaskLaunchService>().LaunchAsync(request, CancellationToken.None);

        result.SurfaceKind.ShouldBe(FakeSurfaceSeedProvider.Surface, "the fake surface resolved by its open string with zero core edit");
        result.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent);

        // The linked entity the provider attached — derived from the payload the core never read — round-trips out.
        result.LinkedEntity.ShouldNotBeNull("the fake provider's linked entity round-trips into the result");
        result.LinkedEntity!.EntityKind.ShouldBe("issue");
        result.LinkedEntity.EntityId.ShouldBe("ISSUE-99");

        await RunEngineAsync(result.RunId);
        await jobClient.WaitForPendingAsync();

        var run = await LoadRunAsync(result.RunId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the launch service resolved + routed + ran a surface unknown to the production core — the dispatch is registry.Resolve(openString) and the core never read the surface payload");
        run.WorkflowId.ShouldBeNull("the fake-surface launch ran as a snapshot run too");

        // The goal the fake provider derived FROM THE PAYLOAD reached the real CLI.
        var agentRun = await LoadAgentRunAsync(result.RunId);
        var folded = JsonSerializer.Deserialize<Messages.Agents.AgentRunResult>(agentRun.ResultJson!, AgentJson.Options)!;
        folded.Summary.ShouldBe(SubtaskAwareFakeCli.ExpectedSummaryFor("Fake surface goal"),
            customMessage: "the goal the fake provider read from the surface payload must reach the real CLI — proving only the provider interpreted Raw, not the core");
    }

    // ─── Multi-repo launch (the front door to the multi-repo workspace底座) ──────────────────────────────────────

    [Fact]
    public async Task Multi_repo_launch_projects_every_related_repo_into_the_frozen_agent_node_and_the_run_scope()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var primary = await SeedRepositoryAsync(teamId);
        var api = await SeedRepositoryAsync(teamId);
        var web = await SeedRepositoryAsync(teamId);

        // A single-agent (quick) task across THREE same-team repos. We assert the ACCEPT path + the multi-repo round
        // trip into the frozen snapshot + the run SCOPE (what a session-branch resolver later reads) — without running
        // (a real multi-repo clone of seed-only repos would fail; the single-agent run path is proven above).
        var request = new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Coordinated change across api + web",
            RepositoryId = primary,
            RelatedRepositories = new[]
            {
                new TaskRelatedRepository { RepositoryId = api, Alias = "api", Access = "write" },
                new TaskRelatedRepository { RepositoryId = web, Alias = "web", Access = "read" },
            },
            RequestedEffort = TaskEffortModes.Quick,
        };

        var result = await LaunchAsync(request);

        result.ProjectionKind.ShouldBe(TaskProjectionKinds.SingleAgent);

        var run = await LoadRunAsync(result.RunId);

        // The primary stays on the agent node's repositoryId; the related repos land on relatedRepositories VERBATIM.
        ReadAgentRepositoryId(run.DefinitionSnapshotJson!).ShouldBe(primary.ToString());

        var related = ReadAgentRelatedRepositories(run.DefinitionSnapshotJson!);
        related.Select(r => r.repoId).ShouldBe(new[] { api.ToString(), web.ToString() }, "both related repos round-trip into the frozen agent.code inputs, in authored order");
        related.Single(r => r.repoId == api.ToString()).access.ShouldBe("write", "the authored write access round-trips");
        related.Single(r => r.repoId == web.ToString()).access.ShouldBe("read", "the authored read access round-trips");
        related.Single(r => r.repoId == api.ToString()).alias.ShouldBe("api");

        // The run SCOPE is the primary PLUS every related repo (distinct) — this is the set a multi-repo session-branch
        // resolver scans by, so opening the multi-repo front door makes a multi-repo session turn producible.
        run.ScopeRepositoryIds.ShouldBe(new[] { primary, api, web }, ignoreOrder: true,
            customMessage: "the launch scope folds primary + related repos — the substrate TaskRunSnapshotFactory already supports, now fed");
    }

    [Fact]
    public async Task A_cross_team_RELATED_repo_is_rejected_fail_closed_and_no_run_is_created()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // The PRIMARY is in-team but a RELATED repo belongs to ANOTHER team. The launch must validate EVERY repo and
        // reject fail-closed — a foreign related repo can never be pulled into the workspace (the tenancy floor).
        var primary = await SeedRepositoryAsync(teamId);
        var foreignRelated = await SeedRepositoryAsync(otherTeamId);

        var request = new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Try to pull in a foreign repo as related",
            RepositoryId = primary,
            RelatedRepositories = new[] { new TaskRelatedRepository { RepositoryId = foreignRelated, Alias = "api" } },
            RequestedEffort = TaskEffortModes.Quick,
        };

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => LaunchAsync(request));

        ex.Message.ShouldContain("not found or not accessible",
            customMessage: "a cross-team RELATED repo must surface as a generic not-found — the same fail-closed posture as the primary, never a cross-team workspace");

        (await CountRunsForTeamAsync(teamId)).ShouldBe(0, "a launch with ANY inaccessible repo rejects BEFORE starting a run (and before opening a session)");
    }

    [Fact]
    public async Task Multi_repo_deep_launch_projects_every_related_repo_into_the_plan_map_body_agent()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var primary = await SeedRepositoryAsync(teamId);
        var api = await SeedRepositoryAsync(teamId);

        // A DEEP task routes to the plan-map-synth fan-out (the supervisor lane is off by default → deep degrades to
        // map-fanout). The related repos must reach the MAP BODY agent.code node (each fan-out branch clones them) —
        // proving the multi-repo workspace flows through the map projection too, not only single-agent. Frozen-def
        // assertion only (the map run path is covered by PlanMapSynthFanoutFlowTests).
        var request = new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Deep coordinated change",
            RepositoryId = primary,
            RelatedRepositories = new[] { new TaskRelatedRepository { RepositoryId = api, Alias = "api", Access = "write" } },
            RequestedEffort = TaskEffortModes.Deep,
        };

        var result = await LaunchAsync(request);

        result.ProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth, "deep degrades to the map-fanout shape with the supervisor lane off (the default)");

        var run = await LoadRunAsync(result.RunId);

        var related = ReadAgentRelatedRepositories(run.DefinitionSnapshotJson!);
        related.Select(r => r.repoId).ShouldBe(new[] { api.ToString() }, "the related repo reaches the map body agent.code node — a deep multi-repo launch is NOT a silent drop");
        related.Single().access.ShouldBe("write");
        run.ScopeRepositoryIds.ShouldBe(new[] { primary, api }, ignoreOrder: true);
    }

    [Fact]
    public async Task A_single_repo_launch_omits_relatedRepositories_byte_identical()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var primary = await SeedRepositoryAsync(teamId);

        var request = new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Single repo, no related",
            RepositoryId = primary,
            RequestedEffort = TaskEffortModes.Quick,
        };

        var run = await LoadRunAsync((await LaunchAsync(request)).RunId);

        HasRelatedRepositoriesKey(run.DefinitionSnapshotJson!).ShouldBeFalse(
            "a single-repo launch must NOT add a relatedRepositories key — the frozen agent.code inputs are byte-identical to the pre-multi-repo launch");
        run.ScopeRepositoryIds.ShouldBe(new[] { primary }, ignoreOrder: true, customMessage: "the scope is just the primary — byte-identical");
    }

    [Fact]
    public async Task A_related_repo_equal_to_the_primary_is_deduped_and_the_launch_succeeds()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var primary = await SeedRepositoryAsync(teamId);

        // The operator double-picks the primary as a related repo. It is the team's OWN repo, so the launch must NOT
        // reject (not a tenancy issue) and must NOT scope/clone it twice — the workspace resolver collapses it to one
        // (unit-pinned on FromAuthoredRepos). Here: the end-to-end launch tolerates it + the scope is deduped.
        var request = new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Primary also listed as related",
            RepositoryId = primary,
            RelatedRepositories = new[] { new TaskRelatedRepository { RepositoryId = primary, Alias = "dup" } },
            RequestedEffort = TaskEffortModes.Quick,
        };

        var run = await LoadRunAsync((await LaunchAsync(request)).RunId);

        run.ScopeRepositoryIds.ShouldBe(new[] { primary }, ignoreOrder: true, customMessage: "a related repo equal to the primary is deduped — the repo is scoped (and cloned) once, not twice");
    }

    [Theory]
    [InlineData(true)]   // the soft-deleted repo is the PRIMARY
    [InlineData(false)]  // the soft-deleted repo is a RELATED repo
    public async Task A_soft_deleted_repo_is_rejected_fail_closed(bool deletedIsPrimary)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var live = await SeedRepositoryAsync(teamId);
        var deleted = await SeedRepositoryAsync(teamId);
        await SoftDeleteRepositoryAsync(deleted);

        // The fail-closed guard (DeletedDate == null) must reject a soft-deleted in-team repo as either the primary OR
        // a related repo — a deleted repo can no more reach the workspace than a foreign one.
        var request = new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Target a deleted repo",
            RepositoryId = deletedIsPrimary ? deleted : live,
            RelatedRepositories = deletedIsPrimary ? null : new[] { new TaskRelatedRepository { RepositoryId = deleted, Alias = "api" } },
            RequestedEffort = TaskEffortModes.Quick,
        };

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => LaunchAsync(request));

        ex.Message.ShouldContain("not found or not accessible", customMessage: "a soft-deleted repo surfaces as a generic not-found — the fail-closed DeletedDate guard");
        (await CountRunsForTeamAsync(teamId)).ShouldBe(0, "a soft-deleted repo rejects BEFORE any run is created");
    }

    [Fact]
    public async Task Related_repositories_without_a_primary_are_rejected_fail_loud_with_no_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var related = await SeedRepositoryAsync(teamId);   // in-team so the team-scope gate passes — the related-without-primary guard is what must fire

        // Related repos have nowhere to anchor without a primary. The launch must reject fail-loud (ArgumentException
        // from BuildAgentProfile, which runs BEFORE the session opens) — proving the guard is reached in the real
        // pipeline and leaves no orphan run/session.
        var request = new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = userId,
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "Related repos but no primary",
            RepositoryId = null,
            RelatedRepositories = new[] { new TaskRelatedRepository { RepositoryId = related, Alias = "api" } },
            RequestedEffort = TaskEffortModes.Quick,
        };

        var ex = await Should.ThrowAsync<ArgumentException>(() => LaunchAsync(request));

        ex.Message.ShouldContain("require a primary repository");
        (await CountRunsForTeamAsync(teamId)).ShouldBe(0, "the related-without-primary guard fires before the session opens — no orphan run/session");
    }

    /// <summary>A test-only launch surface proving zero-core-edit extensibility: derives the goal + a LinkedEntityRef ENTIRELY from the surface payload the core never reads.</summary>
    private sealed class FakeSurfaceSeedProvider : ITaskLaunchSeedProvider
    {
        public const string Surface = "fake-surface";

        public string SurfaceKind => Surface;

        public Task<TaskLaunchSeed> SeedAsync(TaskLaunchRequest request, CancellationToken cancellationToken)
        {
            var raw = request.SurfacePayload[Surface];
            var goal = raw.GetProperty("goal").GetString()!;
            var entityId = raw.GetProperty("entityId").GetString()!;

            return Task.FromResult(new TaskLaunchSeed
            {
                Goal = goal,
                SurfaceKind = Surface,
                TeamId = request.TeamId,
                LinkedEntity = new LinkedEntityRef { EntityKind = "issue", EntityId = entityId },
            });
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskLaunchService>().LaunchAsync(request, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<Core.Services.Workflows.Engine.IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
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

    private async Task<AgentRun> LoadAgentRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == runId);
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

    private async Task<int> CountRunsForTeamAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().CountAsync(r => r.TeamId == teamId);
    }

    /// <summary>Seeds a provider instance + an active repository in the given team — enough to satisfy the launch service's team-scope check.</summary>
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

    /// <summary>Soft-deletes a seeded repo (sets <c>DeletedDate</c>) so the launch's fail-closed <c>DeletedDate == null</c> gate must reject it.</summary>
    private async Task SoftDeleteRepositoryAsync(Guid repoId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var repo = await db.Repository.SingleAsync(r => r.Id == repoId);
        repo.DeletedDate = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();
    }

    /// <summary>Reads the projected agent.code node's bound <c>repositoryId</c> input out of the frozen definition snapshot.</summary>
    private static string? ReadAgentRepositoryId(string definitionSnapshotJson)
    {
        var root = JsonDocument.Parse(definitionSnapshotJson).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");

        return agent.GetProperty("inputs").TryGetProperty("repositoryId", out var repo) ? repo.GetString() : null;
    }

    /// <summary>Reads the projected agent.code node's <c>relatedRepositories</c> input (id + alias + access per entry) out of the frozen snapshot, in authored order. Empty when the key is absent.</summary>
    private static IReadOnlyList<(string repoId, string? alias, string? access)> ReadAgentRelatedRepositories(string definitionSnapshotJson)
    {
        var root = JsonDocument.Parse(definitionSnapshotJson).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");

        if (!agent.GetProperty("inputs").TryGetProperty("relatedRepositories", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];

        return arr.EnumerateArray().Select(e => (
            e.GetProperty("repositoryId").GetString()!,
            e.TryGetProperty("alias", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null,
            e.TryGetProperty("access", out var ac) && ac.ValueKind == JsonValueKind.String ? ac.GetString() : null)).ToList();
    }

    /// <summary>True when the frozen agent.code node carries a <c>relatedRepositories</c> input key at all (the byte-identity guard for a single-repo launch).</summary>
    private static bool HasRelatedRepositoriesKey(string definitionSnapshotJson)
    {
        var root = JsonDocument.Parse(definitionSnapshotJson).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");

        return agent.GetProperty("inputs").TryGetProperty("relatedRepositories", out _);
    }
}
