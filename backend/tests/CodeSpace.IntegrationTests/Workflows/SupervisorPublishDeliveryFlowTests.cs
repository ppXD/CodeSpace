using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): DC-2 (deliver-at-stop) — a run whose <see cref="SupervisorGoalConfig.DeliverySpec"/>
/// requires an opened pull request, driven through the REAL <see cref="SupervisorTurnService"/> +
/// <see cref="SupervisorPublishGate"/> + <see cref="Executors.RealSupervisorActionExecutor"/> against real Postgres —
/// the server-authored <c>publish</c> decision genuinely opens a PR through the test container's fake
/// <c>ProviderKind.Git</c> write capability and records it onto <c>PublishManifest</c>, then the NEXT turn's stop
/// attempt reads that outcome back and proceeds. Covers BOTH publication paths (merge-derived and, run 96695645's
/// own motivating scenario, the P0-5 ledger-direct one) plus the "attempted and failed" park.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorPublishDeliveryFlowTests
{
    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    private readonly PostgresFixture _fixture;

    public SupervisorPublishDeliveryFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_ledger_direct_published_run_auto_opens_a_pr_then_the_next_stop_proceeds()
    {
        // Run 96695645's own motivating scenario: a single accepted agent already pushed to a manifest row, no
        // merge/integration decision ever ran. Before DC-2 this run succeeded with NO PR and no way to get one.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, repoId, "codespace/agent/fix");

        var goalConfig = new SupervisorGoalConfig { Goal = Goal, DeliverySpec = new DeliverySpec { OpenPullRequest = true } };
        var decider = new AlwaysStopDecider();

        var first = await RunTurnAsync(runId, teamId, decider, goalConfig);

        first.IsFinished.ShouldBeFalse("the gate substituted stop for a server-authored publish — the run is not done yet");
        first.DecisionKind.ShouldBe(SupervisorDecisionKinds.Publish);

        var manifest = await IntegrationManifestRowAsync(runId, teamId);
        manifest.PullRequestNumber.ShouldBe(777, "the test container's fake write capability always returns a fixed number");
        manifest.Branch.ShouldBe("codespace/agent/fix");

        var second = await RunTurnAsync(runId, teamId, decider, goalConfig);

        second.IsFinished.ShouldBeTrue("the publish attempt succeeded — the model's own stop now proceeds");
        second.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
    }

    [Fact]
    public async Task A_merge_derived_published_run_also_auto_opens_a_pr()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1");
        await StampTerminalRepositoryIdAsync(runId, repoId);

        var goalConfig = new SupervisorGoalConfig { Goal = Goal, DeliverySpec = new DeliverySpec { OpenPullRequest = true } };
        var decider = new AlwaysStopDecider();

        var first = await RunTurnAsync(runId, teamId, decider, goalConfig);
        first.DecisionKind.ShouldBe(SupervisorDecisionKinds.Publish);

        var manifest = await IntegrationManifestRowAsync(runId, teamId);
        manifest.PullRequestNumber.ShouldBe(777);
        manifest.Branch.ShouldBe("codespace/integration/run/turn1");

        var second = await RunTurnAsync(runId, teamId, decider, goalConfig);
        second.IsFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task A_crash_recovery_replay_of_the_publish_decision_never_opens_a_second_pr()
    {
        // Sweep-found defect: ExecutePublishAsync had no idempotency guard against SupervisorTurnService's generic
        // crash-recovery path (ExecuteUnderClaimAsync re-executes ANY decision left Running after a crash, since a
        // lost begin-CAS means a prior walk crashed after the side effect but before RecordTerminalAsync). Opening
        // a PR is a resource-creating side effect, unlike merge's idempotent git push — a naive re-execution would
        // open a SECOND, duplicate PR. Xmin is the proof: ANY write (even one that lands the identical values)
        // bumps it, so an UNCHANGED Xmin after the replay proves the fix's short-circuit genuinely skipped writing,
        // not merely that the fake write capability's fixed return value happened to look the same.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1");
        await StampTerminalRepositoryIdAsync(runId, repoId);

        var goalConfig = new SupervisorGoalConfig { Goal = Goal, DeliverySpec = new DeliverySpec { OpenPullRequest = true } };
        var decider = new AlwaysStopDecider();

        var first = await RunTurnAsync(runId, teamId, decider, goalConfig);
        first.DecisionKind.ShouldBe(SupervisorDecisionKinds.Publish);

        var afterFirstRun = await IntegrationManifestRowAsync(runId, teamId);
        afterFirstRun.PullRequestNumber.ShouldBe(777);
        var xminAfterFirstOpen = afterFirstRun.Xmin;

        // Simulate the crash: the publish decision's row is flipped back to Running (as if a prior walk crashed
        // right after the real provider call succeeded but before RecordTerminalAsync could persist the terminal
        // outcome) — the NEXT RunTurnAsync call sees it as InFlight and re-enters ExecuteUnderClaimAsync's
        // crash-recovery path, re-executing ExecutePublishAsync from scratch.
        await FlipLatestDecisionBackToRunningAsync(runId, teamId, SupervisorDecisionKinds.Publish);

        var second = await RunTurnAsync(runId, teamId, decider, goalConfig);
        second.DecisionKind.ShouldBe(SupervisorDecisionKinds.Publish);

        var afterReplay = await IntegrationManifestRowAsync(runId, teamId);
        afterReplay.PullRequestNumber.ShouldBe(777, "the pre-existing PR is reused, never re-opened with a fresh call");
        afterReplay.Xmin.ShouldBe(xminAfterFirstOpen, "an UNCHANGED Xmin proves the replay's idempotency check skipped writing the manifest a second time");

        (await ManifestRowCountAsync(runId, teamId)).ShouldBe(1, "no duplicate manifest row for the same alias");
    }

    [Fact]
    public async Task A_run_with_no_delivery_contract_never_opens_a_pr_byte_identical_to_pre_dc2()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1");
        await StampTerminalRepositoryIdAsync(runId, repoId);

        var result = await RunTurnAsync(runId, teamId, new AlwaysStopDecider(), goalConfig: new SupervisorGoalConfig { Goal = Goal });

        result.IsFinished.ShouldBeTrue("no delivery contract at all — the stop proceeds on the very first turn, exactly as before DC-2");
        result.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);

        (await ManifestRowCountAsync(runId, teamId)).ShouldBe(0, "nothing asked for a PR — nothing was opened");
    }

    [Fact]
    public async Task A_failed_publish_attempt_parks_to_ask_human_instead_of_completing_silently()
    {
        // A manifest row pointing at a repository id that does not exist in this team's catalog — IPullRequestService
        // fails loud (repo not found), ChangeSetService isolates it to a Failed disposition, and the NEXT stop attempt
        // must see that failure and park rather than letting the run complete as if delivery had succeeded.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var missingRepoId = Guid.NewGuid();

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, missingRepoId, "codespace/agent/fix");

        var goalConfig = new SupervisorGoalConfig { Goal = Goal, DeliverySpec = new DeliverySpec { OpenPullRequest = true } };
        var decider = new AlwaysStopDecider();

        var first = await RunTurnAsync(runId, teamId, decider, goalConfig);
        first.DecisionKind.ShouldBe(SupervisorDecisionKinds.Publish);

        (await ManifestRowCountAsync(runId, teamId)).ShouldBe(0, "the attempt failed — nothing genuinely opened, nothing recorded");

        var second = await RunTurnAsync(runId, teamId, decider, goalConfig);

        second.IsFinished.ShouldBeFalse("the delivery contract could not be satisfied — the run must not silently complete");
        second.DecisionKind.ShouldBe(SupervisorDecisionKinds.AskHuman);

        using var verify = _fixture.BeginScope();
        var latest = await verify.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderByDescending(d => d.Sequence).FirstAsync();

        var question = JsonDocument.Parse(latest.PayloadJson).RootElement.GetProperty("question").GetString()!;
        question.ShouldContain("could not be opened", Case.Insensitive);
    }

    // ─── Drive a real turn ─────────────────────────────────────────────────────────

    private async Task<Messages.Agents.SupervisorTurnResult> RunTurnAsync(Guid runId, Guid teamId, ISupervisorDecider decider, SupervisorGoalConfig goalConfig)
    {
        using var scope = _fixture.BeginScope();
        var service = NewTurnService(scope, decider);
        return await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, goalConfig, CancellationToken.None);
    }

    /// <summary>The REAL <see cref="SupervisorTurnService"/>, every dependency resolved from the scope except the decider (scripted per test) — mirrors <c>SupervisorPlanDeliveryFlowTests.NewTurnService</c>.</summary>
    private static SupervisorTurnService NewTurnService(ILifetimeScope scope, ISupervisorDecider decider) => new(
        scope.Resolve<ISupervisorDecisionLog>(),
        decider,
        scope.Resolve<ISupervisorActionExecutor>(),
        scope.Resolve<CodeSpaceDbContext>(),
        scope.Resolve<ISupervisorAcceptanceGrader>(),
        scope.Resolve<Core.Services.Decisions.IDecisionQueueService>(),
        scope.Resolve<Core.Services.Supervisor.Arbiter.IDecisionArbiter>(),
        scope.Resolve<Core.Services.Decisions.IDecisionAnswerService>(),
        scope.Resolve<Core.Services.Plans.IWorkPlanService>(),
        scope.Resolve<Core.Services.Workflows.Lifecycle.IRunRecordLogger>(),
        scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
        scope.Resolve<IPublishManifestStore>(),
        scope.Resolve<ILogger<SupervisorTurnService>>());

    /// <summary>A decider that always stops with a fixed summary — DC-2's own mechanism (the gate + the publish executor) is entirely server-side, so the decider's only job here is to keep proposing the SAME stop every turn.</summary>
    private sealed class AlwaysStopDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Stop,
                PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "completed", Summary = "shipped the fix" }, AgentJson.Options),
            });
    }

    // ─── Seeding ────────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        var workflowId = await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-publish-delivery-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json($$"""{"goal":"{{Goal}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition> { new() { From = "start", To = NodeId }, new() { From = NodeId, To = "end" } },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    /// <summary>Simulates a crash right after a decision's real side effect succeeded but before <c>RecordTerminalAsync</c> persisted the terminal outcome — flips the run's LATEST decision of the given kind back to <c>Running</c>, so the next <c>RunTurnAsync</c> call sees it as InFlight and re-enters the crash-recovery replay path.</summary>
    private async Task FlipLatestDecisionBackToRunningAsync(Guid runId, Guid teamId, string decisionKind)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var row = await db.SupervisorDecisionRecord
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == decisionKind)
            .OrderByDescending(d => d.Sequence).FirstAsync();

        row.Status = SupervisorDecisionStatus.Running;
        await db.SaveChangesAsync();
    }

    /// <summary>Registered under <c>ProviderKind.Git</c> — the test container's fake write capability (fixed PR number 777).</summary>
    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "local", BaseUrl = $"https://local-{suffix}" });

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new PatPayload { Token = "integration-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = $"ext-{suffix}", NamespacePath = "org", Name = "repo", FullPath = $"org/repo-{suffix}",
            DefaultBranch = "main", WebUrl = $"https://local-{suffix}/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private async Task SeedSingleRepoMergeAsync(Guid runId, Guid teamId, string integratedBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var outcome = JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch, appliedCount = 1, reason = (string?)null, excludedAgents = Array.Empty<string>() } }, AgentJson.Options);

        await AddTerminalDecisionAsync(db, runId, teamId, SupervisorDecisionKinds.Merge, outcome);
    }

    private async Task SeedSpawnAsync(Guid runId, Guid teamId, Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var result = new SupervisorAgentResult { AgentRunId = agentRunId, Status = "Succeeded", ChangedFiles = new[] { "a.txt" } };
        var outcome = JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);

        await AddTerminalDecisionAsync(db, runId, teamId, SupervisorDecisionKinds.Spawn, outcome);
    }

    private async Task SeedAgentManifestAsync(Guid runId, Guid teamId, Guid agentRunId, Guid repositoryId, string branch)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IPublishManifestStore>().UpsertForAgentRunAsync(agentRunId, new PublishManifestUpsert
        {
            TeamId = teamId, WorkflowRunId = runId, RepositoryAlias = "primary", RepositoryId = repositoryId,
            Branch = branch, ChangedFileCount = 1, PublishStateValue = PublishState.Pushed,
        }, CancellationToken.None);
    }

    private static async Task AddTerminalDecisionAsync(CodeSpaceDbContext db, Guid runId, Guid teamId, string decisionKind, string outcomeJson)
    {
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = decisionKind, IdempotencyKey = $"{decisionKind}-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcomeJson,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task StampTerminalRepositoryIdAsync(Guid runId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.OutputsJson = JsonSerializer.Serialize(new { repositoryId = repositoryId.ToString() });
        await db.SaveChangesAsync();
    }

    private async Task<PublishManifest> IntegrationManifestRowAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var rows = await scope.Resolve<IPublishManifestStore>().ListForWorkflowRunAsync(runId, teamId, CancellationToken.None);
        return rows.Single(m => m.Kind == PublishManifestKind.Integration);
    }

    private async Task<int> ManifestRowCountAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<IPublishManifestStore>().ListForWorkflowRunAsync(runId, teamId, CancellationToken.None)).Count(m => m.Kind == PublishManifestKind.Integration);
    }
}
