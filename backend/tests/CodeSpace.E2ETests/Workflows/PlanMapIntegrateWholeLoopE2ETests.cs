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
using CodeSpace.Messages.Constants;
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

    /// <summary>
    /// The failure arm of the same loop: ONE item flunks its objective contract while its sibling succeeds. Under
    /// the <c>flow.map</c> schema's <c>terminate</c> default this killed the map — which SKIPPED
    /// <c>git.integrate_run</c> and the reduce, so the surviving item's real, pushed work never became a reviewable
    /// candidate and the run's outputs were empty. With the projection declaring <c>continue</c>, the map finishes,
    /// the integrate step runs over the run's publish ledger, and the reduce narrates with the failure counted.
    ///
    /// <para>Which contributions integrate is a LEDGER question, not an outcome one (<c>RunIntegrationContributions</c>
    /// deliberately applies no outcome filter): a unit that captured a diff contributes even if its own gate later
    /// flunked it, so a human reviews the produced work instead of losing it. What this test pins is the part that
    /// was broken — that the candidate exists at all, and that the SUCCEEDED sibling's work is in its tree.</para>
    /// </summary>
    [Fact]
    public async Task A_flunked_item_still_leaves_its_siblings_work_on_one_reviewable_candidate()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        // s2 carries an objective acceptance whose command does not exist in the seeded tree → it flunks for real.
        using (var knob = _fixture.BeginScope()) knob.Resolve<WorkPlanPlanScript>().AuthorContract = true;

        try
        {
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

            run.Status.ShouldBe(WorkflowRunStatus.Success,
                customMessage: $"one flunked item must not sink the whole fan-out — error: {run.Error}");

            (await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync())
                .Count(r => r.Status == AgentRunStatus.Failed)
                .ShouldBe(1, customMessage: "the contract item really flunked — this arm is worthless if both items simply passed");

            var outputs = JsonDocument.Parse(run.OutputsJson!).RootElement;

            outputs.GetProperty(WorkflowOutputKeys.MapFailed).GetInt32().ShouldBe(1, "the run row counts the failure beside the answer it qualifies");

            var integrationBranch = $"codespace/integration/{runId:N}";
            outputs.GetProperty("integratedBranch").GetString().ShouldBe(integrationBranch,
                customMessage: "the integrate step RAN — under terminate the map's failure skipped it entirely and the produced work stayed fragments");

            (await remote.BranchFileContentAsync(integrationBranch, FileWritingFakeCli.FileFor("do the first thing")))
                .ShouldContain("do the first thing", customMessage: "the surviving item's real work is on the candidate — exactly what one sibling's failure used to discard");

            outputs.GetProperty("combined").GetString().ShouldNotBeNullOrWhiteSpace("the reduce ran too — the run narrates instead of dying at the map");
        }
        finally
        {
            using var reset = _fixture.BeginScope();
            reset.Resolve<WorkPlanPlanScript>().Reset();
        }
    }

    [Fact]
    public async Task A_conflicted_candidate_parks_for_review_and_resumes_to_an_honest_finish()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

        // Two items steered onto the SAME file with different content — their patches REALLY conflict on apply.
        using (var knob = _fixture.BeginScope())
            knob.Resolve<WorkPlanPlanScript>().Instructions = new[] { "update the alpha side", "update the beta side" };

        try
        {
            using var cli = new ConflictThenResolveFakeCli();

            using var remote = new BareRemote();
            await remote.SeedBaseAsync(ConflictThenResolveFakeCli.SharedFile);
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            var jobClient = ResolveJobClient();
            jobClient.Clear();
            jobClient.AutoExecute = true;

            var runId = await ProjectAndStartAsync(teamId, userId, plannerRowId, repoId);

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

            using var mid = _fixture.BeginScope();
            var db = mid.Resolve<CodeSpaceDbContext>();

            // "conflict ⇒ park": the run is Suspended on a REAL approval wait whose payload names the conflict.
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "a conflicted candidate must park for a human, never narrate past silently");

            var wait = await db.WorkflowRunWait.AsNoTracking()
                .SingleAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending);
            wait.WaitKind.ShouldBe(WorkflowWaitKinds.Approval);
            wait.PayloadJson!.ShouldContain(ConflictThenResolveFakeCli.SharedFile, customMessage: "the wait payload names the conflicted file — the review is actionable off the run surface");

            // The human ships the fragments (reject) — the resumed pass re-integrates (still conflicted: nothing
            // changed on the remote) and the run finishes HONESTLY: Success, Conflicted candidate, review trail.
            (await mid.Resolve<Core.Services.Workflows.IWorkflowService>().ApproveRunAsync(runId, teamId, userId, approved: false, comment: "ship the fragments", CancellationToken.None))
                .ShouldBeTrue("the run-level approve verb resolves the integrate node's park");

            await jobClient.WaitForPendingAsync();

            using var verify = _fixture.BeginScope();
            var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

            run.Status.ShouldBe(WorkflowRunStatus.Success, $"after the review the run finishes — error: {run.Error}");

            var outputs = JsonDocument.Parse(run.OutputsJson!).RootElement;
            outputs.GetProperty("integrationStatus").GetString().ShouldBe("Conflicted", "the candidate stays honestly conflicted — reviewed, never laundered");
            outputs.GetProperty("integratedBranch").ValueKind.ShouldBe(JsonValueKind.Null);

            (await verify.Resolve<CodeSpaceDbContext>().PublishManifest.AsNoTracking()
                .Where(m => m.WorkflowRunId == runId && m.Kind == PublishManifestKind.Integration).AnyAsync())
                .ShouldBeFalse("no clean candidate ⇒ no candidate row");

            (await remote.RemoteHasBranchAsync($"codespace/integration/{runId:N}")).ShouldBeFalse("nothing was pushed for a conflicted set — the fragments stay the only branches");
        }
        finally
        {
            using var reset = _fixture.BeginScope();
            reset.Resolve<WorkPlanPlanScript>().Reset();
        }
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

        public async Task SeedBaseAsync(string? extraFile = null)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await Git(seed, "config", "user.email", "test@codespace.dev");
            await Git(seed, "config", "user.name", "Test");
            await Git(seed, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, "base.txt"), "base\n");
            if (extraFile is not null) await File.WriteAllTextAsync(Path.Combine(seed, extraFile), "base\n");
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
