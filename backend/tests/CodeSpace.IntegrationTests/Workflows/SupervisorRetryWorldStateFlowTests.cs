using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): P0-1 retry world-state conservation — a supervisor RETRY of a subtask whose prior
/// attempt already pushed a branch must clone that branch, never a fresh checkout of the repository's default
/// branch. Driven through the REAL <see cref="RealSupervisorActionExecutor"/> against real Postgres, with a REAL
/// prior-attempt agent (real <see cref="AgentRunExecutor"/> + a real local bare git remote) so its
/// <see cref="PublishManifest"/> row carries a GENUINE branch, never a hand-faked one. Proves the forensic root
/// cause of run 96695645: a retry's clone ref is resolved from the SUBTASK'S OWN prior attempt, never silently
/// defaulted while the resume hint implies continuity that isn't actually there.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorRetryWorldStateFlowTests
{
    private const string NodeId = "sup";
    private const string Goal = "ship the retried feature";

    private readonly PostgresFixture _fixture;

    public SupervisorRetryWorldStateFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_retry_of_a_subtask_whose_prior_attempt_pushed_a_branch_clones_that_branch()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (priorAttemptRunId, _) = await RunPriorAttemptAsync(teamId, repoId, runId, "printf 'already done\\n' > done.txt; echo edited");
        var manifest = await SingleManifestAsync(priorAttemptRunId, teamId);
        manifest.Branch.ShouldNotBeNull("PublishMode=Branch + a bound credential → the prior attempt actually pushed");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan("sb"),
            priorAttempt: await FailedAttempt(teamId, "sb", priorAttemptRunId));

        var task = await ExecuteRetryAsync(context, "sb");

        task.Workspace.ShouldNotBeNull("retry world-state conservation pins an explicit clone ref");
        task.Workspace!.Repositories.Single().Ref.ShouldBe(manifest.Branch, "the retry clones the PRIOR ATTEMPT'S OWN branch, never the repository default");
        task.Goal.ShouldContain(manifest.Branch!, customMessage: "the server-authored continuity block names the prior attempt's branch in the agent's prompt");

        (await remote.FileOnBranchAsync(manifest.Branch!, "done.txt")).Trim().ShouldBe("already done", "the branch pinned really does contain the prior attempt's committed work");
    }

    [Fact]
    public async Task A_retry_of_a_patch_only_prior_attempt_stays_on_the_default_branch_with_an_honest_redo_hint()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        // PatchOnly blocks the push — the prior attempt's work lives ONLY in its recorded patch, never a branch.
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.PatchOnly);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (priorAttemptRunId, _) = await RunPriorAttemptAsync(teamId, repoId, runId, "printf 'patched only\\n' > patched.txt; echo edited");
        var manifest = await SingleManifestAsync(priorAttemptRunId, teamId);
        manifest.Branch.ShouldBeNull("the repo policy blocked the push — no branch to continue from");

        // Stamp a resumable session onto the prior attempt (the scripted harness itself carries no session id) so
        // the honest-redo hint's OTHER half — "your conversation IS restored" — is genuinely exercised too.
        await StampResumableSessionAsync(priorAttemptRunId, "sess-sb-patch-only", "the patch-only attempt's conversation\n");

        var context = ContextWith(runId, teamId, repoId,
            plan: Plan("sb"),
            priorAttempt: await FailedAttempt(teamId, "sb", priorAttemptRunId));

        var task = await ExecuteRetryAsync(context, "sb");

        task.Workspace.ShouldBeNull("no pushed branch to pin — the retry keeps the byte-identical default-branch clone");
        task.ResumeFromSessionId.ShouldBe("sess-sb-patch-only", "the conversation is still resumed");
        task.Goal.ShouldContain(RealSupervisorActionExecutor.HonestNoContinuityHint, customMessage: "the goal must HONESTLY say the git changes were NOT preserved, since the conversation restore alone could otherwise imply the work is already on disk");
    }

    [Fact]
    public async Task A_retry_with_no_prior_attempt_at_all_is_a_byte_identical_cold_start()
    {
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        // No prior spawn/retry decision at all names "sb" — a genuine cold-start retry (e.g. the model retries a
        // subtask that was planned but never actually staged).
        var context = ContextWith(runId, teamId, repoId, plan: Plan("sb"), priorAttempt: null);

        var task = await ExecuteRetryAsync(context, "sb");

        task.Workspace.ShouldBeNull("no prior attempt exists — the default-branch clone stands, exactly as before P0-1");
    }

    [Fact]
    public async Task With_two_recorded_attempts_the_git_ref_always_matches_the_conversation_being_resumed()
    {
        // The literal-latest attempt (by decision order) and the RESUMABLE attempt can be two DIFFERENT prior runs —
        // e.g. the newest attempt crashed with no session while an older one both pushed a branch AND is resumable.
        // The git-staging lookup must key off the SAME attempt whose conversation is restored, never the independently-
        // resolved "latest" one, or the honest-redo hint would assert a falsehood while discarding a real branch.
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedWithOneCommitAsync();
        var repoId = await SeedRepositoryAsync(teamId, remote.Url, await SeedCredentialAsync(teamId), RepositoryPublishMode.Branch);
        var runId = await SeedSupervisorRunAsync(teamId);

        var (olderAttemptRunId, _) = await RunPriorAttemptAsync(teamId, repoId, runId, "printf 'older attempt work\\n' > older.txt; echo edited");
        var olderManifest = await SingleManifestAsync(olderAttemptRunId, teamId);
        olderManifest.Branch.ShouldNotBeNull("the older attempt genuinely pushed");
        await StampResumableSessionAsync(olderAttemptRunId, "sess-older", "the older attempt's conversation\n");

        // The NEWER attempt's script exits non-zero (no commit, no manifest branch) and is never stamped with a
        // session — a realistic "crashed before publishing, no conversation captured" shape.
        var (newerAttemptRunId, _) = await RunFailingPriorAttemptAsync(teamId, repoId, runId, "exit 1");

        var context = new SupervisorTurnContext
        {
            Goal = Goal, SupervisorRunId = runId, TeamId = teamId, NodeId = NodeId, TurnNumber = 3,
            PriorDecisions = new[] { Plan("sb"), await FailedAttempt(teamId, "sb", olderAttemptRunId), await RetriedAttempt(teamId, "sb", newerAttemptRunId) },
            AgentProfile = new CodeSpace.Messages.Dtos.Agents.SupervisorAgentProfile { RepositoryId = repoId },
        };

        var task = await ExecuteRetryAsync(context, "sb");

        task.ResumeFromSessionId.ShouldBe("sess-older", "the only resumable attempt is the older one — the newer crashed with no session");
        task.Workspace.ShouldNotBeNull("the older (resumable) attempt's own branch must be pinned");
        task.Workspace!.Repositories.Single().Ref.ShouldBe(olderManifest.Branch, "the git ref belongs to the SAME attempt whose conversation is being resumed, never the literal-latest attempt's (which has no branch at all)");
        task.Goal.ShouldNotContain(RealSupervisorActionExecutor.HonestNoContinuityHint, customMessage: "the resumed attempt's own branch IS preserved — asserting otherwise would be a lie");
    }

    // ─── Drive the real executor ──────────────────────────────────────────────────

    private async Task<AgentTask> ExecuteRetryAsync(SupervisorTurnContext context, string subtaskId)
    {
        using var scope = _fixture.BeginScope();
        var executor = scope.Resolve<ISupervisorActionExecutor>();

        var payload = JsonSerializer.Serialize(new SupervisorRetryPayload { SubtaskId = subtaskId }, AgentJson.Options);
        var decision = new SupervisorDecision { Kind = SupervisorDecisionKinds.Retry, PayloadJson = payload };

        await executor.ExecuteAsync(decision, context, CancellationToken.None);

        var run = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == context.SupervisorRunId && r.NodeId == NodeId)
            .OrderByDescending(r => r.CreatedDate).FirstAsync();

        return JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)!;
    }

    // ─── Context / decision-tape builders ─────────────────────────────────────────

    private static SupervisorTurnContext ContextWith(Guid runId, Guid teamId, Guid repositoryId, SupervisorPriorDecision plan, SupervisorPriorDecision? priorAttempt) => new()
    {
        Goal = Goal,
        SupervisorRunId = runId,
        TeamId = teamId,
        NodeId = NodeId,
        TurnNumber = 3,
        PriorDecisions = priorAttempt is null ? new[] { plan } : new[] { plan, priorAttempt },
        AgentProfile = new CodeSpace.Messages.Dtos.Agents.SupervisorAgentProfile { RepositoryId = repositoryId },
    };

    private static SupervisorPriorDecision Plan(string subtaskId)
    {
        var payload = JsonSerializer.Serialize(new SupervisorPlanPayload
        {
            Goal = Goal,
            Subtasks = new List<SupervisorPlannedSubtask> { new() { Id = subtaskId, Title = subtaskId, Instruction = $"do {subtaskId}" } },
        }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = "{}" };
    }

    /// <summary>A prior FAILED spawn recording (subtaskId, REAL agentRunId) — the positional subtaskIds[i] ↔ agentResults[i] shape <see cref="SupervisorDependencyGate"/> reads to find "this subtask's latest attempt", UNFILTERED on success (the whole point of a retry).</summary>
    private async Task<SupervisorPriorDecision> FailedAttempt(Guid teamId, string subtaskId, Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        var manifests = await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None);

        var result = new SupervisorAgentResult { AgentRunId = agentRunId, Status = "Failed", Error = "acceptance failed", ProducedBranch = manifests.FirstOrDefault()?.Branch };

        var payload = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = new[] { subtaskId } }, AgentJson.Options);
        var outcome = JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = outcome };
    }

    /// <summary>A prior FAILED retry recording (subtaskId, REAL agentRunId), SEQUENCED AFTER <see cref="FailedAttempt"/> — the decision-tape-literal "latest attempt" <see cref="SupervisorDependencyGate.LatestAgentRunId"/> reads, independent of whether it is actually resumable.</summary>
    private async Task<SupervisorPriorDecision> RetriedAttempt(Guid teamId, string subtaskId, Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        var manifests = await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None);

        var result = new SupervisorAgentResult { AgentRunId = agentRunId, Status = "Failed", Error = "crashed before publishing", ProducedBranch = manifests.FirstOrDefault()?.Branch };

        var payload = JsonSerializer.Serialize(new SupervisorRetryPayload { SubtaskId = subtaskId }, AgentJson.Options);
        var outcome = JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Retry, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = outcome };
    }

    // ─── Real prior-attempt execution (real AgentRunExecutor + real git) ──────────

    /// <summary>Run ONE real "prior attempt" agent (a scripted /bin/sh harness) through the REAL AgentRunExecutor against the real repo — a genuine PublishManifest row + (PublishMode-dependent) a genuine pushed branch results. Stamped with <see cref="AgentTask.SubtaskId"/> so <see cref="IAgentRunService.FindResumableSubtaskAttemptAsync"/> can find it by subtask id, exactly as a real supervisor-spawned agent would be. Mirrors <see cref="SupervisorDependencyStagingFlowTests.RunProducerAsync"/>.</summary>
    private async Task<(Guid AgentRunId, string ResultJson)> RunPriorAttemptAsync(Guid teamId, Guid repositoryId, Guid supervisorRunId, string script, string subtaskId = "sb")
    {
        var finished = await RunScriptedAttemptAsync(teamId, repositoryId, supervisorRunId, script, subtaskId);
        finished.Status.ShouldBe(AgentRunStatus.Succeeded, "the prior attempt must genuinely push for its manifest to carry a real branch");

        return (finished.Id, finished.ResultJson!);
    }

    /// <summary>The multi-attempt-divergence scenario's NEWER attempt: crashes (non-zero exit) before ever publishing — no manifest row, no session, exactly the "captured nothing" shape a real infra failure leaves.</summary>
    private async Task<(Guid AgentRunId, string ResultJson)> RunFailingPriorAttemptAsync(Guid teamId, Guid repositoryId, Guid supervisorRunId, string script, string subtaskId = "sb")
    {
        var finished = await RunScriptedAttemptAsync(teamId, repositoryId, supervisorRunId, script, subtaskId);
        finished.Status.ShouldBe(AgentRunStatus.Failed, "the scripted harness's non-zero exit is a genuine failure, never silently treated as success");

        return (finished.Id, finished.ResultJson!);
    }

    private async Task<AgentRun> RunScriptedAttemptAsync(Guid teamId, Guid repositoryId, Guid supervisorRunId, string script, string subtaskId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "produce", Harness = "scripted", Model = "test-model", RepositoryId = repositoryId, SubtaskId = subtaskId },
            teamId, supervisorRunId, NodeId, iterationKey: "", cancellationToken: CancellationToken.None);

        var executor = new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            new AgentHarnessRegistry(new IAgentHarness[] { new ScriptedHarness(script) }),
            new HarnessModelReconciler(new AgentHarnessRegistry(new IAgentHarness[] { new ScriptedHarness(script) }), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            scope.Resolve<IPublishManifestStore>(),
            scope.Resolve<IEnumerable<IPublishGuard>>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(run.Id, CancellationToken.None);

        return await scope.Resolve<IAgentRunService>().GetAsync(run.Id, CancellationToken.None);
    }

    private async Task<PublishManifest> SingleManifestAsync(Guid agentRunId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None)).ShouldHaveSingleItem();
    }

    /// <summary>Stamp a resumable session onto an already-terminal agent run — the scripted harness itself carries no session id, so this mirrors what a real Claude-harness run would have persisted on completion (<c>AgentRun.SessionId</c> + the result's <c>SessionTranscript</c>).</summary>
    private async Task StampResumableSessionAsync(Guid agentRunId, string sessionId, string transcript)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.AgentRun.SingleAsync(r => r.Id == agentRunId);
        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;

        run.SessionId = sessionId;
        run.ResultJson = JsonSerializer.Serialize(result with { SessionId = sessionId, SessionTranscript = transcript }, AgentJson.Options);

        await db.SaveChangesAsync();
    }

    // ─── Seeding (team / credential / repository / supervisor run) ────────────────

    private async Task<Guid> SeedTeamAsync() => (await WorkflowsTestSeed.SeedTeamAsync(_fixture)).TeamId;

    private async Task<Guid> SeedCredentialAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = await FindOrCreateProviderInstanceAsync(db, teamId);

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new PatPayload { Token = "retry-world-state-e2e-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId,
            AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        await db.SaveChangesAsync();
        return credentialId;
    }

    private async Task<Guid> FindOrCreateProviderInstanceAsync(CodeSpaceDbContext db, Guid teamId)
    {
        var existing = await db.ProviderInstance.Where(p => p.TeamId == teamId).Select(p => p.Id).FirstOrDefaultAsync();
        if (existing != Guid.Empty) return existing;

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });
        await db.SaveChangesAsync();
        return instanceId;
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId, string cloneUrl, Guid credentialId, RepositoryPublishMode publishMode)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = await FindOrCreateProviderInstanceAsync(db, teamId);

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrl, WebUrl = "https://local/org/repo",
            PublishMode = publishMode,
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var (_, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scopeAsAdmin = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var workflowId = await scopeAsAdmin.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-retry-world-state-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json($$"""{"goal":"{{Goal}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition> { new() { From = "start", To = NodeId }, new() { From = NodeId, To = "end" } },
            },
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    // ─── Git helpers ────────────────────────────────────────────────────────────

    private static async Task<bool> GitAvailableAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local repo standing in for the agents' remote, plus ref inspection via <c>git --git-dir</c> — real-git ground truth. Mirrors <see cref="SupervisorDependencyStagingFlowTests.BareRemote"/>.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-retry-world-state-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedWithOneCommitAsync(string fileName = "README.md", string content = "base")
        {
            await RunGitAsync(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await RunGitAsync(seed, "clone", _bare, seed);
            await RunGitAsync(seed, "config", "user.email", "test@codespace.dev");
            await RunGitAsync(seed, "config", "user.name", "Test");
            await RunGitAsync(seed, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, fileName), content);
            await RunGitAsync(seed, "add", ".");
            await RunGitAsync(seed, "commit", "-m", "seed");
            await RunGitAsync(seed, "push", "origin", "main");
        }

        public Task<string> FileOnBranchAsync(string branch, string file) => RunGitAsync(_root, "--git-dir", _bare, "show", $"{branch}:{file}");

        private static async Task<string> RunGitAsync(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(
                new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>A CLI-less test harness: builds a /bin/sh invocation from a fixed script. Mirrors <see cref="SupervisorDependencyStagingFlowTests.ScriptedHarness"/>.</summary>
    private sealed class ScriptedHarness : IAgentHarness
    {
        private readonly string _script;

        public ScriptedHarness(string script) => _script = script;

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", _script }, WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" };
    }
}
