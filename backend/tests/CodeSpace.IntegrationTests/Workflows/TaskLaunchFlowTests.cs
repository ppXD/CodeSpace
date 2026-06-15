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
        // single-agent, and starts the snapshot run. (Explicit standard/deep now route the multi-agent map-fanout
        // shape — covered by PlanMapSynthFanoutFlowTests; this single-agent E2E pins the quick tier.)
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

    /// <summary>Reads the projected agent.code node's bound <c>repositoryId</c> input out of the frozen definition snapshot.</summary>
    private static string? ReadAgentRepositoryId(string definitionSnapshotJson)
    {
        var root = JsonDocument.Parse(definitionSnapshotJson).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");

        return agent.GetProperty("inputs").TryGetProperty("repositoryId", out var repo) ? repo.GetString() : null;
    }
}
