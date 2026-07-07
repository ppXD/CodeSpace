using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 HIGH fidelity (Rule 12): the S6 BOUNDED REVISE LOOP end to end through the REAL <see cref="AgentRunExecutor"/> —
/// real <see cref="LocalProcessRunner"/> spawning a real <c>/bin/sh</c> agent in a real cloned workspace, a real bare
/// git remote the branch pushes to, the REAL <c>SupervisorAcceptanceGrader</c> cloning that branch and executing the
/// contract's <c>check.sh</c>, and the REAL <c>LlmStructuredCritic</c> resolving a content-keyed deterministic reviewer
/// through the production pool. Only two seams are faked, both honestly: the agent's INTELLIGENCE (a goal-reactive
/// scripted harness that writes flawed work first and fixed work when the goal carries the revise instruction) and the
/// reviewer's NETWORK CALL (<see cref="DeterministicCriticLlmClient"/> — its verdict is a pure function of the diff).
///
/// <para>Covers the four load-bearing arcs: (1) an oracle failure feeds the grader's detail back and the revision
/// PASSES the real re-grade — generator↔evaluator closed in one run; (2) an exhausted budget lands the truthful
/// <c>Failed("acceptance-failed")</c> with every round on the timeline and the work preserved; (3) an Improve-critic
/// flag feeds the critique back, the revision removes the flaw, and the SAME critic approves — and the critic bills
/// exactly twice (once per round), never on a failed oracle; (4) the combined ordering — the oracle fails first
/// (critic NOT billed), the revision passes the check but still carries the planted flaw, and the exhausted budget
/// lands the truthful <c>NeedsReview("output-flagged")</c>. Skips on Windows / when git is absent.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunReviseLoopFlowTests
{
    /// <summary>The check the contract runs on the produced branch: PASS iff the agent's file says "revised".</summary>
    private const string CheckScript = "#!/bin/sh\ngrep -q revised feature.txt\n";

    /// <summary>Writes flawed work — fails <see cref="CheckScript"/> AND carries the critic's reject marker.</summary>
    private const string DraftScript = "printf 'draft " + DeterministicCriticLlmClient.RejectMarker + "\\n' > feature.txt; echo drafted";

    /// <summary>Writes fixed work — passes <see cref="CheckScript"/>, no reject marker.</summary>
    private const string RevisedScript = "printf 'revised clean\\n' > feature.txt; echo revised";

    /// <summary>Fixes the CHECK but keeps the planted flaw — the combined-ordering arc's "half-fix".</summary>
    private const string HalfFixScript = "printf 'revised " + DeterministicCriticLlmClient.RejectMarker + "\\n' > feature.txt; echo half-fixed";

    /// <summary>Sound work (passes <see cref="CheckScript"/>) that draws only a MINOR critic nitpick — the calibration path.</summary>
    private const string NitpickScript = "printf 'revised " + DeterministicCriticLlmClient.NitpickMarker + "\\n' > feature.txt; echo drafted";

    private readonly PostgresFixture _fixture;

    public AgentRunReviseLoopFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_oracle_failure_feeds_back_and_the_revision_passes_the_real_regrade()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedBaseAsync(CheckScript);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        var runId = await CreateRunAsync(teamId, TaskWith(repoId) with { MaxReviseRounds = 1 });

        await ExecuteAsync(runId, new ReviseAwareHarness(first: DraftScript, revised: RevisedScript));

        var (run, result) = await LoadAsync(runId);

        run.Status.ShouldBe(AgentRunStatus.Succeeded, "the revision fixed the work — the re-grade against the REAL check passed");
        result.AcceptancePassed.ShouldBe(true);
        result.ReviseRounds.ShouldBe(1, "exactly one revise round ran");

        var branch = AgentRunExecutor.BuildBranchName(runId);
        (await remote.BranchFileContentAsync(branch, "feature.txt")).ShouldContain("revised", customMessage: "the re-pushed branch tip carries the REVISED work — the same branch name, force-updated");

        var events = await LoadEventsAsync(runId);
        events.Count(e => e.Contains("revising (round 1 of 1)")).ShouldBe(1, "the operator sees WHY the run took another pass");
        events.Single(e => e.Contains("revising")).ShouldContain("acceptance check failed", Case.Insensitive, "the revise event names the oracle failure it feeds back");
    }

    [Fact]
    public async Task An_exhausted_budget_lands_the_truthful_failure_with_every_round_on_the_timeline()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedBaseAsync("#!/bin/sh\nexit 1\n");   // an unfixable check — every round fails
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        var runId = await CreateRunAsync(teamId, TaskWith(repoId) with { MaxReviseRounds = 2 });

        await ExecuteAsync(runId, new ReviseAwareHarness(first: DraftScript, revised: RevisedScript));

        var (run, result) = await LoadAsync(runId);

        run.Status.ShouldBe(AgentRunStatus.Failed, "the budget ran dry with the check still failing — Failed is the truth, never a phantom pass");
        result.AcceptancePassed.ShouldBe(false);
        result.ReviseRounds.ShouldBe(2, "both budgeted rounds actually ran");
        (await remote.HasBranchAsync(AgentRunExecutor.BuildBranchName(runId))).ShouldBeTrue("the captured work survives for diagnosis");

        var events = await LoadEventsAsync(runId);
        events.Count(e => e.Contains("revising (round 1 of 2)")).ShouldBe(1);
        events.Count(e => e.Contains("revising (round 2 of 2)")).ShouldBe(1, "each round announces itself — the timeline is the audit trail");
    }

    [Fact]
    public async Task A_minor_only_critic_flag_does_not_halt_a_gate_the_calibration_fix()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var priorToggle = Environment.GetEnvironmentVariable(CriticToggle.EnabledEnvVar);
        Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, "1");
        try
        {
            var teamId = await SeedTeamAsync();
            var reviewerRowId = await SeedCriticModelAsync(teamId);
            ResetCriticScript();

            using var remote = new BareRemote();
            await remote.SeedBaseAsync("#!/bin/sh\nexit 0\n");   // the structural floor passes — ONLY the critic gates
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            // GATE mode: a flag would hard-halt to NeedsReview. The sound work draws only a MINOR nitpick, and the
            // severity-authoritative projection APPROVES it (no blocker) — so the run stays Succeeded, unflagged.
            var runId = await CreateRunAsync(teamId, TaskWith(repoId) with { OutputReviewMode = ReviewMode.Gate, ReviewerModelId = reviewerRowId });

            await ExecuteAsync(runId, new ReviseAwareHarness(first: NitpickScript, revised: RevisedScript));

            var (run, result) = await LoadAsync(runId);

            run.Status.ShouldBe(AgentRunStatus.Succeeded, "a Minor-only flag no longer halts the gate — the produced work is not blocked over a nitpick");
            result.ReviseRounds.ShouldBe(0, "a Gate mode never revises; and a Minor flag never triggered one either");
            result.ReviewFeedback.ShouldBeNull("an approved run carries no feedback");
            CriticCalls().ShouldBe(1, "the critic was consulted exactly once and its minor-only verdict approved");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, priorToggle);
        }
    }

    [Fact]
    public async Task An_improve_critic_flag_feeds_the_critique_back_and_the_same_critic_approves_the_revision()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var priorToggle = Environment.GetEnvironmentVariable(CriticToggle.EnabledEnvVar);
        Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, "1");
        try
        {
            var teamId = await SeedTeamAsync();
            var reviewerRowId = await SeedCriticModelAsync(teamId);
            ResetCriticScript();

            using var remote = new BareRemote();
            await remote.SeedBaseAsync("#!/bin/sh\nexit 0\n");   // the structural floor passes — ONLY the critic gates
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            // Improve + no explicit budget → the executor's default ONE round (Improve MEANS improve).
            var runId = await CreateRunAsync(teamId, TaskWith(repoId) with { OutputReviewMode = ReviewMode.Improve, ReviewerModelId = reviewerRowId });

            await ExecuteAsync(runId, new ReviseAwareHarness(first: DraftScript, revised: RevisedScript));

            var (run, result) = await LoadAsync(runId);

            run.Status.ShouldBe(AgentRunStatus.Succeeded, "the revision removed the planted flaw — the SAME independent critic approved it");
            result.ReviseRounds.ShouldBe(1);
            result.AcceptancePassed.ShouldBe(true, "the structural floor also re-graded on the revised branch");
            result.ReviewFeedback.ShouldBeNull("an approved final review carries no feedback — the flag was healed, not suppressed");

            CriticCalls().ShouldBe(2, "the critic billed exactly once per round — flag, then approve");

            var events = await LoadEventsAsync(runId);
            events.Single(e => e.Contains("revising (round 1 of 1)")).ShouldContain(DeterministicCriticLlmClient.Critique, customMessage: "the CRITIQUE is what feeds back — the agent revises against the reviewer's words");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, priorToggle);
        }
    }

    [Fact]
    public async Task An_oscillating_critic_stops_early_when_the_same_flaw_is_re_flagged_unchanged()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var priorToggle = Environment.GetEnvironmentVariable(CriticToggle.EnabledEnvVar);
        Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, "1");
        try
        {
            var teamId = await SeedTeamAsync();
            var reviewerRowId = await SeedCriticModelAsync(teamId);
            ResetCriticScript();

            using var remote = new BareRemote();
            await remote.SeedBaseAsync("#!/bin/sh\nexit 0\n");   // the structural floor passes — ONLY the critic gates
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            // Budget 3, but the "revision" never removes the flaw (both scripts carry the reject marker) → the critic
            // re-flags the IDENTICAL feedback. P1b-2: convergence recognises the unchanged re-flag and stops EARLY —
            // rounds 2 and 3 are never billed — instead of silently exhausting the whole budget on an unmovable issue.
            var runId = await CreateRunAsync(teamId, TaskWith(repoId) with { OutputReviewMode = ReviewMode.Improve, ReviewerModelId = reviewerRowId, MaxReviseRounds = 3 });

            await ExecuteAsync(runId, new ReviseAwareHarness(first: DraftScript, revised: DraftScript));

            var (run, result) = await LoadAsync(runId);

            run.Status.ShouldBe(AgentRunStatus.NeedsReview, "the flaw persisted — the run is flagged for a human, never a silent pass");
            result.ReviseRounds.ShouldBe(1, "convergence stopped after ONE round — the identical re-flag was not worth rounds 2 and 3");
            result.ReviewFeedback.ShouldNotBeNull("the flag stands with the reviewer's feedback for the human");

            CriticCalls().ShouldBe(2, "first review + round-1 review only — the stall stopped rounds 2 and 3 before they billed the critic");

            var events = await LoadEventsAsync(runId);
            events.ShouldContain(e => e.Contains(AgentRunExecutor.ReviseStalledPrefix), "the operator sees the loop gave up on an unmovable issue, not a silently-spent budget");
            events.Count(e => e.Contains("revising (round")).ShouldBe(1, "exactly one revise round was announced before the stall");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, priorToggle);
        }
    }

    [Fact]
    public async Task The_oracle_fails_first_without_billing_the_critic_and_a_half_fix_lands_the_truthful_flag()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var priorToggle = Environment.GetEnvironmentVariable(CriticToggle.EnabledEnvVar);
        Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, "1");
        try
        {
            var teamId = await SeedTeamAsync();
            var reviewerRowId = await SeedCriticModelAsync(teamId);
            ResetCriticScript();

            using var remote = new BareRemote();
            await remote.SeedBaseAsync(CheckScript);   // BOTH gates armed: the real check AND the critic
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            // Budget 1: round 1 (draft) flunks the ORACLE; the one revision half-fixes — the check passes but the
            // planted flaw remains — and the budget is spent, so the critic's flag STANDS.
            var runId = await CreateRunAsync(teamId, TaskWith(repoId) with { OutputReviewMode = ReviewMode.Improve, ReviewerModelId = reviewerRowId, MaxReviseRounds = 1 });

            await ExecuteAsync(runId, new ReviseAwareHarness(first: DraftScript, revised: HalfFixScript));

            var (run, result) = await LoadAsync(runId);

            run.Status.ShouldBe(AgentRunStatus.NeedsReview, "the budget is spent and the flaw remains — the flag stands for a human, never a silent pass");
            result.ExitReason.ShouldBe("output-flagged");
            result.AcceptancePassed.ShouldBe(true, "the oracle half IS fixed — the verdicts stay separately truthful");
            result.ReviseRounds.ShouldBe(1);
            result.ReviewFeedback.ShouldNotBeNull();
            result.ReviewFeedback.ShouldContain(DeterministicCriticLlmClient.Critique);

            CriticCalls().ShouldBe(1, "round 1's FAILED oracle never billed a review (grade runs first); only the revised round reached the critic");

            var events = await LoadEventsAsync(runId);
            events.Single(e => e.Contains("revising")).ShouldContain("acceptance check failed", Case.Insensitive, "the round was bought by the ORACLE, not the critic — order proven");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, priorToggle);
        }
    }

    // ─── Seeding ─────────────────────────────────────────────────────────────

    private static AgentTask TaskWith(Guid repositoryId) => new()
    {
        Goal = "make feature.txt say the right thing",
        Harness = "scripted",
        Model = "test-model",
        RepositoryId = repositoryId,
        Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" }, Description = "the file check" },
        // The contract-implies-gradable-branch invariant AgentCodeNode bakes at authoring (F4) — mirrored here because
        // these tests build the task directly rather than through the node.
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
        db.User.Add(new User { Id = userId, Email = $"revise-{userId:N}@test.local", Name = $"revise-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"revise-{teamId:N}", Name = "Revise Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    /// <summary>The reviewer model row under the content-keyed critic fake's provider tag — pinned onto the task so the reviewer resolution is deterministic even beside other seeded rows.</summary>
    private async Task<Guid> SeedCriticModelAsync(Guid teamId) =>
        (await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "critic-model", provider: DeterministicCriticLlmClient.ProviderTag)).RowId;

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

    /// <summary>Mirrors <see cref="AgentBranchPushFlowTests"/>' bound-repo seed: a GitHub PAT credential so the clone carries a token and the push path activates (the contract forces the per-task publish opt-in).</summary>
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
            NullLogger<AgentRunExecutor>.Instance);

        await executor.ExecuteAsync(runId, CancellationToken.None);
    }

    private async Task<(AgentRun Run, AgentRunResult Result)> LoadAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        return (run, JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!);
    }

    private async Task<IReadOnlyList<string>> LoadEventsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        var events = await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, run.TeamId, afterSequence: 0, CancellationToken.None);
        return events.Select(e => e.Text).ToList();
    }

    // ─── Git helpers ─────────────────────────────────────────────────────────

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare local remote seeding <c>check.sh</c> + a base file, with tip-content inspection — the ground truth the revise loop's re-push and the grader's clone both land on. GUID-suffixed; best-effort cleanup.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-revise-loop-" + Guid.NewGuid().ToString("N"));
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

        public async Task<bool> HasBranchAsync(string branch) =>
            (await Git(_root, "--git-dir", _bare, "branch", "--list", branch)).Trim().Length > 0;

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

    /// <summary>
    /// The goal-reactive scripted harness — the honest fake for the agent's INTELLIGENCE across revise rounds: the
    /// FIRST invocation (the plain goal) runs the flawed script; an invocation whose goal carries the executor's
    /// pinned <see cref="AgentRunExecutor.ReviseInstructionPrefix"/> runs the revised one. Everything else — the
    /// process, the workspace, the diff capture, the push, the grade, the review — is production code.
    /// </summary>
    private sealed class ReviseAwareHarness : IAgentHarness
    {
        private readonly string _first;
        private readonly string _revised;

        public ReviseAwareHarness(string first, string revised) { _first = first; _revised = revised; }

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new()
        {
            Command = "/bin/sh",
            Args = new[] { "-c", task.Goal.StartsWith(AgentRunExecutor.ReviseInstructionPrefix, StringComparison.Ordinal) ? _revised : _first },
            WorkingDirectory = task.WorkspaceDirectory,
            TimeoutSeconds = task.TimeoutSeconds,
        };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? Array.Empty<AgentEvent>() : new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };

        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = events.Count > 0 ? events[^1].Text : null }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" };
    }
}
