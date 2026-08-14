using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟡 Medium-mock (real Postgres sourcing + real manifest store, faked git): <c>git.integrate_run</c>'s
/// run-sourced derivation over REAL rows — the node reads the run's own AgentRun + publish-manifest ledgers,
/// hands the integrator a deterministic contribution set, and a CLEAN integration records the run-level
/// <c>Integration</c> manifest row (the durable "unique integrated candidate" fact). Git itself is faked here:
/// the integrator core has its own coverage, and the real-git arc is the supervisor whole-loop E2E's job.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class GitIntegrateRunNodeFlowTests
{
    private readonly PostgresFixture _fixture;

    public GitIntegrateRunNodeFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Derives_the_runs_contributions_and_records_the_integrated_candidate()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var repositoryId = Guid.NewGuid();

        var first = await SeedAgentRunAsync(teamId, runId, "map#0", minutesAgo: 9);
        var second = await SeedAgentRunAsync(teamId, runId, "map#1", minutesAgo: 3);
        await SeedAgentManifestAsync(teamId, runId, first, repositoryId, branch: "codespace/agent/a");
        await SeedAgentManifestAsync(teamId, runId, second, repositoryId, branch: "codespace/agent/b");

        using var scope = _fixture.BeginScope();
        var integrator = new RecordingIntegrator
        {
            Result = IntegrationResult.Build(IntegrationStatus.Clean, $"codespace/integration/{runId:N}", new[]
            {
                new ContributionOutcome { Label = "agent#map#0", Disposition = ContributionDisposition.Applied },
                new ContributionOutcome { Label = "agent#map#1", Disposition = ContributionDisposition.Applied },
            }),
        };
        var node = new GitIntegrateRunNode(integrator, new StubResolver(), scope.Resolve<IPublishManifestStore>(), scope.Resolve<CodeSpaceDbContext>());

        var result = await node.RunAsync(Context(repositoryId, teamId, runId), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["status"].GetString().ShouldBe("Clean");
        result.Outputs["integratedBranch"].GetString().ShouldBe($"codespace/integration/{runId:N}");

        integrator.LastRequest.ShouldNotBeNull();
        integrator.LastRequest!.BaseSha.ShouldBe("b1");
        integrator.LastRequest.IntegrationBranch.ShouldBe($"codespace/integration/{runId:N}");
        integrator.LastRequest.Contributions.Select(c => c.Label).ShouldBe(new[] { "agent#map#0", "agent#map#1" },
            customMessage: "contributions apply in agent-run creation order — the deterministic apply order re-runs reproduce");

        var manifests = await scope.Resolve<IPublishManifestStore>().ListForWorkflowRunAsync(runId, teamId, CancellationToken.None);
        var candidate = manifests.Where(m => m.Kind == PublishManifestKind.Integration).ShouldHaveSingleItem(
            customMessage: "a clean integration must record the run-level Integration manifest row — the durable candidate fact");
        candidate.Branch.ShouldBe($"codespace/integration/{runId:N}");
        candidate.RepositoryId.ShouldBe(repositoryId);
        candidate.BaseSha.ShouldBe("b1");
        candidate.PublishStateValue.ShouldBe(PublishState.Pushed);
    }

    [Fact]
    public async Task A_run_that_produced_nothing_integrable_skips_without_touching_git()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        using var scope = _fixture.BeginScope();
        var integrator = new RecordingIntegrator();
        var node = new GitIntegrateRunNode(integrator, new StubResolver(), scope.Resolve<IPublishManifestStore>(), scope.Resolve<CodeSpaceDbContext>());

        var result = await node.RunAsync(Context(Guid.NewGuid(), teamId, runId), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, "an empty fan-out is a routable Skipped outcome, never a failure");
        result.Outputs["status"].GetString().ShouldBe("Skipped");
        integrator.Calls.ShouldBe(0, "no contributions ⇒ no clone, no push, no workspace resolution");

        (await scope.Resolve<IPublishManifestStore>().ListForWorkflowRunAsync(runId, teamId, CancellationToken.None))
            .ShouldNotContain(m => m.Kind == PublishManifestKind.Integration, "nothing integrated ⇒ no candidate row");
    }

    // ─── P4 "conflict ⇒ park": the opt-in approval park + the re-integrate resume ───

    [Fact]
    public async Task A_conflicted_first_pass_parks_on_an_approval_wait_naming_the_conflict()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var repositoryId = Guid.NewGuid();
        var agentRunId = await SeedAgentRunAsync(teamId, runId, "map#0", minutesAgo: 5);
        await SeedAgentManifestAsync(teamId, runId, agentRunId, repositoryId, branch: "codespace/agent/a");

        using var scope = _fixture.BeginScope();
        var integrator = new RecordingIntegrator { Result = ConflictedResult() };
        var node = new GitIntegrateRunNode(integrator, new StubResolver(), scope.Resolve<IPublishManifestStore>(), scope.Resolve<CodeSpaceDbContext>());

        var result = await node.RunAsync(Context(repositoryId, teamId, runId, parkOnConflict: true), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended, "a conflicted candidate must park for a human, never narrate past silently");
        result.SuspendUntil!.Kind.ShouldBe(CodeSpace.Messages.Constants.WorkflowWaitKinds.Approval);
        result.SuspendUntil.Payload.GetProperty("conflictedFiles")[0].GetString().ShouldBe("shared.txt");
        result.SuspendUntil.Payload.GetProperty("fallbackBranches")[0].GetString().ShouldBe("codespace/agent/b");
        result.SuspendUntil.Payload.GetProperty("prompt").GetString()!.ShouldContain("approve to retry");
    }

    [Fact]
    public async Task An_approved_resume_re_integrates_and_a_repaired_world_lands_the_clean_candidate()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var repositoryId = Guid.NewGuid();
        var agentRunId = await SeedAgentRunAsync(teamId, runId, "map#0", minutesAgo: 5);
        await SeedAgentManifestAsync(teamId, runId, agentRunId, repositoryId, branch: "codespace/agent/a");

        using var scope = _fixture.BeginScope();
        var integrator = new RecordingIntegrator
        {
            Result = IntegrationResult.Build(IntegrationStatus.Clean, $"codespace/integration/{runId:N}", new[]
            {
                new ContributionOutcome { Label = "agent#map#0", Disposition = ContributionDisposition.Applied },
            }),
        };
        var node = new GitIntegrateRunNode(integrator, new StubResolver(), scope.Resolve<IPublishManifestStore>(), scope.Resolve<CodeSpaceDbContext>());

        var result = await node.RunAsync(Context(repositoryId, teamId, runId, parkOnConflict: true, resumePayload: """{"approved":true,"comment":"pushed a fix","by":"user-1"}"""), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, "the resumed pass re-integrates — the human's repair made it clean");
        result.Outputs["status"].GetString().ShouldBe("Clean");
        result.Outputs["reviewApproved"].GetBoolean().ShouldBeTrue();
        result.Outputs["reviewedBy"].GetString().ShouldBe("user-1");
        integrator.Calls.ShouldBe(1, "the resumed pass really re-derived and re-integrated");

        (await scope.Resolve<IPublishManifestStore>().ListForWorkflowRunAsync(runId, teamId, CancellationToken.None))
            .Where(m => m.Kind == PublishManifestKind.Integration).ShouldHaveSingleItem("the repaired candidate records its row exactly like a first-pass clean");
    }

    [Fact]
    public async Task A_still_conflicted_resume_completes_honestly_with_the_review_trail_never_a_loop()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var repositoryId = Guid.NewGuid();
        var agentRunId = await SeedAgentRunAsync(teamId, runId, "map#0", minutesAgo: 5);
        await SeedAgentManifestAsync(teamId, runId, agentRunId, repositoryId, branch: "codespace/agent/a");

        using var scope = _fixture.BeginScope();
        var integrator = new RecordingIntegrator { Result = ConflictedResult() };
        var node = new GitIntegrateRunNode(integrator, new StubResolver(), scope.Resolve<IPublishManifestStore>(), scope.Resolve<CodeSpaceDbContext>());

        var result = await node.RunAsync(Context(repositoryId, teamId, runId, parkOnConflict: true, resumePayload: """{"approved":false,"comment":"ship the fragments","by":"user-2"}"""), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, "one park per run — a still-conflicted retry completes honestly instead of looping");
        result.Outputs["status"].GetString().ShouldBe("Conflicted");
        result.Outputs["reviewApproved"].GetBoolean().ShouldBeFalse();
        result.Outputs["reviewComment"].GetString().ShouldBe("ship the fragments");

        (await scope.Resolve<IPublishManifestStore>().ListForWorkflowRunAsync(runId, teamId, CancellationToken.None))
            .ShouldNotContain(m => m.Kind == PublishManifestKind.Integration, "no clean candidate ⇒ no candidate row");
    }

    [Fact]
    public async Task Without_the_opt_in_a_conflict_stays_a_routable_outcome()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var repositoryId = Guid.NewGuid();
        var agentRunId = await SeedAgentRunAsync(teamId, runId, "map#0", minutesAgo: 5);
        await SeedAgentManifestAsync(teamId, runId, agentRunId, repositoryId, branch: "codespace/agent/a");

        using var scope = _fixture.BeginScope();
        var node = new GitIntegrateRunNode(new RecordingIntegrator { Result = ConflictedResult() }, new StubResolver(), scope.Resolve<IPublishManifestStore>(), scope.Resolve<CodeSpaceDbContext>());

        var result = await node.RunAsync(Context(repositoryId, teamId, runId), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, "an authored graph without the opt-in keeps the conflict a branchable outcome — byte-identical to before the park existed");
        result.Outputs["status"].GetString().ShouldBe("Conflicted");
    }

    private static IntegrationResult ConflictedResult() =>
        IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[]
        {
            new ContributionOutcome { Label = "agent#map#0", Disposition = ContributionDisposition.Applied },
            new ContributionOutcome { Label = "agent#map#1", Disposition = ContributionDisposition.Conflicted, ConflictedFiles = new[] { "shared.txt" }, FallbackBranch = "codespace/agent/b", Reason = "textual conflict" },
        }, "a contribution conflicted while integrating");

    // ─── Seeds ──────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedRunAsync(Guid teamId, Guid userId)
    {
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
            {
                Name = "integrate-" + Guid.NewGuid().ToString("N")[..6],
                Description = null,
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> SeedAgentRunAsync(Guid teamId, Guid runId, string iterationKey, int minutesAgo)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);

        db.AgentRun.Add(new AgentRun
        {
            Id = id, TeamId = teamId, WorkflowRunId = runId, NodeId = "agent", IterationKey = iterationKey,
            Harness = "codex-cli", Status = AgentRunStatus.Succeeded, TaskJson = "{}",
            ResultJson = JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Patch = $"diff --git a/{iterationKey} b/{iterationKey}\n" }, Core.Services.Agents.AgentJson.Options),
            CreatedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedDate = at, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedAgentManifestAsync(Guid teamId, Guid runId, Guid agentRunId, Guid repositoryId, string branch)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IPublishManifestStore>().UpsertForAgentRunAsync(agentRunId, new PublishManifestUpsert
        {
            TeamId = teamId, WorkflowRunId = runId, RepositoryId = repositoryId, RepositoryAlias = "primary",
            BaseSha = "b1", Branch = branch, PublishStateValue = PublishState.Pushed,
        }, CancellationToken.None);
    }

    private static NodeRunContext Context(Guid repositoryId, Guid teamId, Guid runId, bool parkOnConflict = false, string? resumePayload = null) => new()
    {
        Inputs = new Dictionary<string, JsonElement> { ["repositoryId"] = JsonSerializer.SerializeToElement(repositoryId.ToString()) },
        Config = parkOnConflict ? new Dictionary<string, JsonElement> { ["parkOnConflict"] = JsonSerializer.SerializeToElement(true) } : new Dictionary<string, JsonElement>(),
        ResumePayload = resumePayload is null ? null : JsonDocument.Parse(resumePayload).RootElement.Clone(),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope
        {
            Trigger = new Dictionary<string, JsonElement>(),
            Sys = new Dictionary<string, JsonElement>
            {
                [SystemScopeKeys.TeamId] = JsonSerializer.SerializeToElement(teamId.ToString()),
                [SystemScopeKeys.WorkflowRunId] = JsonSerializer.SerializeToElement(runId.ToString()),
            },
        },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
    };

    private sealed class RecordingIntegrator : IBranchIntegrator
    {
        public IntegrationResult? Result;
        public int Calls;
        public IntegrationRequest? LastRequest;

        public string Kind => "local";

        public Task<IntegrationResult> IntegrateAsync(IntegrationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(Result ?? IntegrationResult.Build(IntegrationStatus.Empty, null, Array.Empty<ContributionOutcome>()));
        }
    }

    private sealed class StubResolver : IAgentWorkspaceResolver
    {
        public Task<WorkspaceProvisionRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null, bool softFallback = false, string? pinnedSha = null) =>
            Task.FromResult<WorkspaceRequest?>(new WorkspaceRequest { RepositoryUrl = "file:///remote.git", Ref = @ref ?? "main", Token = "tok", TokenUsername = "x-access-token" });
    }
}
