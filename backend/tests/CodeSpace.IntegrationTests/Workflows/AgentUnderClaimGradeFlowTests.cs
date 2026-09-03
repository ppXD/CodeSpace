using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): D4b's UNDER-CLAIM fold end to end through the REAL <see cref="AgentRunExecutor"/> —
/// real <see cref="LocalProcessRunner"/> running a real <c>/bin/sh</c> agent in a real cloned workspace off a real
/// bare git remote, the REAL <c>SupervisorAcceptanceGrader</c> applying the run's own recorded patch on an
/// agent-independent clone and executing the contract's <c>check.sh</c>, real Postgres holding the result, and the
/// REAL <see cref="AgentMetricsReader"/> projection the journal agent card reads. The ONE faked seam is the agent's
/// own JUDGEMENT: a scripted harness that does the work and then reports failure — exactly the shape that used to
/// terminalize Failure with the work discarded, because a self-reported failure was never graded at all. The run is
/// graded on its recorded PATCH (a Failed run has no branch when the oracle runs) and, once the grade overturns the
/// claim, actually PUSHED — so the assertions cover the whole arc: accepted AND published, never accepted alone.
///
/// <para>Its twin: the same harness against a check that genuinely FAILS must stay Failed with no contradiction —
/// so the fold is proven to follow the ORACLE, not merely to overturn every failure it sees. Skips on Windows /
/// when git is absent.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentUnderClaimGradeFlowTests
{
    /// <summary>The check the contract runs on the run's work: PASS iff the agent's file says "delivered".</summary>
    private const string PassingCheck = "#!/bin/sh\ngrep -q delivered feature.txt\n";

    /// <summary>A check nothing can satisfy — the twin arc's oracle.</summary>
    private const string FailingCheck = "#!/bin/sh\nexit 1\n";

    /// <summary>Does the work, then reports failure: writes the file the check wants and exits non-zero anyway.</summary>
    private const string WorkThenGiveUpScript = "printf 'delivered\\n' > feature.txt; echo 'I could not finish'; exit 1";

    private readonly PostgresFixture _fixture;

    public AgentUnderClaimGradeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_agent_that_did_the_work_but_reported_failure_lands_succeeded_and_names_the_under_claim()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedBaseAsync(PassingCheck);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        var runId = await CreateRunAsync(teamId, TaskWith(repoId));

        await ExecuteAsync(runId, new ScriptedFailingHarness(WorkThenGiveUpScript));

        var (run, result) = await LoadAsync(runId);

        run.Status.ShouldBe(AgentRunStatus.Succeeded,
            "the objective check PASSED on the work the agent left behind — the verdict outranks the claim, and the work is no longer discarded");
        result.AcceptancePassed.ShouldBe(true);
        result.Contradiction.ShouldBe(AgentContradiction.UnderClaim, "the run's durable result NAMES the disagreement instead of hiding it");
        result.Error.ShouldNotBeNull().ShouldContain("exit 1", Case.Insensitive, "the agent's own account of itself survives on the durable result");
        result.Patch.ShouldNotBeNullOrEmpty("the GRADED work is the run's own recorded patch — a Failed run has no branch yet when the oracle runs");

        // The work is PUBLISHED, not merely accepted. The push step runs before the grade and skips a Failed run, so
        // without the post-fold publish this run would land Succeeded + acceptance-Passed with nothing on the remote:
        // the accepted-but-unpublished state publish-or-park exists to prevent, and no `branch` output for a
        // downstream PR-open to consume.
        var branch = AgentRunExecutor.BuildBranchName(runId);
        result.ProducedBranch.ShouldBe(branch, "the fold earned the run its publish round");
        result.PushedCommitSha.ShouldNotBeNullOrEmpty("Pushed is a CONFIRMED claim — the remote readback, not the push command's intent");
        (await remote.HasBranchAsync(branch)).ShouldBeTrue("the branch actually exists on the remote, not just on the result row");

        using var scope = _fixture.BeginScope();

        var manifest = (await scope.Resolve<IPublishManifestStore>().ListForAgentRunAsync(runId, teamId, CancellationToken.None)).ShouldHaveSingleItem();

        manifest.PublishStateValue.ShouldBe(PublishState.Pushed, "the manifest states the pushed truth — an AcceptanceState of Passed beside PatchOnly is the scorecard reading IsSolved for work nobody can reach");
        manifest.AcceptanceState.ShouldBe(PublishAcceptanceState.Passed);
        manifest.Branch.ShouldBe(branch);
        manifest.CommitSha.ShouldNotBeNullOrEmpty();

        // The journal agent card's own read path — a plain agent has no supervisor compact, so this projection is the
        // ONLY way the under-claim reaches the card. Before D4b it could not have carried one at all.
        var metrics = await scope.Resolve<AgentMetricsReader>().ReadAsync(teamId, new[] { runId }, DateTimeOffset.UtcNow, CancellationToken.None);

        metrics[runId].Contradiction.ShouldBe(AgentContradiction.UnderClaim, "the card names the under-claim for the operator, not only the durable row");
    }

    [Fact]
    public async Task The_same_self_reported_failure_stays_failed_when_the_check_really_fails()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedBaseAsync(FailingCheck);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        var runId = await CreateRunAsync(teamId, TaskWith(repoId));

        await ExecuteAsync(runId, new ScriptedFailingHarness(WorkThenGiveUpScript));

        var (run, result) = await LoadAsync(runId);

        run.Status.ShouldBe(AgentRunStatus.Failed, "claim and verdict AGREE — the fold follows the oracle, it does not overturn failures on principle");
        result.AcceptancePassed.ShouldBe(false, "the check ran and rejected the work — that verdict is recorded rather than left null");
        result.Contradiction.ShouldBeNull("an agreeing claim is not a contradiction — least of all an over-claim");
        result.ExitReason.ShouldBe("non-zero-exit", "the run failed on its own report, not on a fail-closed acceptance re-grade");
        result.ProducedBranch.ShouldBeNull("the fold did not overturn anything, so the run buys no publish round — a rejected run's work stays parked");
        (await remote.HasBranchAsync(AgentRunExecutor.BuildBranchName(runId))).ShouldBeFalse("nothing was published for work the check rejected");
    }

    // ─── Seeding ─────────────────────────────────────────────────────────────

    private static AgentTask TaskWith(Guid repositoryId) => new()
    {
        Goal = "make feature.txt say the right thing",
        Harness = "scripted",
        Model = "test-model",
        RepositoryId = repositoryId,
        Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" }, Description = "the file check" },
        PushProducedBranch = true,
    };

    private async Task<Guid> CreateRunAsync(Guid teamId, AgentTask task)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(task, teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"underclaim-{userId:N}@test.local", Name = $"underclaim-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"underclaim-{teamId:N}", Name = "Under-claim Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    /// <summary>Mirrors <see cref="AgentRunReviseLoopFlowTests"/>' bound-repo seed: a PAT credential so the clone carries a token and the publish path activates.</summary>
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

    // ─── Execution ───────────────────────────────────────────────────────────

    private async Task ExecuteAsync(Guid runId, IAgentHarness harness)
    {
        using var scope = _fixture.BeginScope();
        var executor = new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            new AgentHarnessRegistry(new[] { harness }),
            new HarnessModelReconciler(new AgentHarnessRegistry(new[] { harness }), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactStore>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IArtifactManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Agents.Capture.ICaptureIntentService>(),
            scope.Resolve<IEnumerable<CodeSpace.Core.Services.Agents.Publish.IPublishGuard>>(),
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(runId, CancellationToken.None);
    }

    private async Task<(AgentRun Run, AgentRunResult Result)> LoadAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        return (run, JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!);
    }

    // ─── Git helpers ─────────────────────────────────────────────────────────

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local remote seeding <c>check.sh</c> + a base file — the ground truth the workspace clone and the grader's own clone both land on. GUID-suffixed; best-effort cleanup.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-under-claim-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task<bool> HasBranchAsync(string branch) =>
            (await Git(_root, "--git-dir", _bare, "branch", "--list", branch)).Trim().Length > 0;

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

    /// <summary>
    /// The honest fake for the agent's JUDGEMENT: it runs a real script in the real workspace and folds a
    /// self-reported FAILURE from the non-zero exit. Everything else — the process, the workspace, the diff capture,
    /// the grade, the fold — is production code.
    /// </summary>
    private sealed class ScriptedFailingHarness : IAgentHarness
    {
        private readonly string _script;

        public ScriptedFailingHarness(string script) { _script = script; }

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new()
        {
            Command = "/bin/sh",
            Args = new[] { "-c", _script },
            WorkingDirectory = task.WorkspaceDirectory,
            TimeoutSeconds = task.TimeoutSeconds,
        };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = fold.LastText }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Summary = fold.LastText, Error = $"exit {exitCode}" });
    }
}
