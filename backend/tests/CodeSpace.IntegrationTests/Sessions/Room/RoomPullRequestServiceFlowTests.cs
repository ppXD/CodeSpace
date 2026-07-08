using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions.Room;

/// <summary>
/// 🟡 Medium-mock (Rule 12): the Room's Open-PR action (PR-6) end to end — the REAL <see cref="IRoomPullRequestService"/>
/// against REAL Postgres, resolving the run's published branch(es) off a HAND-SEEDED terminal decision tape (the exact
/// JSON shape <c>RealSupervisorActionExecutor</c>'s <c>ProjectIntegrationResult</c> / <c>ProjectRepoBlock</c> emit —
/// the git integration ITSELF is real-git-tested by <c>SupervisorPublishGateFlowTests</c>, so re-driving a real merge
/// here would just re-prove that, not this service), and opening the PR through the REAL
/// <see cref="Core.Services.PullRequests.IChangeSetService"/> against the test container's <c>ProviderKind.Git</c> fake
/// write capability — a real GitHub/GitLab API call is reserved for the real-model E2E tier, the one dependency too
/// expensive to hit here. This suite's job: given the tape says a branch was published, does the service correctly
/// resolve WHAT to open, fan out per repo with honest failure isolation, stay idempotent, and record the result.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RoomPullRequestServiceFlowTests
{
    private readonly PostgresFixture _fixture;

    public RoomPullRequestServiceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_single_repo_run_opens_a_PR_and_a_repeat_call_reuses_it()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1");
        await StampTerminalRepositoryIdAsync(runId, repoId);

        var first = await OpenAsync(runId, teamId);

        first.PullRequests.Count.ShouldBe(1);
        var opened = first.PullRequests.Single();
        opened.Disposition.ShouldBe(RoomPullRequestDisposition.Opened);
        opened.RepositoryId.ShouldBe(repoId);
        opened.Alias.ShouldBe("primary");
        opened.Number.ShouldBe(777, "the test container's fake write capability always returns a fixed number");
        opened.Url.ShouldNotBeNullOrEmpty();

        var manifest = await SingleManifestRowAsync(runId, teamId);
        manifest.Branch.ShouldBe("codespace/integration/run/turn1");
        manifest.PullRequestNumber.ShouldBe(777);
        manifest.PullRequestUrl.ShouldBe(opened.Url);

        var second = await OpenAsync(runId, teamId);

        var reused = second.PullRequests.Single();
        reused.Disposition.ShouldBe(RoomPullRequestDisposition.AlreadyOpened, "a repeat click must reuse the recorded PR, never open a duplicate");
        reused.Number.ShouldBe(opened.Number);
        reused.Url.ShouldBe(opened.Url);

        (await ManifestRowCountAsync(runId, teamId)).ShouldBe(1, "the repeat call must never mint a second manifest row");
    }

    [Fact]
    public async Task A_later_turns_different_branch_opens_a_fresh_PR_instead_of_reusing_the_stale_one()
    {
        // Hidden-dependency sweep finding: an integration branch is turn-scoped (codespace/integration/{runId}/turn{N}),
        // so a LATER merge in the SAME run genuinely produces a DIFFERENT branch for the SAME alias. The idempotency
        // check must key on (alias, branch) — never alias alone — or a repeat call after the frontier moved on would
        // silently reuse the FIRST turn's now-abandoned PR while claiming the run's CURRENT branch is already handled.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1");
        await StampTerminalRepositoryIdAsync(runId, repoId);

        var first = await OpenAsync(runId, teamId);
        var firstOpened = first.PullRequests.Single();
        firstOpened.Disposition.ShouldBe(RoomPullRequestDisposition.Opened);

        // A second, LATER merge decision on the SAME run advances the frontier to a genuinely different branch.
        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn2");

        var second = await OpenAsync(runId, teamId);

        var reOpened = second.PullRequests.Single();
        reOpened.Disposition.ShouldBe(RoomPullRequestDisposition.Opened, "the branch changed — this must open a FRESH PR, not report AlreadyOpened against the stale turn-1 branch");

        var manifest = await SingleManifestRowAsync(runId, teamId);
        manifest.Branch.ShouldBe("codespace/integration/run/turn2", "the manifest row must be overwritten to reflect the run's CURRENT frontier, not left pointing at turn 1");
        manifest.PullRequestUrl.ShouldBe(reOpened.Url);

        (await ManifestRowCountAsync(runId, teamId)).ShouldBe(1, "the SAME alias's row is updated in place — never a second row for the same alias");
    }

    [Fact]
    public async Task A_run_still_in_progress_throws_even_with_a_published_branch_already_on_the_tape()
    {
        // Hidden-dependency sweep finding: a supervisor run sits Suspended BETWEEN turns even after an earlier
        // turn's merge already pushed a real branch — WorkflowRun.OutputsJson (repositoryId) is written only once,
        // at the run's OWN terminal completion. Opening a PR against a mid-run frontier that can still move is
        // unsafe (see the stale-branch test above) — the service must refuse until the run itself is terminal.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);   // stamped Success by the helper...

        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1");
        await StampTerminalRepositoryIdAsync(runId, repoId);

        using (var scope = _fixture.BeginScope())   // ...un-stamp it back to a live, non-terminal status
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
            run.Status = WorkflowRunStatus.Suspended;
            await db.SaveChangesAsync();
        }

        using var verify = _fixture.BeginScope();
        var service = verify.Resolve<IRoomPullRequestService>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => service.OpenAsync(runId, teamId, actorUserId: null, CancellationToken.None));
        ex.Message.ShouldContain("still in progress", Case.Insensitive);
    }

    [Fact]
    public async Task A_single_repo_run_with_no_repositoryId_output_throws()
    {
        // The branch exists but the terminal output never carried its owning repository (a degraded run, or one
        // authored before this field existed) — the service must fail loud, never guess a repository.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedSingleRepoMergeAsync(runId, teamId, "codespace/integration/run/turn1");
        // WorkflowRun.OutputsJson stays "{}" — repositoryId was never stamped.

        using var scope = _fixture.BeginScope();
        var service = scope.Resolve<IRoomPullRequestService>();

        await Should.ThrowAsync<InvalidOperationException>(() => service.OpenAsync(runId, teamId, actorUserId: null, CancellationToken.None));
    }

    [Fact]
    public async Task A_run_with_no_published_branch_throws()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        // No spawn, no merge — the run simply never published anything.
        using var scope = _fixture.BeginScope();
        var service = scope.Resolve<IRoomPullRequestService>();

        await Should.ThrowAsync<InvalidOperationException>(() => service.OpenAsync(runId, teamId, actorUserId: null, CancellationToken.None));
    }

    [Fact]
    public async Task A_multi_repo_run_opens_one_PR_per_repository_isolating_a_degraded_entry()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var webRepoId = await SeedBoundRepositoryAsync(teamId);
        var apiRepoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        // A REAL git integration already produced these branches — trust the tape (I2), the integrator itself is
        // covered by its own flow tests. The THIRD entry has no resolvable repository id (a degraded capture) —
        // proving the service isolates it as Failed rather than crashing the other two repos' opens.
        await SeedMultiRepoMergeAsync(runId, teamId,
            (webRepoId, "web", "codespace/integration/run/turn1", "main"),
            (apiRepoId, "api", "codespace/integration/run/turn1", "main"),
            (null, "degraded", "codespace/integration/run/turn1", "main"));

        var result = await OpenAsync(runId, teamId);

        result.PullRequests.Count.ShouldBe(3);
        result.PullRequests.Where(p => p.Disposition == RoomPullRequestDisposition.Opened).Select(p => p.Alias).ShouldBe(new[] { "web", "api" }, ignoreOrder: true);

        var degraded = result.PullRequests.Single(p => p.Alias == "degraded");
        degraded.Disposition.ShouldBe(RoomPullRequestDisposition.Failed);
        degraded.RepositoryId.ShouldBeNull();
        degraded.Error.ShouldNotBeNullOrEmpty();

        (await ManifestRowCountAsync(runId, teamId)).ShouldBe(2, "only the two genuinely-opened repos get a manifest row");
    }

    [Fact]
    public async Task A_multi_repo_repeat_call_reuses_the_repos_that_already_opened_and_only_opens_the_rest()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var webRepoId = await SeedBoundRepositoryAsync(teamId);
        var apiRepoId = await SeedBoundRepositoryAsync(teamId);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedMultiRepoMergeAsync(runId, teamId,
            (webRepoId, "web", "codespace/integration/run/turn1", "main"),
            (apiRepoId, "api", "codespace/integration/run/turn1", "main"));

        var first = await OpenAsync(runId, teamId);
        var webUrl = first.PullRequests.Single(p => p.Alias == "web").Url;
        var apiUrl = first.PullRequests.Single(p => p.Alias == "api").Url;

        var second = await OpenAsync(runId, teamId);

        second.PullRequests.ShouldAllBe(p => p.Disposition == RoomPullRequestDisposition.AlreadyOpened);
        second.PullRequests.Single(p => p.Alias == "web").Url.ShouldBe(webUrl);
        second.PullRequests.Single(p => p.Alias == "api").Url.ShouldBe(apiUrl);

        (await ManifestRowCountAsync(runId, teamId)).ShouldBe(2);
    }

    // ─── Action driver ──────────────────────────────────────────────────────────────

    private async Task<RoomPullRequestResult> OpenAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRoomPullRequestService>().OpenAsync(runId, teamId, actorUserId: null, CancellationToken.None);
    }

    private async Task<PublishManifest> SingleManifestRowAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var rows = await scope.Resolve<IPublishManifestStore>().ListForWorkflowRunAsync(runId, teamId, CancellationToken.None);
        return rows.Single();
    }

    private async Task<int> ManifestRowCountAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<IPublishManifestStore>().ListForWorkflowRunAsync(runId, teamId, CancellationToken.None)).Count;
    }

    // ─── Seeding ──────────────────────────────────────────────────────────────────────

    /// <summary>Seeds a manual run and immediately stamps it Success — <c>SeedManualRunAsync</c> defaults to <c>Enqueued</c> (mirroring the engine's own entry state for tests that drive the engine), but <see cref="IRoomPullRequestService.OpenAsync"/> now requires a TERMINAL run (a real gap the hidden-dependency sweep found: a mid-run Suspended supervisor already has a genuine merged branch on its decision tape, but its run-level OutputsJson isn't written until the run's own terminal completion) — this suite hand-seeds the decision tape directly, bypassing the engine entirely, so the run's own status needs the same hand-stamp.</summary>
    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        var workflowId = await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "room-open-pr-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var mutate = _fixture.BeginScope();
        var db = mutate.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.Status = WorkflowRunStatus.Success;
        await db.SaveChangesAsync();

        return runId;
    }

    /// <summary>Registered under <c>ProviderKind.Git</c> — the test container's <c>TestRepositoryProvider</c> gives it a real <c>IPullRequestWriteCapability</c> (fixed number 777, deterministic URL), so opening a PR never reaches a real GitHub/GitLab.</summary>
    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "local", BaseUrl = $"https://local-{suffix}" });

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
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
            ExternalId = $"ext-{suffix}", NamespacePath = "org", Name = "repo", FullPath = $"org/repo-{suffix}",
            DefaultBranch = "main", WebUrl = $"https://local-{suffix}/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    /// <summary>Hand-seeds a TERMINAL single-repo Merge decision's outcome — the exact JSON <c>ProjectIntegrationResult</c> emits, which <see cref="SupervisorOutcome.ReadFinalIntegratedBranch"/> reads. <c>Sequence</c> is DELIBERATELY left unset — <c>ValueGeneratedOnAdd()</c>, a real Postgres bigserial.</summary>
    private async Task SeedSingleRepoMergeAsync(Guid runId, Guid teamId, string integratedBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var outcome = JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch, appliedCount = 1, reason = (string?)null, excludedAgents = Array.Empty<string>() } }, AgentJson.Options);

        await AddTerminalDecisionAsync(db, runId, teamId, SupervisorDecisionKinds.Merge, outcome);
    }

    /// <summary>Hand-seeds a TERMINAL multi-repo Merge decision's <c>repositories[]</c> outcome — the shape <c>RealSupervisorActionExecutor.ProjectRepoBlock</c> emits, which <see cref="SupervisorOutcome.ReadFinalRepositoryBranches"/> reads. A null repository id models a degraded capture (no resolvable repo).</summary>
    private async Task SeedMultiRepoMergeAsync(Guid runId, Guid teamId, params (Guid? RepositoryId, string Alias, string SourceBranch, string TargetBranch)[] repos)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var blocks = repos.Select(r => new
        {
            repositoryId = r.RepositoryId,
            alias = r.Alias,
            status = "Clean",
            integratedBranch = r.SourceBranch,
            baseBranch = r.TargetBranch,
        }).ToList();

        var outcome = JsonSerializer.Serialize(new { integration = new { status = "Clean", reason = (string?)null, repositories = blocks } }, AgentJson.Options);

        await AddTerminalDecisionAsync(db, runId, teamId, SupervisorDecisionKinds.Merge, outcome);
    }

    private static async Task AddTerminalDecisionAsync(CodeSpaceDbContext db, Guid runId, Guid teamId, string decisionKind, string outcomeJson)
    {
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = decisionKind, IdempotencyKey = $"{decisionKind}-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcomeJson,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Mirrors what the workflow engine's terminal-node binding does in production (<c>SupervisorDefinitionBuilder.TerminalInputs</c> → <c>WorkflowRun.OutputsJson</c>).</summary>
    private async Task StampTerminalRepositoryIdAsync(Guid runId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.OutputsJson = JsonSerializer.Serialize(new { repositoryId = repositoryId.ToString() });
        await db.SaveChangesAsync();
    }
}
