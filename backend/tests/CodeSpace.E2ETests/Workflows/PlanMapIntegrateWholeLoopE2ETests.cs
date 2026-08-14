using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
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
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 THE plan-map integrated-candidate whole-loop E2E (P4): the PRODUCTION plan-map-synth graph — real
/// <c>plan.author</c> → real <c>flow.map</c> fan-out → REAL OS-process agents that clone a REAL bare remote,
/// write REAL files, and push REAL per-item branches → the <c>git.integrate_run</c> step REALLY integrates both
/// patches onto one reviewable branch on that remote → the synth narrates → the terminal surfaces the candidate.
/// Until this arm, every repo-bound plan-map E2E ran the integrate step and asserted NOTHING about it — the
/// whole point of the step (ONE reviewable head instead of K fragments) was silently unproven.
///
/// <para>Fidelity (Rule 12) — HIGH: real engine + real Postgres + real projection builder + real
/// <c>AgentRunExecutor</c>/<c>LocalProcessRunner</c> + real git clone/push/apply. Deterministic fakes only at
/// the planner LLM (the work-plan script's default two-item plan), the synth LLM (provider retarget), and the
/// CLI's intelligence (<see cref="FileWritingFakeCli"/> — each item writes its own goal-slugged file, so the
/// integrated tree provably carries BOTH items' work). POSIX-only; skips when git is absent.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class PlanMapIntegrateWholeLoopE2ETests
{
    private const string SeedGoal = "Improve the module across both fronts";

    private readonly PostgresFixture _fixture;

    public PlanMapIntegrateWholeLoopE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_repo_bound_fan_out_integrates_both_items_onto_one_reviewable_branch()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitAvailableAsync()) return;    // real git required for clone/push/integrate

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

        using var cli = new FileWritingFakeCli();

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

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success, $"the whole loop must land Success — error: {run.Error}");

        // Both fan-out items really produced and pushed their own branch (the integrate step's inputs exist).
        var agentManifests = await db.PublishManifest.AsNoTracking()
            .Where(m => m.WorkflowRunId == runId && m.Kind == PublishManifestKind.Agent).ToListAsync();
        agentManifests.Count.ShouldBe(2, "two plan items ⇒ two per-agent pushes");
        agentManifests.ShouldAllBe(m => m.PublishStateValue == PublishState.Pushed);

        // THE CANDIDATE — one reviewable branch on the REAL remote, its tree carrying BOTH items' files.
        var integrationBranch = $"codespace/integration/{runId:N}";
        (await remote.RemoteHasBranchAsync(integrationBranch)).ShouldBeTrue(
            $"the integrate step must push the run's unique integrated candidate; remote branches: [{string.Join(", ", await remote.ListBranchesAsync())}]");

        (await remote.BranchFileContentAsync(integrationBranch, FileWritingFakeCli.FileFor("do the first thing")))
            .ShouldContain("do the first thing", customMessage: "the integrated tree carries item 1's work");
        (await remote.BranchFileContentAsync(integrationBranch, FileWritingFakeCli.FileFor("do the second thing")))
            .ShouldContain("do the second thing", customMessage: "…and item 2's — ONE head, not fragments");

        // The durable candidate fact: the run-level Integration manifest row.
        var candidate = (await db.PublishManifest.AsNoTracking()
            .Where(m => m.WorkflowRunId == runId && m.Kind == PublishManifestKind.Integration).ToListAsync()).ShouldHaveSingleItem();
        candidate.Branch.ShouldBe(integrationBranch);
        candidate.PublishStateValue.ShouldBe(PublishState.Pushed);

        // The run's own outputs surface the candidate beside the narrated reduce.
        var outputs = JsonDocument.Parse(run.OutputsJson!).RootElement;
        outputs.GetProperty("integrationStatus").GetString().ShouldBe("Clean");
        outputs.GetProperty("integratedBranch").GetString().ShouldBe(integrationBranch);
        outputs.GetProperty("combined").GetString().ShouldNotBeNullOrWhiteSpace("the synth still narrates — the code reduce rides beside it, not instead of it");
    }

    // ─── Projection (the production builder, planner pinned to the work-plan fake, synth retargeted) ───

    private async Task<Guid> ProjectAndStartAsync(Guid teamId, Guid userId, Guid plannerRowId, Guid repoId)
    {
        using var scope = _fixture.BeginScope();

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = SeedGoal, SurfaceKind = "test", TeamId = teamId },
            Route = new RoutePlan { RecipeKind = TaskRecipeKinds.MapFanout, ProjectionKind = TaskProjectionKinds.PlanMapSynth, Caps = new RouteCaps() },
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Confined", RepositoryId = repoId },
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

    // ─── Seeding / plumbing (the repo-bound whole-loop recipe) ───

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
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-planmap-integrate-" + Guid.NewGuid().ToString("N"));
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

        public async Task<bool> RemoteHasBranchAsync(string branch) =>
            (await Git(_root, "--git-dir", _bare, "branch", "--list", branch)).Contains(branch);

        public async Task<IReadOnlyList<string>> ListBranchesAsync() =>
            (await Git(_root, "--git-dir", _bare, "branch", "--format=%(refname:short)"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        public async Task<string> BranchFileContentAsync(string branch, string file) =>
            await Git(_root, "--git-dir", _bare, "show", $"{branch}:{file}");

        private static async Task<string> Git(string cwd, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = cwd, TimeoutSeconds = 60 }, CancellationToken.None);
            result.Status.ShouldBe(SandboxStatus.Success, $"git {string.Join(' ', args)} failed: {result.Stderr}");
            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
