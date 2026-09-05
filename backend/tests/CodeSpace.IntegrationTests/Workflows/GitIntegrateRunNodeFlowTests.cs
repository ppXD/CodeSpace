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
///
/// <para>Includes the per-unit reduction over real rows, on BOTH sides of its lane fence. A map body's agent node
/// retries by RESPAWNING and a manifest row lands for every attempt regardless of how it ended, so the retried
/// branch used to hand the integrator the same unit twice; a supervisor instead stamps ONE turn cell on all K
/// agents of a turn, so those must all still reach the integrator. Both tests assert on the contribution set the
/// node HANDS the integrator — never on a status the fake was told to return: whether a duplicated unit then
/// conflicts under the sequential apply is the real integrator's own coverage, not something a fake can prove.</para>
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

        var first = await SeedAgentRunAsync(teamId, runId, new AgentRunSeed("map#0", MinutesAgo: 9));
        var second = await SeedAgentRunAsync(teamId, runId, new AgentRunSeed("map#1", MinutesAgo: 3));
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

    /// <summary>
    /// The anchor rule: the base the request carries is the ANCESTOR-MOST one the run recorded, not the first
    /// surviving contribution's. A producer whose definition-of-done rejected it is withheld from the head — it stops
    /// being a contribution while its manifest row keeps naming the commit the run started from — and its dependent,
    /// cut from that producer's head, is then the only thing left to anchor on. Anchoring there refuses any sibling
    /// still rooted at the repository base and checks out the rejected producer's head.
    ///
    /// <para>Mutation check: anchor on the first contribution's base again and the request carries "producer-head".</para>
    /// </summary>
    [Fact]
    public async Task Anchors_on_the_run_root_a_withheld_producer_recorded()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var repositoryId = Guid.NewGuid();

        var producer = await SeedAgentRunAsync(teamId, runId, new AgentRunSeed("map#0", MinutesAgo: 9));
        await SeedAgentManifestAsync(teamId, runId, producer, repositoryId, branch: "codespace/agent/producer", baseSha: "run-root", acceptance: PublishAcceptanceState.Failed);

        var dependent = await SeedAgentRunAsync(teamId, runId, new AgentRunSeed("map#1", MinutesAgo: 3));
        await SeedAgentManifestAsync(teamId, runId, dependent, repositoryId, branch: "codespace/agent/dependent", baseSha: "producer-head");

        using var scope = _fixture.BeginScope();
        var integrator = new RecordingIntegrator
        {
            Result = IntegrationResult.Build(IntegrationStatus.Clean, $"codespace/integration/{runId:N}", new[]
            {
                new ContributionOutcome { Label = "agent#map#1", Disposition = ContributionDisposition.Applied },
            }),
        };
        var node = new GitIntegrateRunNode(integrator, new StubResolver(), scope.Resolve<IPublishManifestStore>(), scope.Resolve<CodeSpaceDbContext>());

        var result = await node.RunAsync(Context(repositoryId, teamId, runId), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        integrator.LastRequest.ShouldNotBeNull();
        integrator.LastRequest!.Contributions.Select(c => c.Label).ShouldBe(new[] { "agent#map#1" },
            customMessage: "the withheld producer never reaches the reviewable candidate — that gate is what leaves the anchor unrecoverable from the contributions");
        integrator.LastRequest.BaseSha.ShouldBe("run-root",
            customMessage: "the ledger row the withheld producer left behind still names the run's root, and the anchor is read from there");
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
        var agentRunId = await SeedAgentRunAsync(teamId, runId, new AgentRunSeed("map#0", MinutesAgo: 5));
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
        var agentRunId = await SeedAgentRunAsync(teamId, runId, new AgentRunSeed("map#0", MinutesAgo: 5));
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
        var agentRunId = await SeedAgentRunAsync(teamId, runId, new AgentRunSeed("map#0", MinutesAgo: 5));
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
        var agentRunId = await SeedAgentRunAsync(teamId, runId, new AgentRunSeed("map#0", MinutesAgo: 5));
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

    // ─── The per-unit reduction over real rows, and the lane fence that bounds it ───

    [Fact]
    public async Task A_retried_subtask_hands_the_integrator_only_its_latest_attempt()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var repositoryId = Guid.NewGuid();

        var abandoned = await SeedAgentRunAsync(teamId, runId, new AgentRunSeed("map#0", MinutesAgo: 9, Patch: "diff --git a/shared.txt b/shared.txt\nhalf-done\n"));
        var respawned = await SeedAgentRunAsync(teamId, runId, new AgentRunSeed("map#0", MinutesAgo: 4, Patch: "diff --git a/shared.txt b/shared.txt\nfinished\n"));
        var sibling = await SeedAgentRunAsync(teamId, runId, new AgentRunSeed("map#1", MinutesAgo: 2));
        await SeedAgentManifestAsync(teamId, runId, abandoned, repositoryId, branch: "codespace/agent/a1");
        await SeedAgentManifestAsync(teamId, runId, respawned, repositoryId, branch: "codespace/agent/a2");
        await SeedAgentManifestAsync(teamId, runId, sibling, repositoryId, branch: "codespace/agent/b");

        using var scope = _fixture.BeginScope();
        var integrator = new RecordingIntegrator();
        var node = new GitIntegrateRunNode(integrator, new StubResolver(), scope.Resolve<IPublishManifestStore>(), scope.Resolve<CodeSpaceDbContext>());

        await node.RunAsync(Context(repositoryId, teamId, runId, parkOnConflict: true), CancellationToken.None);

        integrator.LastRequest!.Contributions.Select(c => c.Label).ShouldBe(new[] { "agent#map#0", "agent#map#1" },
            customMessage: "one contribution per (node, iteration) unit, still in agent-run creation order — the fan-out's sibling is a different unit and survives");
        integrator.LastRequest.Contributions.First().Patch.ShouldContain("finished", customMessage: "the UNSUPERSEDED attempt's bytes integrate, never the abandoned attempt's");
        integrator.LastRequest.Contributions.First().ProducedBranch.ShouldBe("codespace/agent/a2");
    }

    [Fact]
    public async Task A_supervisor_turns_parallel_agents_all_reach_the_integrator()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var repositoryId = Guid.NewGuid();

        // RealSupervisorActionExecutor stamps ONE turn cell on every agent it spawns in a turn, so these two rows
        // share a (node, iteration) cell while being concurrent deliverables of DIFFERENT subtasks.
        var alpha = await SeedAgentRunAsync(teamId, runId, new AgentRunSeed("sup#turn1", MinutesAgo: 9, NodeId: "sup", Patch: "diff-alpha", SubtaskId: "subtask-a"));
        var beta = await SeedAgentRunAsync(teamId, runId, new AgentRunSeed("sup#turn1", MinutesAgo: 8, NodeId: "sup", Patch: "diff-beta", SubtaskId: "subtask-b"));
        await SeedAgentManifestAsync(teamId, runId, alpha, repositoryId, branch: "codespace/agent/s1");
        await SeedAgentManifestAsync(teamId, runId, beta, repositoryId, branch: "codespace/agent/s2");

        using var scope = _fixture.BeginScope();
        var integrator = new RecordingIntegrator();
        var node = new GitIntegrateRunNode(integrator, new StubResolver(), scope.Resolve<IPublishManifestStore>(), scope.Resolve<CodeSpaceDbContext>());

        await node.RunAsync(Context(repositoryId, teamId, runId), CancellationToken.None);

        integrator.LastRequest!.Contributions.Select(c => c.ProducedBranch).ShouldBe(new[] { "codespace/agent/s1", "codespace/agent/s2" },
            customMessage: "the supervisor's K parallel agents share a TURN cell, not a unit — reducing them to one silently drops K-1 real, unsuperseded contributions");
        integrator.LastRequest.Contributions.Select(c => c.Patch).ShouldBe(new[] { "diff-alpha", "diff-beta" }, customMessage: "and each sibling's own bytes reach the integrator");
    }

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

    /// <summary>One agent-run row to seed. <see cref="SubtaskId"/> is the supervisor's per-agent stamp — set it and the row lands in the supervisor lane, whose turn cell is a container rather than a unit.</summary>
    private sealed record AgentRunSeed(string IterationKey, int MinutesAgo, string NodeId = "agent", string? Patch = null, string? SubtaskId = null);

    private async Task<Guid> SeedAgentRunAsync(Guid teamId, Guid runId, AgentRunSeed seed)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddMinutes(-seed.MinutesAgo);

        // A REAL task envelope, never "{}": the contribution reduction reads it to tell which lane the row is in.
        db.AgentRun.Add(new AgentRun
        {
            Id = id, TeamId = teamId, WorkflowRunId = runId, NodeId = seed.NodeId, IterationKey = seed.IterationKey,
            Harness = "codex-cli", Status = AgentRunStatus.Succeeded,
            TaskJson = JsonSerializer.Serialize(new AgentTask { Goal = "do the work", Harness = "codex-cli", SubtaskId = seed.SubtaskId }, Core.Services.Agents.AgentJson.Options),
            ResultJson = JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Patch = seed.Patch ?? $"diff --git a/{seed.IterationKey} b/{seed.IterationKey}\n" }, Core.Services.Agents.AgentJson.Options),
            CreatedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedDate = at, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedAgentManifestAsync(Guid teamId, Guid runId, Guid agentRunId, Guid repositoryId, string branch, string baseSha = "b1", PublishAcceptanceState acceptance = PublishAcceptanceState.NotApplicable)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IPublishManifestStore>().UpsertForAgentRunAsync(agentRunId, new PublishManifestUpsert
        {
            TeamId = teamId, WorkflowRunId = runId, RepositoryId = repositoryId, RepositoryAlias = "primary",
            BaseSha = baseSha, Branch = branch, AcceptanceState = acceptance, PublishStateValue = PublishState.Pushed,
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
