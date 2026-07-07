using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 THE grounded plan-review E2E (D① — "plan 真 agent 評審"), end to end through REAL machinery: a plan is reviewed
/// by a REAL, SEPARATE <see cref="AgentRun"/> (the fixture registry's codex-cli lane, driven by
/// <see cref="ReviewVerdictFakeCli"/>) that CLONES the repository's DEFAULT branch — the tree the plan's first agent
/// would see — and greps the ACTUAL code for the plan's broken assumption; its <c>VERDICT:</c> final message parses
/// into an evidence-attached verdict. The planner-critic ladder then folds that grounded verdict into the plan's
/// risks exactly as a model verdict would. The plan's judge is a real agent process in a real clone — not a text
/// impression of the plan.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GroundedPlanReviewFlowTests
{
    private readonly PostgresFixture _fixture;

    public GroundedPlanReviewFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_grounded_plan_review_disapproves_when_the_real_tree_refutes_the_plan()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var reviewerCli = new ReviewVerdictFakeCli();

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedAsync(("hack.txt", ReviewVerdictFakeCli.FlawMarker + "\n"));   // the plan's broken assumption, IN the default branch
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        using var scope = _fixture.BeginScope();
        var verdict = await scope.Resolve<IAgentPlanReviewer>().ReviewAsync(new PlanReviewRequest
        {
            PlanArtifact = "Goal: clean tree\nSubtasks:\n  - ship: build on the existing clean base",
            Goal = "ship the feature on a clean base",
            RepositoryId = repoId,
            TeamId = teamId,
        }, CancellationToken.None);

        verdict.Failed.ShouldBeFalse("the reviewer run completed and honoured the VERDICT contract");
        verdict.Approved.ShouldBeFalse("the real tree refutes the plan — the reviewer SAW the flaw the plan ignores");
        verdict.Rationale.ShouldBe(ReviewVerdictFakeCli.DisapproveRationale);
        verdict.Issues.ShouldContain(i => i.Evidence != null && i.Evidence.Contains("grep found"), "the disapproval carries evidence from the ACTUAL clone");

        var db = scope.Resolve<CodeSpaceDbContext>();
        var reviewRun = await db.AgentRun.AsNoTracking().SingleAsync(r => r.TeamId == teamId && r.IterationKey == AgentPlanReviewer.IterationKey);
        reviewRun.Status.ShouldBe(AgentRunStatus.Succeeded, "a disapproval is a VERDICT, not a process failure");
        reviewRun.Harness.ShouldBe("codex-cli", "a model-produced plan takes the first registered harness — a real agent toolchain");
    }

    [Fact]
    public async Task A_grounded_plan_review_approves_against_a_clean_tree()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        using var reviewerCli = new ReviewVerdictFakeCli();

        var teamId = await SeedTeamAsync();
        using var remote = new BareRemote();
        await remote.SeedAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        using var scope = _fixture.BeginScope();
        var verdict = await scope.Resolve<IAgentPlanReviewer>().ReviewAsync(new PlanReviewRequest
        {
            PlanArtifact = "Goal: clean tree\nSubtasks:\n  - ship: build on the existing clean base",
            Goal = "ship the feature on a clean base",
            RepositoryId = repoId,
            TeamId = teamId,
        }, CancellationToken.None);

        verdict.Failed.ShouldBeFalse();
        verdict.Approved.ShouldBeTrue("the clone holds no counter-evidence — the plan's assumptions verify against the real tree");
    }

    [Fact]
    public async Task The_planner_critic_ladder_folds_the_grounded_evidence_into_the_plan_risks()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var priorToggle = Environment.GetEnvironmentVariable(CriticToggle.EnabledEnvVar);
        Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, "1");
        try
        {
            using var reviewerCli = new ReviewVerdictFakeCli();

            var teamId = await SeedTeamAsync();
            using var remote = new BareRemote();
            await remote.SeedAsync(("hack.txt", ReviewVerdictFakeCli.FlawMarker + "\n"));
            var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

            using var scope = _fixture.BeginScope();
            scope.Resolve<CriticReviewScript>().Reset();
            var decorator = new CriticPlannerDecorator(new FixedPlanner(), scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(), scope.Resolve<IAgentPlanReviewer>());

            var plan = await decorator.PlanAsync(new WorkflowPlanRequest
            {
                TaskText = "ship the feature on a clean base",
                TeamId = teamId,
                Review = ReviewMode.Gate,
                ReviewerAgent = true,
                RepositoryId = repoId,
            }, CancellationToken.None);

            plan.Risks.ShouldContain(r => r.Contains("grep found"), "the grounded evidence from the REAL clone rides the plan the human confirms");
            plan.Risks.ShouldContain(r => r.Contains("flagged concerns"), "the agent's verdict annotates exactly as a model verdict would");
            scope.Resolve<CriticReviewScript>().Calls.ShouldBe(0, "the agent produced a verdict — the model critic was never billed");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CriticToggle.EnabledEnvVar, priorToggle);
        }
    }

    // ─── plumbing (the S8 reviewer-flow test's proven fixtures) ──────────────

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"gpr-{userId:N}@test.local", Name = $"gpr-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"gpr-{teamId:N}", Name = "Grounded Plan Review Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
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

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>The inner planner under review — a fixed plan whose soundness only the TREE can judge.</summary>
    private sealed class FixedPlanner : IWorkflowPlanner
    {
        public Task<PlannedWorkflow> PlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PlannedWorkflow { Goal = request.TaskText, Subtasks = new[] { new PlannedSubtask { Id = "1", Title = "ship", Instruction = "build on the existing clean base" } } });
    }

    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-plan-review-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        /// <summary>Seed main with base.txt plus the given files — the DEFAULT-branch tree the plan reviewer clones.</summary>
        public async Task SeedAsync(params (string Name, string Content)[] files)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await Git(seed, "config", "user.email", "test@codespace.dev");
            await Git(seed, "config", "user.name", "Test");
            await Git(seed, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, "base.txt"), "base\n");
            foreach (var (name, content) in files) await File.WriteAllTextAsync(Path.Combine(seed, name), content);
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
}
