using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Tasks.SpecPreview;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Tasks;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.IntegrationTests.Binding;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// PR-D Slice 2 integration: the planner's repo-metadata GROUNDING, on real Postgres. A bound repo on the
/// <see cref="TestRepositoryProvider"/> (ProviderKind.Git) returns a known root tree; the real
/// <c>WorkflowPlanningService</c> resolves it TEAM-SCOPED via <c>RepoGroundingProvider</c> and folds the
/// honest "top-level layout" string into <c>request.GroundingContext</c> before invoking the planner.
///
/// <para>Fidelity (Rule 12): real Postgres, the full command → handler → service → RepoGroundingProvider →
/// the registry-resolved <c>IRepositorySourceCapability</c> path, and the real team-scoped binding lookup are
/// ALL real. The PLANNER is faked at the <see cref="IWorkflowPlanner"/> seam by a recording fake (child-scope
/// override) so the test asserts the EXACT grounding the service built — no LLM call. Tenancy is proven by a
/// repo in a DIFFERENT team yielding no grounding (the team-scoped load finds nothing — no cross-team read).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PlannerGroundingFlowTests
{
    private readonly PostgresFixture _fixture;

    public PlannerGroundingFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_repo_in_the_callers_team_grounds_the_plan_with_its_top_level_layout()
    {
        var seed = await SeedTeamWithBoundRepoAsync();

        var recorder = await PlanWithRecordingPlannerAsync(seed.TeamId, seed.UserId, seed.RepositoryId);

        recorder.LastRequest.ShouldNotBeNull();
        recorder.LastRequest!.GroundingContext.ShouldNotBeNull("the bound repo is in the caller's team, so the service must assemble grounding");
        recorder.LastRequest.GroundingContext!.ShouldContain("top-level layout");
        recorder.LastRequest.GroundingContext.ShouldContain(TestRepositoryProvider.RootEntryNames[0], Case.Sensitive, "the real root tree's first entry must surface in the grounding");

        // Honesty guard end-to-end: the assembled string never over-claims.
        recorder.LastRequest.GroundingContext.ShouldNotContain("analyzed your codebase", Case.Insensitive);
    }

    [Fact]
    public async Task A_repo_in_another_team_yields_no_grounding_and_the_planner_runs_task_only()
    {
        var seed = await SeedTeamWithBoundRepoAsync();

        // A SECOND team the caller is in — but the repo belongs to seed.TeamId. Planning AS this other team must
        // NOT resolve the repo: the team-scoped load finds nothing (no cross-team read), grounding degrades to null.
        var (otherTeamId, otherUserId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var recorder = await PlanWithRecordingPlannerAsync(otherTeamId, otherUserId, seed.RepositoryId);

        recorder.LastRequest.ShouldNotBeNull();
        recorder.LastRequest!.GroundingContext.ShouldBeNull("a repo in another team must yield no grounding — fail-closed, never a cross-team read");
    }

    [Fact]
    public async Task A_provider_read_failure_degrades_grounding_to_null_and_the_planner_still_runs_task_only()
    {
        // The repo IS in the caller's team — the team-scoped DB load succeeds and the real RepoGroundingProvider
        // proceeds to the provider read. We then make that read THROW (scope check) to prove the catch-block
        // degrade-to-null path: grounding becomes null, but planning never fails.
        var seed = await SeedTeamWithBoundRepoAsync();

        var recorder = await PlanWithRecordingPlannerAsync(seed.TeamId, seed.UserId, seed.RepositoryId, configure: b =>
            b.RegisterInstance(new ThrowingScopeChecker()).As<IScopeChecker>().SingleInstance());

        // The planner STILL ran (the provider failure degraded grounding, it did not fail the planning call)...
        recorder.LastRequest.ShouldNotBeNull("a provider read failure must degrade grounding, never fail planning");

        // ...with NO grounding folded in (the catch returned null).
        recorder.LastRequest!.GroundingContext.ShouldBeNull("a provider/scope read failure must degrade grounding to null");
    }

    [Fact]
    public async Task A_repository_without_a_usable_credential_is_unavailable_not_an_observed_empty_tree()
    {
        var seed = await SeedTeamWithBoundRepoAsync();
        using (var write = _fixture.BeginScope())
            await write.Resolve<CodeSpaceDbContext>().Repository.Where(r => r.Id == seed.RepositoryId).ExecuteUpdateAsync(s => s.SetProperty(r => r.CredentialId, (Guid?)null));
        var recorder = await PlanWithRecordingPlannerAsync(seed.TeamId, seed.UserId, seed.RepositoryId);
        recorder.LastRequest.ShouldNotBeNull();
        recorder.LastRequest.GroundingContext.ShouldBeNull("missing credentials cannot establish that the repository root is empty");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_evidence_distinguishes_an_observed_empty_root_from_a_nonempty_root(bool empty)
    {
        var seed = await SeedTeamWithBoundRepoAsync();
        var provider = new RecordingEvidenceProvider { EmptyRoot = empty };
        using var scope = EvidenceScope(provider);
        var context = await scope.Resolve<ITaskSpecEvidenceReader>().CaptureAsync(new(seed.TeamId, "Investigate this project", seed.RepositoryId), CancellationToken.None);
        context.Repository.State.ShouldBe(empty ? TaskSpecRepositoryState.ObservedEmpty : TaskSpecRepositoryState.Observed);
        context.Repository.Reference.ShouldBe(provider.Reference);
        context.Grounded.ShouldBeTrue();
        context.Sources.Select(s => s.Id).ShouldBe(new[] { "goal", "repository-layout" });
        provider.TreeReferences.ShouldBe(new[] { provider.Reference });
    }

    [Fact]
    public async Task Typed_evidence_without_credentials_does_not_observe_a_tree()
    {
        var seed = await SeedTeamWithBoundRepoAsync();
        using (var write = _fixture.BeginScope())
            await write.Resolve<CodeSpaceDbContext>().Repository.Where(r => r.Id == seed.RepositoryId).ExecuteUpdateAsync(s => s.SetProperty(r => r.CredentialId, (Guid?)null));
        var provider = new RecordingEvidenceProvider { EmptyRoot = true };
        using var scope = EvidenceScope(provider);
        var context = await scope.Resolve<ITaskSpecEvidenceReader>().CaptureAsync(new(seed.TeamId, "Inspect the task", seed.RepositoryId), CancellationToken.None);
        context.Repository.State.ShouldBe(TaskSpecRepositoryState.Unavailable);
        context.Grounded.ShouldBeFalse();
        provider.TreeReferences.ShouldBeEmpty();
        context.Sources.Select(s => s.Id).ShouldBe(new[] { "goal" });
    }

    [Fact]
    public async Task Typed_evidence_never_reads_a_different_teams_repository()
    {
        var seed = await SeedTeamWithBoundRepoAsync();
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var provider = new RecordingEvidenceProvider();
        using var scope = EvidenceScope(provider);
        var context = await scope.Resolve<ITaskSpecEvidenceReader>().CaptureAsync(new(teamId, "Inspect the task", seed.RepositoryId), CancellationToken.None);
        context.Repository.State.ShouldBe(TaskSpecRepositoryState.Unavailable);
        provider.TreeReferences.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_commit_resolution_is_unknown_even_when_the_provider_would_return_an_empty_tree()
    {
        var seed = await SeedTeamWithBoundRepoAsync();
        var provider = new RecordingEvidenceProvider { Reference = null, EmptyRoot = true };
        using var scope = EvidenceScope(provider);
        var context = await scope.Resolve<ITaskSpecEvidenceReader>().CaptureAsync(new(seed.TeamId, "Inspect", seed.RepositoryId), CancellationToken.None);
        context.Repository.State.ShouldBe(TaskSpecRepositoryState.Unavailable);
        provider.TreeReferences.ShouldBeEmpty();
    }

    [Fact]
    public async Task Model_selected_dependency_reads_remain_pinned_when_the_default_branch_moves()
    {
        var seed = await SeedTeamWithBoundRepoAsync();
        var provider = new RecordingEvidenceProvider();
        using var scope = EvidenceScope(provider);
        var reader = scope.Resolve<ITaskSpecEvidenceReader>();
        var captured = await reader.CaptureAsync(new(seed.TeamId, "Validate the result", seed.RepositoryId), CancellationToken.None);
        provider.Reference = new string('b', 40);
        var context = await reader.ReadFilesAsync(captured, ["arbitrary/驗收.txt"], CancellationToken.None);
        context.ReadFailures.ShouldBeEmpty();
        var evidence = context.Sources.Single(s => s.Kind == "repository-file");
        evidence.Path.ShouldBe("arbitrary/驗收.txt");
        evidence.Reference.ShouldBe(captured.Repository.Reference);
        evidence.Content.ShouldBe(RecordingEvidenceProvider.FileText);
        evidence.ContentDigest.ShouldBe(TaskSpecSource.Create("x", "x", RecordingEvidenceProvider.FileText).ContentDigest);
        provider.FileReads.ShouldBe(new[] { ("arbitrary/驗收.txt", captured.Repository.Reference) });
    }

    [Fact]
    public async Task Revoked_credential_between_observation_and_dependency_reads_is_freshly_denied_in_the_same_scope()
    {
        var seed = await SeedTeamWithBoundRepoAsync();
        var provider = new RecordingEvidenceProvider();
        using var scope = EvidenceScope(provider);
        var reader = scope.Resolve<ITaskSpecEvidenceReader>();
        var captured = await reader.CaptureAsync(new(seed.TeamId, "Inspect", seed.RepositoryId), CancellationToken.None);
        using (var write = _fixture.BeginScope())
            await write.Resolve<CodeSpaceDbContext>().Repository.Where(r => r.Id == seed.RepositoryId).ExecuteUpdateAsync(s => s.SetProperty(r => r.CredentialId, (Guid?)null));
        var context = await reader.ReadFilesAsync(captured, ["custom/requirements.md"], CancellationToken.None);
        provider.FileReads.ShouldBeEmpty("an EF tracked credential must not outlive fresh access validation");
        context.ReadFailures.ShouldHaveSingleItem();
        context.Sources.ShouldBe(captured.Sources);
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("provider")]
    public async Task Soft_deleted_source_authority_is_not_reused_in_the_same_scope(string deletedEntity)
    {
        var seed = await SeedTeamWithBoundRepoAsync();
        var provider = new RecordingEvidenceProvider();
        using var scope = EvidenceScope(provider);
        var reader = scope.Resolve<ITaskSpecEvidenceReader>();
        var grounding = scope.Resolve<IRepoGroundingProvider>();
        var request = new TaskSpecEvidenceRequest(seed.TeamId, "Inspect", seed.RepositoryId);
        var captured = await reader.CaptureAsync(request, CancellationToken.None);
        (await grounding.BuildGroundingAsync(seed.RepositoryId, seed.TeamId, null, CancellationToken.None)).ShouldNotBeNull();
        using (var write = _fixture.BeginScope())
        {
            var db = write.Resolve<CodeSpaceDbContext>();
            var repository = await db.Repository.SingleAsync(r => r.Id == seed.RepositoryId);
            if (deletedEntity == "credential") await db.Credential.Where(c => c.Id == repository.CredentialId).ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedDate, DateTimeOffset.UtcNow));
            else await db.ProviderInstance.Where(i => i.Id == repository.ProviderInstanceId).ExecuteUpdateAsync(s => s.SetProperty(i => i.DeletedDate, DateTimeOffset.UtcNow));
        }
        var files = await reader.ReadFilesAsync(captured, ["custom/checks.md"], CancellationToken.None);
        provider.FileReads.ShouldBeEmpty("soft-deleted authority cannot supply new evidence");
        files.ReadFailures.ShouldHaveSingleItem();
        (await reader.CaptureAsync(request, CancellationToken.None)).Repository.State.ShouldBe(TaskSpecRepositoryState.Unavailable);
        (await grounding.BuildGroundingAsync(seed.RepositoryId, seed.TeamId, null, CancellationToken.None)).ShouldBeNull();
        provider.TreeReferences.Count.ShouldBe(2, "no new listing may be read after either authority record is deleted");
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("wrong-path")]
    [InlineData("throw")]
    public async Task Incomplete_file_evidence_remains_unknown(string failure)
    {
        var seed = await SeedTeamWithBoundRepoAsync();
        var provider = new RecordingEvidenceProvider { FileFailure = failure };
        using var scope = EvidenceScope(provider);
        var reader = scope.Resolve<ITaskSpecEvidenceReader>();
        var captured = await reader.CaptureAsync(new(seed.TeamId, "Inspect", seed.RepositoryId), CancellationToken.None);
        var context = await reader.ReadFilesAsync(captured, ["checks/contract.data"], CancellationToken.None);
        context.ReadFailures.ShouldHaveSingleItem();
        context.Sources.ShouldBe(captured.Sources);
        provider.FileReads.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Dependency_read_budget_and_relative_paths_bound_provider_access_without_a_language_whitelist()
    {
        var seed = await SeedTeamWithBoundRepoAsync();
        var provider = new RecordingEvidenceProvider();
        using var scope = EvidenceScope(provider);
        var reader = scope.Resolve<ITaskSpecEvidenceReader>();
        var captured = await reader.CaptureAsync(new(seed.TeamId, "Inspect", seed.RepositoryId), CancellationToken.None);
        var context = await reader.ReadFilesAsync(captured, ["../private", "first.contract", "first.contract", "nested/second.contract", "third.contract", "outside-budget"], CancellationToken.None);
        context.ReadFailures.Count.ShouldBe(2);
        provider.FileReads.Select(r => r.Path).ShouldBe(new[] { "first.contract", "nested/second.contract", "third.contract" });
        context.Sources.Count.ShouldBe(captured.Sources.Count + 3);
    }

    private ILifetimeScope EvidenceScope(RecordingEvidenceProvider provider) => _fixture.BeginScope(b => b.RegisterInstance(new ProviderRegistry([provider])).As<IProviderRegistry>().SingleInstance());

    // Only the provider source boundary is simulated; team/credential checks and reader composition use real PostgreSQL.
    private sealed class RecordingEvidenceProvider : IRepositorySourceCapability
    {
        public const string FileText = "The project verifies output with custom-check --assert; inspect the dependencies before execution.";
        public ProviderKind Kind => ProviderKind.Git;
        public string? Reference { get; set; } = new string('a', 40);
        public bool EmptyRoot { get; init; }
        public string? FileFailure { get; init; }
        public List<string?> TreeReferences { get; } = [];
        public List<(string Path, string? Reference)> FileReads { get; } = [];
        public Task<IReadOnlyList<RemoteBranch>> ListBranchesAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RemoteBranch>>([new() { Name = repository.DefaultBranch, IsDefault = true, CommitSha = Reference }]);
        public Task<IReadOnlyList<RemoteTreeEntry>> ListTreeAsync(ProviderContext context, RemoteRepository repository, string? path, string? reference, CancellationToken cancellationToken)
        {
            TreeReferences.Add(reference);
            return Task.FromResult<IReadOnlyList<RemoteTreeEntry>>(EmptyRoot ? [] : [new() { Name = "custom", Path = "custom", Type = RemoteTreeEntryType.Directory }]);
        }
        public Task<RemoteFileContent> GetFileAsync(ProviderContext context, RemoteRepository repository, string path, string? reference, CancellationToken cancellationToken)
        {
            FileReads.Add((path, reference));
            if (FileFailure == "throw") throw new IOException("source unavailable");
            return Task.FromResult(new RemoteFileContent { Path = FileFailure == "wrong-path" ? "different" : path, Name = path, Text = FileFailure == "oversized" ? new string('x', 16001) : FileText, IsBinary = FileFailure == "binary", IsTruncated = FileFailure == "truncated" });
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<RecordingWorkflowPlanner> PlanWithRecordingPlannerAsync(Guid teamId, Guid userId, Guid repositoryId, Action<ContainerBuilder>? configure = null)
    {
        var recorder = new RecordingWorkflowPlanner();

        // Child-scope override at the IWorkflowPlanner seam: the scoped WorkflowPlanningService resolves THIS fake,
        // so the test captures the exact request (incl. the grounding the service folded in) the planner sees.
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "test", Roles.Admin)).As<CodeSpace.Core.Services.Identity.ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<CodeSpace.Core.Services.Identity.ICurrentTeam>().SingleInstance();
            b.RegisterInstance(recorder).As<IWorkflowPlanner>().SingleInstance();

            configure?.Invoke(b);
        });

        await scope.Resolve<IMediator>().Send(new PlanWorkflowFromTaskCommand { TaskText = "Improve onboarding", RepositoryId = repositoryId });

        return recorder;
    }

    private async Task<SeedResult> SeedTeamWithBoundRepoAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "instance",
            BaseUrl = $"https://git-{suffix}.local", OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret"),
        };
        var credential = new Credential
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService,
            AuthType = AuthType.Pat, DisplayName = "connection",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "tkn" })), Status = CredentialStatus.Active,
        };
        var repo = new Repository
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, CredentialId = credential.Id,
            ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = "acme/api",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://git.local/acme/api", Status = RepositoryStatus.Active,
        };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repo);
        await db.SaveChangesAsync();

        return new SeedResult(teamId, userId, repo.Id);
    }

    private sealed record SeedResult(Guid TeamId, Guid UserId, Guid RepositoryId);

    /// <summary>Records the request the planner was handed — proves the service folded the grounding in before calling it.</summary>
    private sealed class RecordingWorkflowPlanner : IWorkflowPlanner
    {
        public WorkflowPlanRequest? LastRequest { get; private set; }

        public Task<PlannedWorkflow> PlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new PlannedWorkflow
            {
                Goal = "g",
                Subtasks = new[] { new PlannedSubtask { Id = "s1", Title = "t", Instruction = "i" } },
                RecommendedWorkflowKind = "analysis",
            });
        }
    }

    /// <summary>Throws from the scope-check seam RepoGroundingProvider hits AFTER the team-scoped DB load — models a provider/scope read failure so the test exercises the catch-block degrade-to-null path.</summary>
    private sealed class ThrowingScopeChecker : IScopeChecker
    {
        public ScopeCheckOutcome Check(ProviderKind kind, Type capabilityType, IReadOnlyCollection<string>? grantedScopes) =>
            throw new InvalidOperationException("scope check unavailable");

        public void EnsureCapability(Credential credential, ProviderKind kind, Type capabilityType) =>
            throw new InvalidOperationException("scope check unavailable");
    }
}
