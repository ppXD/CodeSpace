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
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): DC-3 — a supervisor turn's TERMINAL output (<see cref="SupervisorTurnResult.IntegratedBranch"/> /
/// <see cref="SupervisorTurnResult.RepositoryBranches"/>) correctly surfaces a P0-5 LEDGER-DIRECT publication (run
/// 96695645's own motivating scenario — a single accepted agent's own pushed <c>PublishManifest</c> row, no merge
/// ever ran) driven through the REAL <see cref="SupervisorTurnService"/>, PRE-TERMINAL (the run is never stamped
/// terminal in this test — a live turn's own context is exactly what a stop decision executes against). Before this
/// fix, <c>AgentSupervisorNode.Finish</c>'s output bag (and any downstream <c>git.open_pr</c> / <c>git.open_change_set</c>
/// node wired to it) was silently blind to this run class entirely.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorLedgerDirectTerminalOutputFlowTests
{
    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    private readonly PostgresFixture _fixture;

    public SupervisorLedgerDirectTerminalOutputFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_ledger_direct_published_stop_surfaces_the_branch_as_IntegratedBranch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, repoId, "codespace/agent/fix");

        var result = await RunTurnAsync(runId, teamId, new AlwaysStopDecider());

        result.IsFinished.ShouldBeTrue();
        result.IntegratedBranch.ShouldBe("codespace/agent/fix", "the ledger-direct branch must surface even though no merge ever ran and the run was never stamped terminal");
        result.RepositoryBranches.ShouldBeEmpty("a single resolved branch surfaces as IntegratedBranch, not the multi-repo shape");
    }

    [Fact]
    public async Task A_merge_derived_stop_is_unaffected_byte_identical_to_pre_dc3()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1");

        var result = await RunTurnAsync(runId, teamId, new AlwaysStopDecider());

        result.IntegratedBranch.ShouldBe("codespace/integration/run/turn1", "the existing merge-derived path is untouched — the enrichment never overrides a genuine result");
    }

    [Fact]
    public async Task A_ledger_direct_multi_repo_publication_surfaces_as_RepositoryBranches()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var webRepoId = await SeedBoundRepositoryAsync(teamId);
        var apiRepoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, webRepoId, "codespace/agent/web", alias: "web");
        await SeedAgentManifestAsync(runId, teamId, agentRunId, apiRepoId, "codespace/agent/api", alias: "api");

        var result = await RunTurnAsync(runId, teamId, new AlwaysStopDecider());

        result.IntegratedBranch.ShouldBeNull();
        result.RepositoryBranches.Count.ShouldBe(2);
        result.RepositoryBranches.Select(b => b.Alias).ShouldBe(new[] { "web", "api" }, ignoreOrder: true);
    }

    [Fact]
    public async Task A_withheld_acceptance_failed_stop_never_surfaces_a_branch_via_the_new_enrichment()
    {
        // The withhold invariant (L4 P1): a FAILED acceptance grade must withhold the reviewable branch — the NEW
        // enrichment step must respect the SAME AcceptancePassed==false flag BuildResult already computes, never
        // resurrecting a branch behind it. Driven via the MERGE-DERIVED path deliberately: acceptance grading's own
        // target resolution (SupervisorTurnService.Rehydrate.ResolveAcceptanceTargets) has the identical
        // ledger-direct blindness this slice fixes for the 4 named readers, but fixing ResolveAcceptanceTargets
        // itself is a separate, not-yet-scoped concern — this test isolates the ENRICHMENT's own withhold-respecting
        // guard from that adjacent gap.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1");

        // ResolveAcceptanceTargets (the grading target resolver) needs context.AgentProfile?.RepositoryId to clone
        // the head + run the floor against it — set here so grading genuinely RUNS (and genuinely fails).
        var goalConfig = new SupervisorGoalConfig { Goal = Goal, AcceptanceChecks = new[] { "sh", "-c", "exit 1" }, AgentProfile = new SupervisorAgentProfile { RepositoryId = repoId } };

        var result = await RunTurnAsync(runId, teamId, new AlwaysStopDecider(), goalConfig);

        result.AcceptancePassed.ShouldBe(false);
        result.IntegratedBranch.ShouldBeNull("a withheld stop must never surface a branch — the enrichment's guard checks the SAME flag BuildResult already computed");
        result.RepositoryBranches.ShouldBeEmpty();
    }

    // ─── Drive a real turn ─────────────────────────────────────────────────────────

    private async Task<SupervisorTurnResult> RunTurnAsync(Guid runId, Guid teamId, ISupervisorDecider decider, SupervisorGoalConfig? goalConfig = null)
    {
        using var scope = _fixture.BeginScope();
        var service = NewTurnService(scope, decider);
        return await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, goalConfig ?? new SupervisorGoalConfig { Goal = Goal }, CancellationToken.None);
    }

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
        scope.Resolve<ISupervisorPublishedBranchResolver>(),
        scope.Resolve<ILogger<SupervisorTurnService>>());

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
            Name = "sup-ledger-terminal-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

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

    private async Task SeedSpawnAsync(Guid runId, Guid teamId, Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var result = new SupervisorAgentResult { AgentRunId = agentRunId, Status = "Succeeded", ChangedFiles = new[] { "a.txt" } };
        var outcome = JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);

        await AddTerminalDecisionAsync(db, runId, teamId, SupervisorDecisionKinds.Spawn, outcome);
    }

    private async Task SeedAgentManifestAsync(Guid runId, Guid teamId, Guid agentRunId, Guid repositoryId, string branch, string alias = "primary")
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IPublishManifestStore>().UpsertForAgentRunAsync(agentRunId, new PublishManifestUpsert
        {
            TeamId = teamId, WorkflowRunId = runId, RepositoryAlias = alias, RepositoryId = repositoryId,
            Branch = branch, ChangedFileCount = 1, PublishStateValue = PublishState.Pushed,
        }, CancellationToken.None);
    }

    private async Task SeedSingleRepoMergeAsync(Guid runId, Guid teamId, string integratedBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var outcome = JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch, appliedCount = 1, reason = (string?)null, excludedAgents = Array.Empty<string>() } }, AgentJson.Options);

        await AddTerminalDecisionAsync(db, runId, teamId, SupervisorDecisionKinds.Merge, outcome);
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
}
