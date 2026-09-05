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
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Supervisor;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): <see cref="ISupervisorPublishedBranchResolver"/> against REAL Postgres — DC-3's ONE
/// reader of "what did this run genuinely publish" (the Room's Open-PR action, the Room's publish-state
/// projection, the Room's delivery card, <c>AgentSupervisorNode.Finish</c>'s terminal output, and the supervisor's
/// own future auto-open-PR delivery step all share it). Proves the merge-derived reads win when present, the P0-5
/// ledger-direct fallback recognizes a genuinely published branch the pre-DC-3 readers were blind to (run 96695645's
/// own motivating scenario), and — the audit's own core finding — that the single-repo merge-derived path resolves
/// correctly PRE-TERMINAL when the caller supplies a live <c>primaryRepositoryId</c> (mirroring
/// <c>SupervisorTurnService.Rehydrate.ResolveAcceptanceTargets</c>'s identical need), never depending on
/// <c>WorkflowRun.OutputsJson</c> being populated (which only happens at terminal completion).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorPublishedBranchResolverTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorPublishedBranchResolverTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Merge_derived_single_repo_branch_resolves_PRE_TERMINAL_via_the_callers_own_repository_id()
    {
        // The audit's core finding: before this fix, the single-repo path read WorkflowRun.OutputsJson, written
        // ONLY at terminal completion — a stop-gate / mid-run enrichment call (this run is never stamped terminal
        // in this test) would have resolved to EMPTY. A caller with a live turn context supplies its own
        // primaryRepositoryId (context.AgentProfile?.RepositoryId) and must resolve correctly regardless.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1");
        // Deliberately NEVER stamp WorkflowRun.OutputsJson — this run stays pre-terminal for the whole test.

        var branches = await ResolveAsync(runId, teamId, primaryRepositoryId: repoId);

        var branch = branches.ShouldHaveSingleItem();
        branch.RepositoryId.ShouldBe(repoId);
        branch.Alias.ShouldBe("primary");
        branch.SourceBranch.ShouldBe("codespace/integration/run/turn1");
        branch.TargetBranch.ShouldBe("main");
    }

    [Fact]
    public async Task Merge_derived_single_repo_branch_falls_back_to_terminal_OutputsJson_when_no_hint_is_supplied()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1");
        await StampTerminalRepositoryIdAsync(runId, repoId);

        var branches = await ResolveAsync(runId, teamId, primaryRepositoryId: null);

        var branch = branches.ShouldHaveSingleItem();
        branch.RepositoryId.ShouldBe(repoId);
        branch.SourceBranch.ShouldBe("codespace/integration/run/turn1");
    }

    [Fact]
    public async Task Merge_derived_multi_repo_branches_win_when_present()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var webRepoId = await SeedBoundRepositoryAsync(teamId);
        var apiRepoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedMultiRepoMergeAsync(runId, teamId, (webRepoId, "web", "codespace/integration/run/turn1", "main"), (apiRepoId, "api", "codespace/integration/run/turn1", "main"));

        var branches = await ResolveAsync(runId, teamId, primaryRepositoryId: null);

        branches.Count.ShouldBe(2);
        branches.Select(b => b.Alias).ShouldBe(new[] { "web", "api" }, ignoreOrder: true);
    }

    [Fact]
    public async Task Partial_multi_repo_merge_exposes_no_authoritative_branch_before_resolution()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var webRepoId = await SeedBoundRepositoryAsync(teamId);
        var apiRepoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPartialMultiRepoMergeAsync(runId, teamId, webRepoId, apiRepoId);

        (await ResolveAsync(runId, teamId, primaryRepositoryId: null))
            .ShouldBeEmpty("the resolver must not publish the clean child of an aggregate-conflicted integration before the conflicted repository is resolved");
    }

    [Fact]
    public async Task Merge_derived_branches_win_uncontaminated_by_a_leftover_unrelated_manifest_row()
    {
        // Sweep-found coverage gap: a run that DID merge cleanly can still carry an unrelated, EARLIER round's
        // contributor's own manifest row (superseded by that later merge, never itself merged) — the merge-derived
        // rung must win OUTRIGHT and never blend with the ledger-direct fallback data. The stray contributor's
        // spawn/manifest MUST precede the merge in sequence — a spawn AFTER the merge is a different, already-
        // handled case (ReadFinalIntegratedBranch's OWN "fresh unconsolidated work invalidates a stale merge"
        // disqualifier), not "an irrelevant leftover a definitive later merge already superseded".
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var strayAgentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, strayAgentRunId, acceptancePassed: true);
        await SeedAgentManifestAsync(runId, teamId, strayAgentRunId, repoId, "codespace/agent/superseded", PublishState.Pushed);

        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1");

        var branches = await ResolveAsync(runId, teamId, primaryRepositoryId: repoId);

        var branch = branches.ShouldHaveSingleItem();
        branch.SourceBranch.ShouldBe("codespace/integration/run/turn1", "the merge-derived branch must win outright — the stray manifest row must never surface or blend in");
    }

    [Fact]
    public async Task Ledger_direct_fallback_recognizes_a_single_pushed_contributor_with_no_merge_at_all()
    {
        // Run 96695645's own scenario: a single accepted unit's own AgentRunId already has a Pushed PublishManifest
        // row, but no merge/integration decision ever ran. The pre-DC-3 readers were blind to this entirely.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId, acceptancePassed: true);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, repoId, "codespace/agent/fix", PublishState.Pushed);

        var branches = await ResolveAsync(runId, teamId, primaryRepositoryId: null);

        var branch = branches.ShouldHaveSingleItem();
        branch.RepositoryId.ShouldBe(repoId);
        branch.Alias.ShouldBe("primary");
        branch.SourceBranch.ShouldBe("codespace/agent/fix");
        branch.TargetBranch.ShouldBe("main");
    }

    [Fact]
    public async Task Ledger_direct_fallback_surfaces_a_manifest_a_replan_stranded_when_the_active_plan_staged_nothing()
    {
        // Run 3a49c716's own failure, and the other half of the merge rung's carry-over: three Succeeded, PUSHED
        // agent runs, then two re-plans, then publish. The active generation's staging set is EMPTY (not null), so
        // this rung filtered out every manifest and the run logged "Supervisor publish … resolved 0 target(s)". A
        // plan-generation boundary may supersede an INSTRUCTION; it must not make FINISHED, PUSHED work invisible.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var oldAgentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, oldAgentRunId, acceptancePassed: true, sequence: 1);
        await SeedAgentManifestAsync(runId, teamId, oldAgentRunId, repoId, "codespace/agent/old", PublishState.Pushed);
        await SeedPlanAsync(runId, teamId, "replacement", sequence: 2);

        var branch = (await ResolveAsync(runId, teamId, primaryRepositoryId: null)).ShouldHaveSingleItem();
        branch.SourceBranch.ShouldBe("codespace/agent/old", "the run's only genuinely published work must stay reachable across a re-plan that staged nothing of its own");
    }

    [Fact]
    public async Task Ledger_direct_fallback_does_not_surface_a_manifest_from_before_a_plan_that_staged_its_own_work()
    {
        // The invariant the carry-over deliberately keeps: a superseded generation's contributor never OUTRANKS the
        // current generation's own staged work — it is conserved only when the active generation staged nothing.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var staleRepoId = await SeedBoundRepositoryAsync(teamId);
        var currentRepoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var oldAgentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, oldAgentRunId, acceptancePassed: true, sequence: 1);
        await SeedAgentManifestAsync(runId, teamId, oldAgentRunId, staleRepoId, "codespace/agent/old", PublishState.Pushed, alias: "web");
        await SeedPlanAsync(runId, teamId, "replacement", sequence: 2);

        var freshAgentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, freshAgentRunId, acceptancePassed: true, sequence: 3);
        await SeedAgentManifestAsync(runId, teamId, freshAgentRunId, currentRepoId, "codespace/agent/fresh", PublishState.Pushed, alias: "api");

        var branch = (await ResolveAsync(runId, teamId, primaryRepositoryId: null)).ShouldHaveSingleItem();
        branch.SourceBranch.ShouldBe("codespace/agent/fresh", "a pushed manifest remains durable evidence, but it cannot join a plan generation that staged its own work");
    }

    [Fact]
    public async Task An_integrated_head_from_a_generation_the_plan_abandoned_is_never_published()
    {
        // plan(1) → spawn (Succeeded, PUSHED) → merge (Clean) → plan(1, ABANDON) → publish. The exclusion reached
        // only the ledger-direct rung, so the merge-derived rung above it still read the WHOLE tape and resolved
        // exactly the head the flag declared unpublishable: the merge folded none of that work and the run shipped it.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedPlanAsync(runId, teamId, "s1", sequence: 1);
        await SeedSpawnAsync(runId, teamId, agentRunId, acceptancePassed: true, sequence: 2);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, repoId, "codespace/agent/a", PublishState.Pushed);
        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1", sequence: 3);
        await SeedPlanAsync(runId, teamId, "s2", sequence: 4, abandonEarlierResults: true);

        (await ResolveAsync(runId, teamId, primaryRepositoryId: repoId))
            .ShouldBeEmpty("the head AND the contributor branch under it both came from the direction the plan abandoned — every rung of the ladder has to read from that plan onward, not just the last one");
    }

    [Fact]
    public async Task Ledger_direct_fallback_excludes_an_acceptance_rejected_contributor()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId, acceptancePassed: false);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, repoId, "codespace/agent/fix", PublishState.Pushed);

        (await ResolveAsync(runId, teamId, primaryRepositoryId: null)).ShouldBeEmpty("a raw push happens before the per-unit acceptance grade folds — a REJECTED unit must never satisfy the ledger-direct fallback");
    }

    [Fact]
    public async Task Ledger_direct_fallback_requires_every_repo_of_a_multi_repo_agent_to_be_pushed()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var webRepoId = await SeedBoundRepositoryAsync(teamId);
        var apiRepoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, agentRunId, acceptancePassed: true);
        await SeedAgentManifestAsync(runId, teamId, agentRunId, webRepoId, "codespace/agent/web", PublishState.Pushed, alias: "web");
        await SeedAgentManifestAsync(runId, teamId, agentRunId, apiRepoId, branch: null, PublishState.PatchOnly, alias: "api");

        (await ResolveAsync(runId, teamId, primaryRepositoryId: null)).ShouldBeEmpty("a partially-published multi-repo agent is not genuinely published — the same all-or-nothing posture acceptance grading applies");
    }

    [Fact]
    public async Task Ledger_direct_fallback_picks_the_newest_contributor_per_alias_across_rounds()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var firstAgentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, firstAgentRunId, acceptancePassed: true);
        await SeedAgentManifestAsync(runId, teamId, firstAgentRunId, repoId, "codespace/agent/first", PublishState.Pushed);

        var secondAgentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, secondAgentRunId, acceptancePassed: true);
        await SeedAgentManifestAsync(runId, teamId, secondAgentRunId, repoId, "codespace/agent/second", PublishState.Pushed);

        var branch = (await ResolveAsync(runId, teamId, primaryRepositoryId: null)).ShouldHaveSingleItem();
        branch.SourceBranch.ShouldBe("codespace/agent/second", "the LATER contributor's branch wins for the shared alias");
    }

    [Fact]
    public async Task Nothing_published_resolves_to_empty()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        (await ResolveAsync(runId, teamId, primaryRepositoryId: null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_merge_derived_branch_with_no_repository_hint_and_no_terminal_stamp_resolves_to_empty_never_throws()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1");
        // No primaryRepositoryId hint, and WorkflowRun.OutputsJson stays "{}" — repositoryId is unresolvable either way.

        (await ResolveAsync(runId, teamId, primaryRepositoryId: null)).ShouldBeEmpty("a degraded merge-derived branch with no resolvable repository is a caller concern, never a thrown exception from the shared resolver");
    }

    // ─── Resolve driver ─────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<SupervisorRepositoryBranch>> ResolveAsync(Guid runId, Guid teamId, Guid? primaryRepositoryId)
    {
        using var scope = _fixture.BeginScope();
        var priorDecisions = await scope.Resolve<ISupervisorDecisionLog>().GetTerminalDecisionsAsync(runId, teamId, CancellationToken.None);
        return await scope.Resolve<ISupervisorPublishedBranchResolver>().ResolveAsync(runId, teamId, priorDecisions, primaryRepositoryId, CancellationToken.None);
    }

    // ─── Seeding ────────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        var workflowId = await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "publish-branch-resolver-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    /// <summary>Registered under <c>ProviderKind.Git</c> with a bound credential — <c>DefaultBranch</c> "main".</summary>
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

    private async Task SeedSingleRepoMergeAsync(Guid runId, Guid teamId, string integratedBranch, int sequence = 0)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var outcome = JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch, appliedCount = 1, reason = (string?)null, excludedAgents = Array.Empty<string>() } }, AgentJson.Options);

        await AddTerminalDecisionAsync(db, runId, teamId, SupervisorDecisionKinds.Merge, outcome, sequence: sequence);
    }

    private async Task SeedMultiRepoMergeAsync(Guid runId, Guid teamId, params (Guid? RepositoryId, string Alias, string SourceBranch, string TargetBranch)[] repos)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var blocks = repos.Select(r => new { repositoryId = r.RepositoryId, alias = r.Alias, status = "Clean", integratedBranch = r.SourceBranch, baseBranch = r.TargetBranch }).ToList();
        var outcome = JsonSerializer.Serialize(new { integration = new { status = "Clean", reason = (string?)null, repositories = blocks } }, AgentJson.Options);

        await AddTerminalDecisionAsync(db, runId, teamId, SupervisorDecisionKinds.Merge, outcome);
    }

    private async Task SeedPartialMultiRepoMergeAsync(Guid runId, Guid teamId, Guid webRepoId, Guid apiRepoId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var outcome = JsonSerializer.Serialize(new
        {
            integration = new
            {
                status = "Conflicted", reason = "api conflicted",
                repositories = new object[]
                {
                    new { repositoryId = webRepoId, alias = "web", status = "Clean", integratedBranch = "codespace/integration/run/turn1", baseBranch = "main" },
                    new { repositoryId = apiRepoId, alias = "api", status = "Conflicted", integratedBranch = (string?)null, baseBranch = "main" },
                },
            },
        }, AgentJson.Options);

        await AddTerminalDecisionAsync(db, runId, teamId, SupervisorDecisionKinds.Merge, outcome);
    }

    private async Task SeedPlanAsync(Guid runId, Guid teamId, string subtaskId, int sequence = 0, bool abandonEarlierResults = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var payload = JsonSerializer.Serialize(new SupervisorPlanPayload
        {
            Goal = "replacement",
            Subtasks = new[] { new SupervisorPlannedSubtask { Id = subtaskId, Title = subtaskId, Instruction = $"do {subtaskId}" } },
            AbandonEarlierResults = abandonEarlierResults,
        }, AgentJson.Options);

        await AddTerminalDecisionAsync(db, runId, teamId, SupervisorDecisionKinds.Plan, "{}", payload, sequence);
    }

    /// <summary>Hand-seeds a TERMINAL spawn decision with one folded agent result — the shape <see cref="SupervisorOutcome.ReadAgentResults"/> reads, which the ledger-direct fallback's rejection filter (<see cref="SupervisorOutcome.WithheldAgentRunIds"/>) scans.</summary>
    private async Task SeedSpawnAsync(Guid runId, Guid teamId, Guid agentRunId, bool? acceptancePassed, int sequence = 0)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var result = new SupervisorAgentResult { AgentRunId = agentRunId, Status = "Succeeded", ChangedFiles = new[] { "a.txt" }, AcceptancePassed = acceptancePassed };
        var outcome = JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);

        await AddTerminalDecisionAsync(db, runId, teamId, SupervisorDecisionKinds.Spawn, outcome, sequence: sequence);
    }

    private async Task SeedAgentManifestAsync(Guid runId, Guid teamId, Guid agentRunId, Guid? repositoryId, string? branch, PublishState state, string alias = "primary")
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IPublishManifestStore>().UpsertForAgentRunAsync(agentRunId, new PublishManifestUpsert
        {
            TeamId = teamId,
            WorkflowRunId = runId,
            RepositoryAlias = alias,
            RepositoryId = repositoryId,
            Branch = branch,
            ChangedFileCount = 1,
            PublishStateValue = state,
        }, CancellationToken.None);
    }

    private static async Task AddTerminalDecisionAsync(CodeSpaceDbContext db, Guid runId, Guid teamId, string decisionKind, string outcomeJson, string payloadJson = "{}", int sequence = 0)
    {
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence,
            DecisionKind = decisionKind, IdempotencyKey = $"{decisionKind}-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
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
}
