using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 THE combined-gates E2E for the triad's S6 revise loop — every evaluation mechanism armed at once, on one
/// STANDARD-tier run, end to end through the REAL engine: the plan-confirm gate parks the authored plan (zero agents
/// until approval), the approved plan fans out TWO real agents whose FIRST attempts are both flawed, and each branch
/// is healed by a DIFFERENT gate feeding the SAME in-run revise loop —
///
/// <list type="bullet">
///   <item>item 1 (no contract): the INDEPENDENT Improve-critic (real <c>LlmStructuredCritic</c> through the
///         production pool; content-keyed deterministic reviewer) flags the planted flaw → the critique feeds back →
///         the revision removes it → the SAME critic approves.</item>
///   <item>item 2 (authored contract): the OBJECTIVE oracle (real <c>SupervisorAcceptanceGrader</c> cloning the
///         pushed branch off a real bare remote and executing <c>check.sh</c>) fails the draft → the grader's detail
///         feeds back → the revision passes the REAL re-grade on the re-pushed branch — and the failed oracle never
///         billed a review (order proven by the critic's call count).</item>
/// </list>
///
/// The run lands Success with both items Completed on the LIVE checklist, the contract item's verdict stamped, and
/// every revise round announced on its agent's timeline. Fidelity (Rule 12) — HIGH: real engine + real Postgres +
/// real <see cref="PlanConfirmNode"/> Action-wait resume + real <see cref="AgentRunExecutor"/> + real
/// <c>LocalProcessRunner</c> + real git clone/push/grade against a bare <c>file://</c> remote. Faked at honest seams:
/// the planner LLM (deterministic work-plan fake via the launch-baked model pin), the reviewer LLM (content-keyed
/// verdict), and the CLI's intelligence (<see cref="ReviseHealingFakeCli"/> — flawed first, fixed under the revise
/// instruction). POSIX-only; skips when git is absent.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class ReviseLoopFanoutE2ETests
{
    private const string SeedGoal = "Harden the feature file across the codebase";

    private readonly PostgresFixture _fixture;

    public ReviseLoopFanoutE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_confirmed_plan_fans_out_flawed_agents_and_both_gates_heal_their_branches_through_the_revise_loop()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitAvailableAsync()) return;    // real git required for clone/push/grade

        var priorToggle = Environment.GetEnvironmentVariable(CriticToggle.EnabledEnvVar);
        Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, "1");
        using (var knob = _fixture.BeginScope()) knob.Resolve<WorkPlanPlanScript>().AuthorContract = true;

        try
        {
            using var cli = new ReviseHealingFakeCli();

            var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
            var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);
            var (_, criticRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "critic-model", provider: DeterministicCriticLlmClient.ProviderTag);
            ResetCriticScript();

            using var remote = new BareRemote();
            await remote.SeedBaseAsync($"#!/bin/sh\ngrep -q revised {ReviseHealingFakeCli.FileName}\n");
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            var jobClient = ResolveJobClient();
            jobClient.Clear();
            jobClient.AutoExecute = true;

            var runId = await ProjectGatedAndStartAsync(teamId, userId, plannerRowId, criticRowId, repoId);

            // ── Pass 1: the planner authors the contract plan; the confirm gate PARKS it — zero agents. ──
            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            using (var parked = _fixture.BeginScope())
            {
                var db = parked.Resolve<CodeSpaceDbContext>();

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "the authored plan parks the run on the confirm gate");
                (await db.AgentRun.AsNoTracking().CountAsync(a => a.WorkflowRunId == runId)).ShouldBe(0, "fail-closed: no agent before the operator approves");
                (await db.WorkPlan.AsNoTracking().SingleAsync(p => p.WorkflowRunId == runId)).Status.ShouldBe(WorkPlanStatuses.AwaitingConfirmation);
            }

            // ── Approve → the fan-out runs; each branch drafts flawed work, its gate catches it, the revise loop heals it. ──
            (await ApproveAsync(runId, teamId, userId)).Resumed.ShouldBeTrue();
            await jobClient.WaitForPendingAsync();

            using var verify = _fixture.BeginScope();
            var db2 = verify.Resolve<CodeSpaceDbContext>();

            (await db2.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "both flawed branches healed through the revise loop — the run lands a clean Success");

            var agentRuns = await db2.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).OrderBy(r => r.IterationKey).ToListAsync();
            agentRuns.Count.ShouldBe(2, "the confirmed plan has two items — one branch each");
            agentRuns.ShouldAllBe(r => r.Status == AgentRunStatus.Succeeded);

            // Branch 0 — no contract: the CRITIC bought its revise round; there is no oracle verdict to stamp.
            var criticHealed = Result(agentRuns[0]);
            criticHealed.ReviseRounds.ShouldBe(1, "the Improve flag bought exactly one round");
            criticHealed.AcceptancePassed.ShouldBeNull("item 1 authored no contract — no oracle verdict");
            criticHealed.ReviewFeedback.ShouldBeNull("the final review APPROVED the revision — the flag was healed");

            // Branch 1 — the authored contract: the ORACLE bought its revise round and the re-grade passed for real.
            var oracleHealed = Result(agentRuns[1]);
            oracleHealed.ReviseRounds.ShouldBe(1, "the failed check bought exactly one round");
            oracleHealed.AcceptancePassed.ShouldBe(true, "the revised branch passed the REAL re-grade");
            oracleHealed.ProducedBranch.ShouldNotBeNull("the contract forces the branch publish");

            (await remote.BranchFileContentAsync(oracleHealed.ProducedBranch!, ReviseHealingFakeCli.FileName))
                .ShouldContain("revised", customMessage: "the remote branch tip carries the REVISED work — the same branch, force-updated by the loop");

            // The gates fired in the designed ORDER: the failed oracle never billed a review, so the critic ran
            // exactly three times — branch 0's flag + approve, and branch 1's single post-revise approve.
            CriticCalls().ShouldBe(3, "critic billing = flag(b0) + approve(b0) + approve(b1); round 1 of the contract branch was killed by the oracle BEFORE any review");

            await AssertReviseEventsAsync(verify, agentRuns[0].Id, agentRuns[0].TeamId, expectedFragment: "reviewer flagged");
            await AssertReviseEventsAsync(verify, agentRuns[1].Id, agentRuns[1].TeamId, expectedFragment: "acceptance check failed");

            // The LIVE checklist tells the healed story per item — Completed states + the contract item's verdict.
            var checklist = await verify.Resolve<IWorkPlanChecklistService>().GetCurrentAsync(runId, teamId, CancellationToken.None);
            checklist!.Items.Count.ShouldBe(2);
            checklist.Items.ShouldAllBe(i => i.State == WorkPlanItemStates.Completed);
            checklist.Items[1].AcceptancePassed.ShouldBe(true, "the checklist chip shows the contract item's PASSED verdict");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, priorToggle);
            using var reset = _fixture.BeginScope();
            reset.Resolve<WorkPlanPlanScript>().Reset();
            reset.Resolve<CriticReviewScript>().Reset();
        }
    }

    // ─── Assertions ──────────────────────────────────────────────────────────

    private static AgentRunResult Result(AgentRun run) =>
        JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;

    private static async Task AssertReviseEventsAsync(ILifetimeScope scope, Guid agentRunId, Guid teamId, string expectedFragment)
    {
        var events = await scope.Resolve<IAgentRunService>().GetEventsAsync(agentRunId, teamId, afterSequence: 0, CancellationToken.None);
        var revise = events.Where(e => e.Text.Contains("revising (round 1 of 1)")).ToList();

        revise.Count.ShouldBe(1, "the revise round announces itself exactly once on the agent's timeline");
        revise[0].Text.ShouldContain(expectedFragment, Case.Insensitive, $"the event names WHICH gate bought the round for agent {agentRunId}");
    }

    // ─── Seeding / projection ────────────────────────────────────────────────

    /// <summary>The launch-shaped projection: standard plan-map-synth with the confirm gate ON, the planner pinned to the work-plan fake, and the agent profile arming BOTH S6 gates on every branch — the Improve critic (pinned reviewer) and, via the plan's authored contract on item 2, the objective oracle; one revise round each.</summary>
    private async Task<Guid> ProjectGatedAndStartAsync(Guid teamId, Guid userId, Guid plannerRowId, Guid criticRowId, Guid repoId)
    {
        using var scope = _fixture.BeginScope();

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = SeedGoal, SurfaceKind = "test", TeamId = teamId },
            Route = new RoutePlan { RecipeKind = TaskRecipeKinds.MapFanout, ProjectionKind = TaskProjectionKinds.PlanMapSynth, Caps = new RouteCaps() },
            AgentProfile = new ResolvedAgentProfile
            {
                Harness = "codex-cli",
                RunnerKind = "local",
                AutonomyLevel = "Confined",
                RepositoryId = repoId,
                OutputReviewMode = ReviewMode.Improve,
                ReviewerModelId = criticRowId,
                ReviseRounds = 1,
            },
            RequirePlanConfirmation = true,
            PlannerModelRowId = plannerRowId,
        };

        var definition = RetargetSynth(scope.Resolve<ITaskProjectionRegistry>().Resolve(context.Route.ProjectionKind).Build(context));

        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, teamId, userId, launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
    }

    /// <summary>Only the SYNTH llm.complete retargets to the plain-text fake — the planner + confirm nodes resolve the work-plan fake through the launch-baked plannerModelId pin (the production path).</summary>
    private static WorkflowDefinition RetargetSynth(WorkflowDefinition definition) => definition with
    {
        Nodes = definition.Nodes.Select(n => n.Id == "synth" ? RetargetProvider(n, DeterministicSynthLlmClient.ProviderTag) : n).ToList(),
    };

    private static NodeDefinition RetargetProvider(NodeDefinition node, string providerTag)
    {
        var config = node.Config.Deserialize<Dictionary<string, JsonElement>>() ?? new();
        config["provider"] = JsonSerializer.SerializeToElement(providerTag);

        return node with { Config = JsonSerializer.SerializeToElement(config) };
    }

    private async Task<WorkPlanConfirmationOutcome> ApproveAsync(Guid runId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<IWorkPlanConfirmationService>().AnswerAsync(runId, teamId, userId, approve: true, feedback: null, CancellationToken.None))!;
    }

    /// <summary>Mirrors the whole-loop E2E's bound-repo seed: a GitHub PAT credential so each branch's clone carries a token — the contract's forced publish and the grader's clone both need it.</summary>
    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new PatPayload { Token = "agent-clone-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId,
            AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    // ─── Fixture plumbing ────────────────────────────────────────────────────

    private void ResetCriticScript()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<CriticReviewScript>().Reset();
    }

    private int CriticCalls()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<CriticReviewScript>().Calls;
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

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local remote seeding <c>check.sh</c> + a base file, with tip-content inspection — what the branches push to and the grader clones from. GUID-suffixed; best-effort cleanup.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-revise-fanout-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedBaseAsync(string checkScript)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await Git(seed, "config", "user.email", "test@codespace.dev");
            await Git(seed, "config", "user.name", "Test");
            await Git(seed, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, "check.sh"), checkScript);
            await File.WriteAllTextAsync(Path.Combine(seed, "base.txt"), "base\n");
            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", "seed");
            await Git(seed, "push", "origin", "main");
        }

        public async Task<string> BranchFileContentAsync(string branch, string file) =>
            await Git(_root, "--git-dir", _bare, "show", $"{branch}:{file}");

        private static async Task<string> Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
