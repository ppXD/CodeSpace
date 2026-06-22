using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 THE whole-loop supervisor E2E — the join the coverage audit found was NEVER tested at any tier: a supervisor
/// driving REAL OS-process agents that PRODUCE A REAL PATCH, through a REAL git merge, gated by REAL objective
/// acceptance. Every prior test proves ONE half — <see cref="SupervisorRealAgentE2ETests"/> runs real agents but the
/// fake CLI writes no files (no patch), and <c>SupervisorMergeIntegrateFlowTests</c> integrates real git but SEEDS the
/// agent results. This test deletes both gaps: the 2 supervisor-spawned agents run through the production
/// <see cref="AgentRunExecutor"/> + real <c>LocalProcessRunner</c>, each EDITS A FILE in its cloned workspace
/// (<see cref="FileWritingFakeCli"/>), the executor's real git-diff captures each as a real
/// <see cref="AgentRunResult.Patch"/> + pushed branch, the supervisor's <c>merge</c> turn really integrates them into
/// one reviewable branch on the bare remote, and the terminal <c>stop</c> grades that integrated branch against a real
/// <c>check.sh</c> acceptance floor before declaring success.
///
/// <para><c>trigger.manual</c> → <c>agent.supervisor</c> (scripted plan→spawn→merge→stop) → terminal. Driven as ONE
/// <c>RunEngineAsync</c> + <c>WaitForPendingAsync</c> drain (the in-memory job client chains every self-advance,
/// executor dispatch, and barrier resume through one FIFO queue).</para>
///
/// <para><b>Fidelity (Rule 12) — HIGH.</b> Real engine, real Postgres, real <see cref="SupervisorTurnService"/> +
/// <see cref="RealSupervisorActionExecutor"/>, real <see cref="AgentRunExecutor"/> + real <c>LocalProcessRunner</c>
/// spawning a real OS process in a real cloned git workspace, real <c>LocalGitBranchIntegrator</c> against a bare
/// <c>file://</c> remote, real <c>SupervisorAcceptanceGrader</c> running <c>check.sh</c>. Two things are faked at honest
/// boundaries: the supervisor's DECISIONS (<see cref="ScriptedSupervisorDecider"/> — this slice is the deterministic
/// skeleton; the live-model brain is the follow-up) and the CLI's INTELLIGENCE (the fake codex writes a deterministic
/// file). A break in ANY seam — spawn → real agent edit → patch capture → merge integrate → acceptance grade → stop —
/// fails this test. POSIX-only (the fake CLI is <c>/bin/sh</c>); skipped on Windows / when git is absent.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class SupervisorWholeLoopE2ETests : IDisposable
{
    private const string NodeId = "sup";

    private readonly PostgresFixture _fixture;
    private readonly string? _laneBefore;
    private readonly string? _integrateBefore;

    public SupervisorWholeLoopE2ETests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _laneBefore = Environment.GetEnvironmentVariable(SupervisorLane.EnabledEnvVar);
        _integrateBefore = Environment.GetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar);
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, "1");
        Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, "1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, _laneBefore);
        Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, _integrateBefore);

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = true;
    }

    [Fact]
    public async Task Supervisor_drives_real_agents_to_a_real_patch_a_real_merge_and_a_passing_acceptance_gate()
    {
        if (OperatingSystem.IsWindows()) return;          // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitReadyAsync()) return;               // real git is required for clone/capture/integrate

        using var cli = new FileWritingFakeCli();         // each spawned agent EDITS a distinct file in its workspace

        SetDecisionScript(s => s.PlanSpawnMergeStop());

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 0\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        await AssertRunReachedSuccessAsync(runId);
        await AssertBothAgentsProducedRealPatchesAsync(runId);
        await AssertDecisionLedgerIsPlanSpawnMergeStopAsync(runId, teamId);
        await AssertIntegratedBranchOnRemoteAsync(remote, runId);
        await AssertAcceptancePassedOnStopAsync(runId, teamId);
    }

    [Fact]
    public async Task Supervisor_withholds_the_reviewable_branch_when_the_real_acceptance_floor_fails()
    {
        if (OperatingSystem.IsWindows()) return;          // the fake CLI is a /bin/sh script the runner spawns
        if (!await GitReadyAsync()) return;               // real git is required for clone/capture/integrate/grade

        using var cli = new FileWritingFakeCli();

        SetDecisionScript(s => s.PlanSpawnMergeStop());

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var remote = new BareRemote();
        // SAME happy-loop wiring — the ONLY change is the operator's acceptance floor REJECTS the integrated head
        // (check.sh exits 1). The whole real arc (spawn → real patch → real merge) still runs; the difference is the
        // objective gate must catch the broken head and WITHHOLD it.
        await remote.SeedBaseAsync(new() { ["check.sh"] = "#!/bin/sh\nexit 1\n", ["base.txt"] = "base\n" });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url, "main");

        var workflowId = await CreateWholeLoopWorkflowAsync(teamId, userId, repoId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        // The agents still did real work and the merge still integrated — the gate is the LAST thing, not a short-circuit.
        await AssertRunReachedSuccessAsync(runId);
        await AssertBothAgentsProducedRealPatchesAsync(runId);
        await AssertIntegratedBranchOnRemoteAsync(remote, runId);

        // The objective floor FAILED against the real grader → the stop's grade is false, the node reports
        // AcceptanceFailed, and the reviewable branch is WITHHELD (a downstream git.open_pr binds "" → nothing). The
        // safety floor provably stops a broken head from ever reaching a PR — end to end through real git.
        await AssertAcceptanceFailedAndBranchWithheldAsync(runId, teamId);
    }

    // ─── Assertions ──────────────────────────────────────────────────────────────────

    private async Task AssertRunReachedSuccessAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the supervisor→real-agent→patch→merge→acceptance→stop arc must reach Success; if not, inspect the AgentRun.Error + failed WorkflowRunNode rows + the supervisor decision outcomes");
    }

    private async Task AssertBothAgentsProducedRealPatchesAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var results = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId)
            .Select(r => r.ResultJson).ToListAsync();

        results.Count.ShouldBe(2, "spawn[both] staged exactly 2 real agent runs");

        foreach (var json in results)
        {
            json.ShouldNotBeNull("each real agent persisted a folded AgentRunResult");
            var result = System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(json!, AgentJson.Options)!;
            result.Status.ShouldBe(AgentRunStatus.Succeeded);
            result.Patch.ShouldNotBeNullOrWhiteSpace("the executor's real git-diff captured the file the fake CLI wrote — a real unified diff, not a seeded stand-in");
            result.ProducedBranch.ShouldNotBeNullOrWhiteSpace("the real agent's change was published as its own branch");
            result.ChangedFiles.ShouldContain(f => f.StartsWith(FileWritingFakeCli.FilePrefix), "the captured diff names the agent's written file");
        }
    }

    private async Task AssertDecisionLedgerIsPlanSpawnMergeStopAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        IReadOnlyList<string> kinds = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence)
            .Select(d => d.DecisionKind)
            .ToListAsync();

        kinds.ShouldBe(
            new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop },
            customMessage: "the ledger must record plan/spawn/merge/stop in order — the merge turn only advanced because both real agents completed through the barrier, and the stop only succeeded because acceptance passed");
    }

    private async Task AssertIntegratedBranchOnRemoteAsync(BareRemote remote, Guid runId)
    {
        // The merge turn is sequence 2 → the integrator's reviewable branch is codespace/integration/<run>/turn2.
        var branch = $"codespace/integration/{runId:N}/turn2";
        (await remote.RemoteHasBranchAsync(branch)).ShouldBeTrue(
            $"the supervisor's merge really integrated both agents' real patches and pushed {branch} to the bare remote");
    }

    private async Task AssertAcceptancePassedOnStopAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var stop = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .OrderByDescending(d => d.Sequence)
            .FirstAsync();

        SupervisorOutcome.ReadAcceptanceGradePassed(stop.OutcomeJson).ShouldBe(true,
            "the terminal stop graded the integrated branch against the real check.sh operator floor and it PASSED");
    }

    private async Task AssertAcceptanceFailedAndBranchWithheldAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var stop = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .OrderByDescending(d => d.Sequence)
            .FirstAsync();

        SupervisorOutcome.ReadAcceptanceGradePassed(stop.OutcomeJson).ShouldBe(false,
            "the integrated head failed the real check.sh floor → the stop's objective grade is FALSE, not a self-reported success");

        // The terminal supervisor node output: status=AcceptanceFailed + the reviewable branch withheld to "".
        var supRows = await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId && n.NodeId == NodeId).ToListAsync();
        var terminal = supRows
            .Select(n => System.Text.Json.JsonDocument.Parse(n.OutputsJson).RootElement)
            .First(o => o.TryGetProperty("status", out _));

        terminal.GetProperty("status").GetString().ShouldBe("AcceptanceFailed",
            "the supervisor reports the objective definition-of-done was NOT met — not a self-reported Completed");
        terminal.GetProperty("integratedBranch").GetString().ShouldBe("",
            "the reviewable branch is WITHHELD — a head that fails the operator floor must never be handed to a downstream PR-open");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────

    private void SetDecisionScript(Action<SupervisorDecisionScript> set)
    {
        using var scope = _fixture.BeginScope();
        set(scope.Resolve<SupervisorDecisionScript>());
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<Guid> CreateWholeLoopWorkflowAsync(Guid teamId, Guid userId, Guid repoId)
    {
        // The supervisor's agents clone repoId, push their branches, and the merge integrates them; the operator's
        // acceptance floor (check.sh) gates the terminal stop against the integrated head.
        var supConfig = $$"""
            {
              "goal": "ship the feature",
              "agentProfile": { "repositoryId": "{{repoId}}", "pushBranch": true, "integrateBranches": true },
              "acceptanceChecks": ["sh", "check.sh"]
            }
            """;

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-wholeloop-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(supConfig), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps, string defaultBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = $"https://local/{instanceId:N}" });

        var serializer = scope.Resolve<CodeSpace.Core.Services.Credentials.ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();
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
            DefaultBranch = defaultBranch, CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local repo standing in for the remote — base-seeding + ref inspection. GUID-suffixed; best-effort cleaned.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-sup-wholeloop-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedBaseAsync(Dictionary<string, string> files)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);
            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await Config(seed);
            foreach (var (name, content) in files) await File.WriteAllTextAsync(Path.Combine(seed, name), content);
            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", "seed");
            await Git(seed, "push", "origin", "main");
        }

        public async Task<bool> RemoteHasBranchAsync(string branch) =>
            (await Git(_root, "--git-dir", _bare, "branch", "--list", branch)).Trim().Length > 0;

        private static async Task Config(string dir)
        {
            await Git(dir, "config", "user.email", "test@codespace.dev");
            await Git(dir, "config", "user.name", "Test");
            await Git(dir, "config", "commit.gpgsign", "false");
        }

        private static async Task<string> Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
            if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");
            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
