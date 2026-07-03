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
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 THE agent-based adversarial-review E2E (triad S8 — the owner's "true independent agent + harness" ask), end to
/// end through REAL machinery: the PRODUCER agent (a goal-reactive scripted harness) commits flawed work to a real
/// bare remote; the executor's reviewer ladder stages a REAL, SEPARATE review <see cref="AgentRun"/> on a DIFFERENT
/// harness (the fixture registry's codex-cli, driven by <see cref="ReviewVerdictFakeCli"/>) that CLONES the produced
/// branch and greps the actual tree for the planted flaw; its <c>VERDICT:</c> final message parses into an
/// evidence-attached disapproval; the S6 Improve loop feeds that critique back; the revision removes the flaw,
/// re-pushes, a SECOND review agent clones the updated branch and approves — the run lands Succeeded with two
/// first-class reviewer runs on the tape. The generator and its adversary are BOTH real agent processes in real
/// clones on different harnesses.
///
/// <para>Also covered: the ladder's honesty — with no produced branch there is nothing for an agent to inspect, so
/// the review falls back to the in-process MODEL critic (billed exactly once on the deterministic critic fake).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentReviewerLoopFlowTests
{
    private const string DraftScript = "printf 'draft " + ReviewVerdictFakeCli.FlawMarker + "\\n' > feature.txt; echo drafted";
    private const string RevisedScript = "printf 'revised clean\\n' > feature.txt; echo revised";

    private readonly PostgresFixture _fixture;

    public AgentReviewerLoopFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_real_review_agent_on_another_harness_blocks_the_flaw_and_approves_the_revision()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var priorToggle = Environment.GetEnvironmentVariable(CriticToggle.EnabledEnvVar);
        Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, "1");
        try
        {
            using var reviewerCli = new ReviewVerdictFakeCli();   // the REVIEWER binary (codex-cli lane)

            var teamId = await SeedTeamAsync();
            using var remote = new BareRemote();
            await remote.SeedBaseAsync();
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            // Improve + ReviewerAgent: the disapproving AGENT verdict buys the S6 revise round (implicit 1 under Improve).
            var task = new AgentTask
            {
                Goal = "make feature.txt clean",
                Harness = "scripted",
                Model = "test-model",
                RepositoryId = repoId,
                PushProducedBranch = true,   // the reviewer clones the PRODUCED branch — publish is the review's precondition
                OutputReviewMode = ReviewMode.Improve,
                ReviewerAgent = true,
            };

            var runId = await CreateRunAsync(teamId, task);

            await ExecuteAsync(runId, new ReviseAwareHarness(DraftScript, RevisedScript));

            using var scope = _fixture.BeginScope();
            var db = scope.Resolve<CodeSpaceDbContext>();

            var producer = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);
            producer.Status.ShouldBe(AgentRunStatus.Succeeded, "the revision removed the flaw and the SECOND review agent approved it");

            var result = JsonSerializer.Deserialize<AgentRunResult>(producer.ResultJson!, AgentJson.Options)!;
            result.ReviseRounds.ShouldBe(1, "the agent reviewer's disapproval bought exactly one revise round");
            result.ReviewFeedback.ShouldBeNull("the final review APPROVED — no flag stands");

            (await remote.BranchFileContentAsync(AgentRunExecutor.BuildBranchName(runId), "feature.txt"))
                .ShouldContain("revised clean", customMessage: "the re-pushed branch tip carries the healed work the reviewer approved");

            // TWO first-class reviewer runs on the tape — one per round — each on the DISTINCT harness the ladder picked.
            var reviewRuns = await db.AgentRun.AsNoTracking().Where(r => r.TeamId == teamId && r.IterationKey.EndsWith("#review")).OrderBy(r => r.CreatedDate).ToListAsync();
            reviewRuns.Count.ShouldBe(2, "each verification round ran its own real review agent");
            reviewRuns.ShouldAllBe(r => r.Status == AgentRunStatus.Succeeded);
            reviewRuns.ShouldAllBe(r => r.Harness == "codex-cli", "the distinct-first ladder picked a harness DIFFERENT from the producer's");

            // The revise instruction carried the reviewer's rationale + grep evidence — the agent revised against a
            // REAL inspection of its own tree, not a diff-string opinion.
            var events = await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, teamId, afterSequence: 0, CancellationToken.None);
            var revise = events.Single(e => e.Text.Contains("revising (round 1 of 1)"));
            revise.Text.ShouldContain(ReviewVerdictFakeCli.DisapproveRationale);
            revise.Text.ShouldContain("grep found", customMessage: "the evidence rides the fed-back critique");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, priorToggle);
        }
    }

    [Fact]
    public async Task With_no_produced_branch_the_ladder_falls_back_to_the_model_critic()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var priorToggle = Environment.GetEnvironmentVariable(CriticToggle.EnabledEnvVar);
        Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, "1");
        try
        {
            using var reviewerCli = new ReviewVerdictFakeCli();

            var teamId = await SeedTeamAsync();
            var criticRowId = (await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "critic-model", provider: DeterministicCriticLlmClient.ProviderTag)).RowId;
            ResetCriticScript();

            using var remote = new BareRemote();
            await remote.SeedBaseAsync();
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            // NO push → no produced branch → nothing for an agent to clone → the ladder must fall to the model critic.
            var task = new AgentTask
            {
                Goal = "make feature.txt clean",
                Harness = "scripted",
                Model = "test-model",
                RepositoryId = repoId,
                OutputReviewMode = ReviewMode.Gate,
                ReviewerAgent = true,
                ReviewerModelId = criticRowId,
            };

            var runId = await CreateRunAsync(teamId, task);

            await ExecuteAsync(runId, new ReviseAwareHarness(DraftScript, RevisedScript));

            using var scope = _fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

            run.Status.ShouldBe(AgentRunStatus.NeedsReview, "the MODEL critic (the ladder's fallback) flagged the flawed diff");
            JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!.ReviewFeedback
                .ShouldContain(DeterministicCriticLlmClient.Critique);

            CriticCalls().ShouldBe(1, "the fallback billed the model critic exactly once");

            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.AgentRun.AsNoTracking().CountAsync(r => r.TeamId == teamId && r.IterationKey.EndsWith("#review")))
                .ShouldBe(0, "no branch ⇒ no review agent was ever staged — the ladder skipped straight to the model");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, priorToggle);
        }
    }

    // ─── plumbing (the S6 revise-loop test's proven fixtures) ────────────────

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
        db.User.Add(new User { Id = userId, Email = $"arev-{userId:N}@test.local", Name = $"arev-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"arev-{teamId:N}", Name = "Agent Review Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

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

    /// <summary>The hand-built executor drives the PRODUCER on the scripted harness; the REVIEWER path resolves the REAL executor + REAL harness registry from DI (the fixture scope), so the review run goes through the production codex-cli lane.</summary>
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

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-agent-review-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>The producer's goal-reactive scripted harness (the S6 pattern): flawed first, fixed under the revise instruction.</summary>
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
