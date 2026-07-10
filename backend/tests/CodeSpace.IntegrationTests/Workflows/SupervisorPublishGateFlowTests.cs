using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Arbiter;
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
/// 🟢 HIGH fidelity (Rule 12): I3 (publish-or-park) end to end — the REAL <see cref="SupervisorTurnService"/> +
/// the REAL <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> merge, against a REAL
/// bare git remote (no fakes at the git seam). Each test seeds ONE prior spawn whose accepted unit carries a real,
/// git-valid patch, then drives a SCRIPTED decider that always tries to <c>stop</c> — proving <see cref="SupervisorPublishGate"/>
/// rewrites that stop into a server-authored merge the FIRST time (a real clone + apply + push happens), and — on
/// the following turn, once the tape shows what that merge produced — either lets the (now genuinely published +
/// summarized) stop through, or parks it to <c>ask_human</c> naming why publishing still failed (a real conflict,
/// or the repo's OWN <c>PublishMode.PatchOnly</c> policy blocking the push through the SAME guard chain the
/// per-agent push already respects).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorPublishGateFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorPublishGateFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    [Fact]
    public async Task A_stop_with_unpublished_work_auto_merges_then_a_later_stop_completes_cleanly()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var remote = new PublishGateRemote();
        await remote.InitAsync();
        var baseSha = await remote.HeadShaAsync();
        var patch = await remote.MakePatchAsync(baseSha, dir => File.WriteAllText(Path.Combine(dir, "feature.txt"), "shipped\n"));

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedAcceptedUnitAsync(runId, teamId, agentRunId, baseSha, patch);
        await SeedSpawnAsync(runId, agentRunId);

        // Turn 1: the decider tries to stop; I3 has nothing published yet → rewritten to a server-authored merge.
        var merge = await RunStopTurnAsync(runId, teamId, repoId);
        merge.Kind.ShouldBe(SupervisorDecisionKinds.Merge, "I3 rewrites the stop into an auto-integrate BEFORE the run can terminalize unpublished work");
        JsonSerializer.Deserialize<SupervisorMergePayload>(merge.PayloadJson, AgentJson.Options)!.ForcedByPublishGate.ShouldBe(true);

        var integration = JsonDocument.Parse(merge.OutcomeJson!).RootElement.GetProperty("integration");
        integration.GetProperty("status").GetString().ShouldBe("Clean", "a real, non-conflicting patch integrates cleanly");
        var integratedBranch = integration.GetProperty("integratedBranch").GetString();
        integratedBranch.ShouldNotBeNullOrEmpty();
        (await remote.HasBranchAsync(integratedBranch!)).ShouldBeTrue("the merge genuinely pushed the integrated branch to the real remote, not just result_jsonb");
        integration.TryGetProperty("synthesis", out _).ShouldBeFalse("a FORCED merge skips the LLM synthesis facet — the server is publishing, not narrating");

        // Turn 2: the SAME decider tries to stop again; the tape now shows a clean, published merge + a summary → proceeds untouched.
        var stop = await RunStopTurnAsync(runId, teamId, repoId);
        stop.Kind.ShouldBe(SupervisorDecisionKinds.Stop, "published + summarized — the SECOND stop attempt is no longer rewritten");
    }

    [Fact]
    public async Task A_stop_whose_frontier_is_already_published_via_its_own_manifest_completes_with_no_merge_attempt()
    {
        // Real-incident regression (run 96695645-2555-453b-9208-4e1df5114770): a single accepted unit's OWN
        // AgentRunId already had a Pushed PublishManifest row, but no SEPARATE Integration-kind manifest ever
        // existed for a later merge (the auto-integrate-at-stop augmentation is opt-in-gated, and this scenario's
        // model-authored merge never triggers it). Before this fix, I3 saw "not published" and repeatedly rewrote
        // every stop attempt into the SAME templated ask_human, forever — even though the work was already on a
        // real, pushed branch. This test proves the gate now reads the canonical PublishManifest ledger directly
        // and lets the run complete WITHOUT ever needing a merge at all.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, agentRunId);
        await SeedPushedManifestAsync(runId, teamId, agentRunId, "codespace/agent/already-pushed");

        var stop = await RunStopTurnAsync(runId, teamId, repoId);

        stop.Kind.ShouldBe(SupervisorDecisionKinds.Stop, "the frontier's own contributor is already published on the ledger — I3 must let the summarized stop through directly, no merge and no ask_human");
    }

    [Fact]
    public async Task A_stop_whose_frontier_is_already_published_but_carries_no_summary_still_parks_to_ask_human()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, agentRunId);
        await SeedPushedManifestAsync(runId, teamId, agentRunId, "codespace/agent/already-pushed");

        using (var scope = _fixture.BeginScope())
        {
            var service = NewTurnService(scope, new AlwaysStopWithNoSummaryDecider());
            await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, GoalConfig(repoId), CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var latest = await verify.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderByDescending(d => d.Sequence).FirstAsync();

        latest.DecisionKind.ShouldBe(SupervisorDecisionKinds.AskHuman, "published work still needs a summary before the run can complete — the same rule I3 already applies to an integration-published run");
    }

    [Fact]
    public async Task A_pushed_but_acceptance_rejected_contributor_never_satisfies_i3_via_the_published_shortcut()
    {
        // Finding 1 (adversarial sweep on the P0-5 fix): a raw push happens BEFORE the per-unit acceptance grade
        // folds (AgentRunExecutor pushes at agent-completion time; FoldUnitAcceptanceGradeAsync grades later) — so a
        // REJECTED unit can still show up as Pushed on the canonical ledger. I3's published shortcut must exclude
        // it, the same "局部綠≠整合綠" bar the merge + resolver doors already enforce — otherwise I3 would let a run
        // complete with ONLY rejected work.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        var agentRunId = Guid.NewGuid();
        await SeedRejectedSpawnAsync(runId, agentRunId);
        await SeedPushedManifestAsync(runId, teamId, agentRunId, "codespace/agent/rejected-but-pushed");

        var merge = await RunStopTurnAsync(runId, teamId, repoId);

        merge.Kind.ShouldBe(SupervisorDecisionKinds.Merge, "the contributor's push does not count as published once its own acceptance grade rejected it — I3 falls through to the ordinary ladder, never a silent complete");
    }

    [Fact]
    public async Task A_frontiers_published_manifest_never_overrides_a_prior_diagnosed_merge_conflict()
    {
        // Finding 2 (adversarial sweep on the P0-5 fix): "pushed" and "cleanly integrates" are INDEPENDENT facts
        // about the SAME branch — a contributor's own raw push happens at agent-completion time, strictly BEFORE any
        // later merge/integrate attempt over that branch. A prior merge that ran a real integrate step and genuinely
        // conflicted must outrank the raw-push shortcut, never be silently overridden by it — proven through the
        // REAL rehydrate-from-Postgres path (not a synthetic in-memory context), the exact production seam the P0-5
        // fix touched. The Conflicted outcome is seeded directly (mirrors what the real integrator writes — see the
        // real-git equivalent below) for a deterministic, DB-only proof of the ordering fix.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, agentRunId);
        await SeedPushedManifestAsync(runId, teamId, agentRunId, "codespace/agent/already-pushed");
        await SeedConflictedMergeAsync(runId, "base SHA mismatch");

        var park = await RunStopTurnAsync(runId, teamId, repoId);

        park.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "a prior diagnosed integration conflict outranks the raw-push shortcut — the run must park, not silently complete");

        var question = JsonSerializer.Deserialize<SupervisorAskHumanPayload>(park.PayloadJson, AgentJson.Options)!.Question;
        question.ShouldContain("base SHA mismatch", Case.Insensitive, "the park names the diagnosed reason, not a generic message");
    }

    [Fact]
    public async Task A_multi_repo_agent_with_only_one_repo_pushed_is_not_recognized_as_published()
    {
        // The all-or-nothing multi-repo posture: TWO manifest rows share the SAME AgentRunId (one per repo it
        // wrote to) — only ONE repo actually pushed. A lone pushed repo must never mask an unpublished sibling,
        // so this agent must NOT be in the published set, and I3 must still fall through to its ordinary ladder.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        var agentRunId = Guid.NewGuid();
        await SeedSpawnAsync(runId, agentRunId);
        await SeedPushedManifestAsync(runId, teamId, agentRunId, "codespace/agent/web-pushed", alias: "web");
        await SeedPatchOnlyManifestAsync(runId, teamId, agentRunId, alias: "api");

        var merge = await RunStopTurnAsync(runId, teamId, repoId);

        merge.Kind.ShouldBe(SupervisorDecisionKinds.Merge, "one published repo out of two must never satisfy I3 for the whole agent — it falls through to the ordinary forced-merge ladder exactly as if nothing were published");
    }

    [Fact]
    public async Task A_stop_after_a_conflicting_auto_merge_parks_to_ask_human()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var remote = new PublishGateRemote();
        await remote.InitAsync();
        var baseSha = await remote.HeadShaAsync();

        // Two contributions that BOTH touch the same file's same line — a real, unavoidable textual conflict.
        var patchA = await remote.MakePatchAsync(baseSha, dir => File.WriteAllText(Path.Combine(dir, "shared.txt"), "agent A\n"));
        var patchB = await remote.MakePatchAsync(baseSha, dir => File.WriteAllText(Path.Combine(dir, "shared.txt"), "agent B\n"));

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        await SeedAcceptedUnitAsync(runId, teamId, agentA, baseSha, patchA);
        await SeedAcceptedUnitAsync(runId, teamId, agentB, baseSha, patchB);
        await SeedSpawnAsync(runId, agentA, agentB);

        var merge = await RunStopTurnAsync(runId, teamId, repoId);
        merge.Kind.ShouldBe(SupervisorDecisionKinds.Merge);
        JsonDocument.Parse(merge.OutcomeJson!).RootElement.GetProperty("integration").GetProperty("status").GetString().ShouldBe("Conflicted");

        var park = await RunStopTurnAsync(runId, teamId, repoId);
        park.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "a real conflict already tried once — I3 never retries the merge blindly, it parks");

        var question = JsonSerializer.Deserialize<SupervisorAskHumanPayload>(park.PayloadJson, AgentJson.Options)!.Question;
        question.ShouldContain("could not be published", Case.Insensitive);
    }

    [Fact]
    public async Task A_stop_against_a_patch_only_repository_parks_to_ask_human_naming_publish_policy()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var remote = new PublishGateRemote();
        await remote.InitAsync();
        var baseSha = await remote.HeadShaAsync();
        var patch = await remote.MakePatchAsync(baseSha, dir => File.WriteAllText(Path.Combine(dir, "feature.txt"), "shipped\n"));

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, publishMode: RepositoryPublishMode.PatchOnly);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentRunId = Guid.NewGuid();
        await SeedAcceptedUnitAsync(runId, teamId, agentRunId, baseSha, patch);
        await SeedSpawnAsync(runId, agentRunId);

        var merge = await RunStopTurnAsync(runId, teamId, repoId);
        merge.Kind.ShouldBe(SupervisorDecisionKinds.Merge);

        var integration = JsonDocument.Parse(merge.OutcomeJson!).RootElement.GetProperty("integration");
        integration.GetProperty("status").GetString().ShouldBe("Skipped", "the repo's PatchOnly policy blocks the push — the SAME guard chain the per-agent push already respects");
        integration.GetProperty("reason").GetString().ShouldContain("publish policy", Case.Insensitive);

        var park = await RunStopTurnAsync(runId, teamId, repoId);
        park.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman);

        var question = JsonSerializer.Deserialize<SupervisorAskHumanPayload>(park.PayloadJson, AgentJson.Options)!.Question;
        question.ShouldContain("publish policy", Case.Insensitive, "the park names the repo policy as WHY, not a generic failure");
    }

    // ─── Turn driver ────────────────────────────────────────────────────────────────

    /// <summary>Run ONE turn with a decider that always tries to author a genuinely-summarized <c>stop</c> — I3 decides what it actually becomes. Re-resolves a FRESH <see cref="SupervisorTurnService"/> each call (mirrors <c>SupervisorMergeWithholdFlowTests</c>) so the tape is re-rehydrated from Postgres exactly like production.</summary>
    private async Task<SupervisorDecisionRecordSnapshot> RunStopTurnAsync(Guid runId, Guid teamId, Guid repoId)
    {
        using (var scope = _fixture.BeginScope())
        {
            var service = NewTurnService(scope, new AlwaysStopDecider());
            await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, GoalConfig(repoId), CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var latest = await verify.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderByDescending(d => d.Sequence)
            .FirstAsync();

        return new SupervisorDecisionRecordSnapshot(latest.DecisionKind, latest.PayloadJson, latest.OutcomeJson);
    }

    /// <summary>The REAL <see cref="SupervisorTurnService"/>, every dependency resolved from the scope except the decider (scripted per test) — the shared construction every turn driver in this file reuses.</summary>
    private static SupervisorTurnService NewTurnService(ILifetimeScope scope, ISupervisorDecider decider) => new(
        scope.Resolve<ISupervisorDecisionLog>(),
        decider,
        scope.Resolve<ISupervisorActionExecutor>(),
        scope.Resolve<CodeSpaceDbContext>(),
        scope.Resolve<ISupervisorAcceptanceGrader>(),
        scope.Resolve<IDecisionQueueService>(),
        scope.Resolve<IDecisionArbiter>(),
        scope.Resolve<IDecisionAnswerService>(),
        scope.Resolve<Core.Services.Plans.IWorkPlanService>(),
        scope.Resolve<Core.Services.Workflows.Lifecycle.IRunRecordLogger>(),
        scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
        scope.Resolve<Core.Services.Agents.Publish.IPublishManifestStore>(),
        scope.Resolve<Core.Services.Supervisor.ISupervisorPublishedBranchResolver>(),
        scope.Resolve<ILogger<SupervisorTurnService>>());

    private sealed record SupervisorDecisionRecordSnapshot(string Kind, string PayloadJson, string? OutcomeJson);

    /// <summary>A decider that always tries to stop with a real summary — I3 alone decides whether that stop proceeds, is rewritten to a merge, or is parked.</summary>
    private sealed class AlwaysStopDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Stop,
                PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "completed", Summary = "shipped the feature" }, AgentJson.Options),
            });
    }

    /// <summary>A decider that always tries to stop with a BLANK summary — proves I3's summary requirement still applies to a run published via the P0-5 ledger-direct recognition, not only the integration path.</summary>
    private sealed class AlwaysStopWithNoSummaryDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Stop,
                PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "completed", Summary = "" }, AgentJson.Options),
            });
    }

    private static SupervisorGoalConfig GoalConfig(Guid repoId) => new() { Goal = Goal, AgentProfile = new SupervisorAgentProfile { RepositoryId = repoId } };

    // ─── Seeding ────────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-publish-gate-" + Guid.NewGuid().ToString("N")[..6],
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
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>Seed a Repository bound to a real (file://) clone URL with a write-capable PAT credential, optionally under a repo-level publish policy — mirrors <c>AgentBranchPushFlowTests.SeedBoundRepositoryAsync</c>.</summary>
    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps, RepositoryPublishMode publishMode = RepositoryPublishMode.Branch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = $"https://local/{instanceId:N}" });

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
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
            PublishMode = publishMode,
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    /// <summary>Seed a REAL terminal <c>AgentRun</c> row whose result carries a real base SHA + real patch — the SAME fields the real capture layer records, so the real integrator has genuine, git-valid work to apply.</summary>
    private async Task SeedAcceptedUnitAsync(Guid runId, Guid teamId, Guid agentRunId, string baseSha, string patch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var resultJson = JsonSerializer.Serialize(new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "did it",
            ChangedFiles = new[] { "feature.txt" }, BaseSha = baseSha, Patch = patch,
        }, AgentJson.Options);

        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = runId, NodeId = NodeId, Harness = "codex-cli",
            Status = AgentRunStatus.Succeeded, TaskJson = "{}", ResultJson = resultJson,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seed the prior <c>spawn</c> decision the merge folds — a compact projection of the accepted unit(s). <c>Sequence</c>
    /// is DELIBERATELY left unset: it's <c>ValueGeneratedOnAdd()</c> (a real Postgres bigserial), so an explicit value
    /// here would bypass the sequence counter and collide with the NEXT real decision <c>RunTurnAsync</c> records —
    /// exactly the bug that made two decisions land on the same Sequence in this test's first draft.
    /// </summary>
    private async Task SeedSpawnAsync(Guid runId, params Guid[] agentRunIds)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var teamId = await db.WorkflowRun.Where(r => r.Id == runId).Select(r => r.TeamId).SingleAsync();

        var units = agentRunIds.Select(id => new SupervisorAgentResult { AgentRunId = id, Status = "Succeeded", Summary = "did it", ChangedFiles = new[] { "feature.txt" } }).ToArray();
        var outcome = SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds, agentCount = agentRunIds.Length }, AgentJson.Options), units);

        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Spawn, IdempotencyKey = $"spawn-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = """{"subtaskIds":["s1"]}""", OutcomeJson = outcome,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Seed a <c>spawn</c> decision for a unit whose per-unit acceptance grade OBJECTIVELY REJECTED it (Finding 1's
    /// shape) — mirrors <see cref="SeedSpawnAsync"/> but stamps <see cref="SupervisorAgentResult.AcceptancePassed"/> false.</summary>
    private async Task SeedRejectedSpawnAsync(Guid runId, Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var teamId = await db.WorkflowRun.Where(r => r.Id == runId).Select(r => r.TeamId).SingleAsync();

        var unit = new SupervisorAgentResult { AgentRunId = agentRunId, Status = "Succeeded", Summary = "did it", ChangedFiles = new[] { "feature.txt" }, AcceptancePassed = false };
        var outcome = SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1 }, AgentJson.Options), new[] { unit });

        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Spawn, IdempotencyKey = $"spawn-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = """{"subtaskIds":["s1"]}""", OutcomeJson = outcome,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Seed a genuinely-published Agent-kind <c>PublishManifest</c> row DIRECTLY (bypassing real git — this scenario proves the GATE reads the ledger, not the integrator's own push mechanics, which are covered elsewhere in this file).</summary>
    private async Task SeedPushedManifestAsync(Guid runId, Guid teamId, Guid agentRunId, string branch, string alias = "primary")
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, WorkflowRunId = runId, AgentRunId = agentRunId,
            RepositoryAlias = alias, Branch = branch, PublishStateValue = PublishState.Pushed,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Seed a manifest row for a repo the agent touched but never pushed (patch-only) — the multi-repo "one sibling unpublished" half of the all-or-nothing test.</summary>
    private async Task SeedPatchOnlyManifestAsync(Guid runId, Guid teamId, Guid agentRunId, string alias)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, WorkflowRunId = runId, AgentRunId = agentRunId,
            RepositoryAlias = alias, PublishStateValue = PublishState.PatchOnly,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Seed a REAL merge decision whose recorded integration genuinely CONFLICTED — the same <c>{ merged, count, integration }</c>
    /// shape the real integrator writes (see the real-git equivalent, <c>A_stop_after_a_conflicting_auto_merge_parks_to_ask_human</c>),
    /// written directly so the ordering fix (Finding 2) has a deterministic, DB-only proof independent of real git.</summary>
    private async Task SeedConflictedMergeAsync(Guid runId, string reason)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var teamId = await db.WorkflowRun.Where(r => r.Id == runId).Select(r => r.TeamId).SingleAsync();

        var outcome = JsonSerializer.Serialize(new { merged = Array.Empty<object>(), count = 0, integration = new { status = "Conflicted", integratedBranch = (string?)null, reason } }, AgentJson.Options);

        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Merge, IdempotencyKey = $"merge-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local repo standing in for the remote — base-seeding, real patch construction, and branch inspection. Mirrors <c>SupervisorAcceptanceFoldFlowTests.AcceptanceRemote</c>.</summary>
    private sealed class PublishGateRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-sup-publishgate-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;
        private readonly string _seed;

        public PublishGateRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
            _seed = Path.Combine(_root, "seed");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task InitAsync()
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);
            Directory.CreateDirectory(_seed);
            await Git(_seed, "clone", _bare, _seed);
            await Config(_seed);
            await File.WriteAllTextAsync(Path.Combine(_seed, "base.txt"), "base\n");
            await Git(_seed, "add", "-A");
            await Git(_seed, "commit", "-m", "seed");
            await Git(_seed, "push", "origin", "main");
        }

        public async Task<string> HeadShaAsync()
        {
            var rev = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "-C", _seed, "rev-parse", "HEAD" }, TimeoutSeconds = 30 }, CancellationToken.None);
            return rev.Stdout.Trim();
        }

        /// <summary>A real unified diff rooted at <paramref name="baseSha"/> — a fresh detached clone, <paramref name="mutate"/>, then <c>git diff --cached</c> against the base.</summary>
        public async Task<string> MakePatchAsync(string baseSha, Action<string> mutate)
        {
            var work = Path.Combine(_root, "patch-" + Guid.NewGuid().ToString("N"));
            await Git(_root, "clone", _bare, work);
            await Config(work);
            await Git(work, "checkout", "--detach", baseSha);
            mutate(work);
            await Git(work, "add", "-A");

            var diff = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "-C", work, "diff", "--cached", "--no-color", baseSha }, TimeoutSeconds = 30 }, CancellationToken.None);

            Directory.Delete(work, recursive: true);
            return diff.Stdout;
        }

        public async Task<bool> HasBranchAsync(string branch)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--git-dir", _bare, "branch", "--list", branch }, TimeoutSeconds = 30 }, CancellationToken.None);
            return result.Stdout.Contains(branch, StringComparison.Ordinal);
        }

        private static async Task Config(string dir)
        {
            await Git(dir, "config", "user.email", "test@codespace.dev");
            await Git(dir, "config", "user.name", "Test");
            await Git(dir, "config", "commit.gpgsign", "false");
        }

        private static async Task Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
            if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
