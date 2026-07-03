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
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 THE non-coding closed-loop E2E (triad S7 × S6) — a RESEARCH deliverable healed by the RUBRIC JUDGE through
/// the in-run revise loop, end to end through the REAL engine: the standard tier's planner authors ONE research
/// item whose acceptance is an <c>LlmJudge</c> contract (deliverable path + one-criterion rubric + pinned judge
/// row), the map fans out a real agent whose FIRST attempt writes a report that flunks the rubric (no
/// <c>MEETS[healed]</c> marker), the executor's oracle gate runs the REAL <c>LlmRubricJudge</c> against the pushed
/// branch's committed file, the failure detail — naming the unmet criterion — feeds the S6 revise round, the
/// revision writes the satisfying content, the re-pushed branch RE-JUDGES to a pass, and the run lands Success
/// with the checklist verdict stamped.
///
/// <para>This is the triad's whole thesis on one run: plan authors the contract (sprint-contract), the agent
/// generates, the evaluator (a rubric judge — subjective step contained in binary evidence-backed criteria)
/// verifies, and the generator↔evaluator loop closes WITHOUT a human. Fidelity (Rule 12) — HIGH: real engine +
/// real Postgres + real <c>plan.author</c> + real fan-out + real <c>AgentRunExecutor</c>/<c>LocalProcessRunner</c>
/// + real git push/clone + real judge resolution through the pool; deterministic fakes only at the planner LLM,
/// the judge's network call (content-keyed — the verdict flips only when the file really changes), and the CLI's
/// intelligence. POSIX-only; skips when git is absent.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class RubricReviseFanoutE2ETests
{
    private const string SeedGoal = "Research the competitive landscape and write the report";

    private readonly PostgresFixture _fixture;

    public RubricReviseFanoutE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_research_item_heals_through_the_revise_loop_against_the_rubric_judge()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitAvailableAsync()) return;    // real git required for clone/push/grade

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);
        var (_, judgeRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "judge-model", provider: DeterministicJudgeLlmClient.ProviderTag);

        using (var knob = _fixture.BeginScope())
        {
            var script = knob.Resolve<WorkPlanPlanScript>();
            script.AuthorRubricContract = true;
            script.RubricJudgeModelId = judgeRowId;   // the fake can't know the seeded row — the test pins it
        }

        try
        {
            using var cli = new ReviseHealingFakeCli();

            using var remote = new BareRemote();
            await remote.SeedBaseAsync();
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            var jobClient = ResolveJobClient();
            jobClient.Clear();
            jobClient.AutoExecute = true;

            var runId = await ProjectAndStartAsync(teamId, userId, plannerRowId, repoId);

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "the flawed first report was healed by the revise round and the RE-JUDGE passed");

            var agentRun = await db.AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == runId);
            agentRun.Status.ShouldBe(AgentRunStatus.Succeeded);

            var result = JsonSerializer.Deserialize<AgentRunResult>(agentRun.ResultJson!, AgentJson.Options)!;
            result.ReviseRounds.ShouldBe(1, "the rubric failure bought exactly one revise round");
            result.AcceptancePassed.ShouldBe(true, "the revised deliverable passed the REAL judge on the re-pushed branch");
            result.AcceptanceDetail!.ShouldContain("1/1 criteria met");
            result.ProducedBranch.ShouldNotBeNull();

            (await remote.BranchFileContentAsync(result.ProducedBranch!, ReviseHealingFakeCli.FileName))
                .ShouldContain(DeterministicJudgeLlmClient.MeetsMarker(DeterministicWorkPlanLlmClient.RubricCriterionId),
                    customMessage: "the remote branch tip carries the content that actually satisfies the rubric");

            var events = await verify.Resolve<IAgentRunService>().GetEventsAsync(agentRun.Id, agentRun.TeamId, afterSequence: 0, CancellationToken.None);
            var revise = events.Single(e => e.Text.Contains("revising (round 1 of 1)"));
            revise.Text.ShouldContain($"[{DeterministicWorkPlanLlmClient.RubricCriterionId}]", customMessage: "the fed-back failure NAMES the unmet criterion — the agent revises against the rubric's own words");

            var checklist = await verify.Resolve<IWorkPlanChecklistService>().GetCurrentAsync(runId, teamId, CancellationToken.None);
            checklist!.Items.Count.ShouldBe(1);
            checklist.Items[0].State.ShouldBe(WorkPlanItemStates.Completed);
            checklist.Items[0].AcceptancePassed.ShouldBe(true, "the checklist chip shows the healed item's PASSED rubric verdict");
        }
        finally
        {
            using var reset = _fixture.BeginScope();
            reset.Resolve<WorkPlanPlanScript>().Reset();
        }
    }

    // ─── Projection ──────────────────────────────────────────────────────────

    /// <summary>Standard plan-map-synth, planner pinned to the work-plan fake, ONE revise round on the branch agents; synth retargeted to the plain-text fake.</summary>
    private async Task<Guid> ProjectAndStartAsync(Guid teamId, Guid userId, Guid plannerRowId, Guid repoId)
    {
        using var scope = _fixture.BeginScope();

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = SeedGoal, SurfaceKind = "test", TeamId = teamId },
            Route = new RoutePlan { RecipeKind = TaskRecipeKinds.MapFanout, ProjectionKind = TaskProjectionKinds.PlanMapSynth, Caps = new RouteCaps() },
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Confined", RepositoryId = repoId, ReviseRounds = 1 },
            PlannerModelRowId = plannerRowId,
        };

        var definition = RetargetSynth(scope.Resolve<ITaskProjectionRegistry>().Resolve(context.Route.ProjectionKind).Build(context));

        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, teamId, userId, launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
    }

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

    // ─── Seeding / plumbing ──────────────────────────────────────────────────

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId,
            AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "agent-clone-token" })), Status = CredentialStatus.Active,
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

    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-rubric-revise-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedBaseAsync()
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await Git(seed, "config", "user.email", "test@codespace.dev");
            await Git(seed, "config", "user.name", "Test");
            await Git(seed, "config", "commit.gpgsign", "false");
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
