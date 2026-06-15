using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
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
        Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, "1");
        try
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
        finally
        {
            Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, null);
        }
    }

    [Fact]
    public async Task A_repo_in_another_team_yields_no_grounding_and_the_planner_runs_task_only()
    {
        Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, "1");
        try
        {
            var seed = await SeedTeamWithBoundRepoAsync();

            // A SECOND team the caller is in — but the repo belongs to seed.TeamId. Planning AS this other team must
            // NOT resolve the repo: the team-scoped load finds nothing (no cross-team read), grounding degrades to null.
            var (otherTeamId, otherUserId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

            var recorder = await PlanWithRecordingPlannerAsync(otherTeamId, otherUserId, seed.RepositoryId);

            recorder.LastRequest.ShouldNotBeNull();
            recorder.LastRequest!.GroundingContext.ShouldBeNull("a repo in another team must yield no grounding — fail-closed, never a cross-team read");
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, null);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<RecordingWorkflowPlanner> PlanWithRecordingPlannerAsync(Guid teamId, Guid userId, Guid repositoryId)
    {
        var recorder = new RecordingWorkflowPlanner();

        // Child-scope override at the IWorkflowPlanner seam: the scoped WorkflowPlanningService resolves THIS fake,
        // so the test captures the exact request (incl. the grounding the service folded in) the planner sees.
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "test", Roles.Admin)).As<CodeSpace.Core.Services.Identity.ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<CodeSpace.Core.Services.Identity.ICurrentTeam>().SingleInstance();
            b.RegisterInstance(recorder).As<IWorkflowPlanner>().SingleInstance();
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
}
