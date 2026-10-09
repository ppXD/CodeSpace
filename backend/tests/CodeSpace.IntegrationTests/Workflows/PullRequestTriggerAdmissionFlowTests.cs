using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.PullRequest;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Who may start a run with a pull request, and how often — through the real <c>RunSourceDispatcher</c>,
/// <c>PullRequestTriggerAdmission</c>, claim table, auditor and <c>RunStarter</c> against Postgres. The GitLab cases ask
/// the real GitLab provider class for the author's standing over the wire, from a loopback <see cref="StubProviderHost"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PullRequestTriggerAdmissionFlowTests
{
    private const string Opened = "trigger.pr.opened";
    private const string Updated = "trigger.pr.updated";

    private readonly PostgresFixture _fixture;

    public PullRequestTriggerAdmissionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ─── Authors ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Opened, RepositoryVisibility.Public)]
    [InlineData(Updated, RepositoryVisibility.Public)]
    [InlineData(Opened, RepositoryVisibility.Internal)]
    [InlineData(Updated, RepositoryVisibility.Internal)]
    public async Task On_a_public_or_internal_repository_an_existing_activation_admits_only_members_and_records_the_refusal_once(string typeKey, RepositoryVisibility visibility)
    {
        // Internal too: every signed-in user of the instance (GitLab) or member of the enterprise (GitHub) can read an
        // internal repository, fork it and open a pull request from the fork.
        var seed = await SeedAsync(ProviderKind.GitHub);
        await SeedActivationAsync(seed, typeKey, "{}");
        var outsider = new PullRequestOrigin { AuthorAssociation = PullRequestAuthorAssociation.None, IsFork = true, HeadRepositoryFullName = "evil/repo", RepositoryVisibility = visibility };

        await PublishAsync(Event(typeKey, seed.RepositoryId, number: 1, outsider));
        await PublishAsync(Event(typeKey, seed.RepositoryId, number: 1, outsider));

        (await CountRunsAsync(seed.WorkflowId)).ShouldBe(0);
        (await LoadRefusalsAsync(seed.TeamId, WorkflowRunRequestRejectionReasons.AuthorNotMember)).Count.ShouldBe(1, customMessage: "a reopen loop is a stream of genuinely signed deliveries: one row per (activation, PR) per day");
        (await LoadRefusalsAsync(seed.TeamId, WorkflowRunRequestRejectionReasons.NoMatchingActivation)).ShouldBeEmpty("a refusal is its own reason — it must not also read as nothing listening");
    }

    [Theory]
    [InlineData(PullRequestAuthorAssociation.Member, "{}", 1)]
    [InlineData(PullRequestAuthorAssociation.Contributor, "{}", 0)]
    [InlineData(PullRequestAuthorAssociation.Unknown, "{}", 0)]
    [InlineData(PullRequestAuthorAssociation.None, """{"authors":"any"}""", 1)]
    public async Task On_a_public_repository_the_authors_filter_decides(PullRequestAuthorAssociation association, string configJson, int expectedRuns)
    {
        var seed = await SeedAsync(ProviderKind.GitHub);
        await SeedActivationAsync(seed, Opened, configJson);

        await PublishAsync(Event(Opened, seed.RepositoryId, number: 2, new PullRequestOrigin { AuthorAssociation = association, RepositoryVisibility = RepositoryVisibility.Public }));

        (await CountRunsAsync(seed.WorkflowId)).ShouldBe(expectedRuns);
    }

    [Fact]
    public async Task On_a_private_repository_an_existing_activation_keeps_admitting_anyone_and_members_can_still_be_chosen()
    {
        var seed = await SeedAsync(ProviderKind.GitHub);
        var membersOnly = await SeedSiblingWorkflowAsync(seed);
        await SeedActivationAsync(seed, Opened, "{}");
        await SeedActivationAsync(membersOnly, Opened, """{"authors":"members"}""");

        await PublishAsync(Event(Opened, seed.RepositoryId, number: 3, new PullRequestOrigin { AuthorAssociation = PullRequestAuthorAssociation.None, IsFork = true, RepositoryVisibility = RepositoryVisibility.Private }));

        (await CountRunsAsync(seed.WorkflowId)).ShouldBe(1, customMessage: "the default only narrows on PUBLIC repositories");
        (await CountRunsAsync(membersOnly.WorkflowId)).ShouldBe(0, customMessage: "an explicit members choice holds on any repository");
    }

    [Theory]
    [InlineData("66", true, 0)]     // an outsider pushed to a member's PR whose head lives in the outsider's fork
    [InlineData("9", true, 1)]      // the member author pushed to their own fork PR
    [InlineData("66", false, 1)]    // someone else pushed to a head in the target repository, which takes a role there
    public async Task On_a_public_repository_new_commits_are_admitted_only_when_the_author_or_a_member_pushed_them(string pusher, bool isFork, int expectedRuns)
    {
        var seed = await SeedAsync(ProviderKind.GitHub);
        await SeedActivationAsync(seed, Updated, "{}");
        var origin = new PullRequestOrigin { AuthorExternalId = "9", AuthorAssociation = PullRequestAuthorAssociation.Member, IsFork = isFork, HeadRepositoryFullName = isFork ? "evil/repo" : "acme/api", RepositoryVisibility = RepositoryVisibility.Public, PusherExternalId = pusher };

        await PublishAsync(Event(Updated, seed.RepositoryId, number: 13, origin));

        (await CountRunsAsync(seed.WorkflowId)).ShouldBe(expectedRuns, customMessage: "a member's PR from an outsider's fork carries the outsider's code once the outsider pushes — check PullRequestTriggerAuthors.AdmitsPusher");
        if (expectedRuns == 0) (await LoadRefusalsAsync(seed.TeamId, WorkflowRunRequestRejectionReasons.AuthorNotMember)).ShouldHaveSingleItem().ShouldContain("pushed by");
    }

    [Fact]
    public async Task The_run_payload_says_who_wrote_the_pr_and_where_its_head_lives()
    {
        var seed = await SeedAsync(ProviderKind.GitHub);
        await SeedActivationAsync(seed, Updated, """{"authors":"any"}""");

        await PublishAsync(Event(Updated, seed.RepositoryId, number: 4, new PullRequestOrigin { AuthorAssociation = PullRequestAuthorAssociation.Contributor, IsFork = true, HeadRepositoryFullName = "evil/repo", RepositoryVisibility = RepositoryVisibility.Public }));

        var payload = await LoadOnlyPayloadAsync(seed.WorkflowId);
        payload.GetProperty("authorAssociation").GetString().ShouldBe("contributor");
        payload.GetProperty("isFork").GetBoolean().ShouldBeTrue();
        payload.GetProperty("headRepositoryFullName").GetString().ShouldBe("evil/repo");
    }

    // ─── GitLab standing ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(200, """{"id":51,"access_level":40}""", 1, "member")]
    [InlineData(200, """{"id":51,"access_level":20}""", 0, null)]
    [InlineData(404, """{"message":"404 Not found"}""", 0, null)]
    [InlineData(500, """{"message":"boom"}""", 0, null)]
    public async Task On_a_public_gitlab_project_the_authors_standing_is_asked_of_gitlab(int status, string memberBody, int expectedRuns, string? expectedAssociation)
    {
        using var gitlab = new StubProviderHost();
        var seed = await SeedAsync(ProviderKind.GitLab, gitlab.BaseUrl);
        gitlab.Answer("GET", $"/api/v4/projects/{seed.ExternalId}/members/all/51", status, memberBody);
        await SeedActivationAsync(seed, Opened, "{}");

        await PublishAsync(Event(Opened, seed.RepositoryId, number: 5, new PullRequestOrigin { AuthorExternalId = "51", RepositoryVisibility = RepositoryVisibility.Public, IsFork = true }));

        (await CountRunsAsync(seed.WorkflowId)).ShouldBe(expectedRuns, customMessage: "Developer and above is a member; Reporter, not a member at all, and an answer GitLab could not give are not");
        if (expectedAssociation != null) (await LoadOnlyPayloadAsync(seed.WorkflowId)).GetProperty("authorAssociation").GetString().ShouldBe(expectedAssociation);
        gitlab.Requests.ShouldContain(r => r.PrivateTokenHeader == "glpat-fake-test-token", customMessage: "the lookup authenticates as the repository's own credential");
    }

    [Theory]
    [InlineData(200, """{"id":66,"access_level":30}""", 1)]
    [InlineData(404, """{"message":"404 Not found"}""", 0)]
    public async Task On_a_public_gitlab_project_a_push_by_someone_else_to_a_fork_mr_asks_gitlab_about_the_pusher(int status, string pusherBody, int expectedRuns)
    {
        using var gitlab = new StubProviderHost();
        var seed = await SeedAsync(ProviderKind.GitLab, gitlab.BaseUrl);
        gitlab.Answer("GET", $"/api/v4/projects/{seed.ExternalId}/members/all/51", 200, """{"id":51,"access_level":40}""")
              .Answer("GET", $"/api/v4/projects/{seed.ExternalId}/members/all/66", status, pusherBody);
        await SeedActivationAsync(seed, Updated, "{}");

        await PublishAsync(Event(Updated, seed.RepositoryId, number: 14, new PullRequestOrigin { AuthorExternalId = "51", RepositoryVisibility = RepositoryVisibility.Public, IsFork = true, PusherExternalId = "66" }));

        (await CountRunsAsync(seed.WorkflowId)).ShouldBe(expectedRuns, customMessage: "a Developer pushing to a member's fork MR is a member's push; someone with no role on the project is not");
        gitlab.Requests.Count(r => r.PathAndQuery.Contains("/members/all/66")).ShouldBe(1, customMessage: "GitLab names the pusher but not their standing, so it is asked like the author's");
    }

    [Fact]
    public async Task One_delivery_asks_gitlab_once_however_many_activations_match_and_an_any_trigger_still_learns_the_standing()
    {
        using var gitlab = new StubProviderHost();
        var seed = await SeedAsync(ProviderKind.GitLab, gitlab.BaseUrl);
        var sibling = await SeedSiblingWorkflowAsync(seed);
        gitlab.Answer("GET", $"/api/v4/projects/{seed.ExternalId}/members/all/51", 200, """{"id":51,"access_level":30}""");
        await SeedActivationAsync(seed, Opened, """{"authors":"any"}""");
        await SeedActivationAsync(sibling, Opened, """{"authors":"members"}""");

        await PublishAsync(Event(Opened, seed.RepositoryId, number: 6, new PullRequestOrigin { AuthorExternalId = "51", RepositoryVisibility = RepositoryVisibility.Private }));

        gitlab.Requests.Count(r => r.PathAndQuery.Contains("/members/all/51")).ShouldBe(1);
        (await CountRunsAsync(sibling.WorkflowId)).ShouldBe(1);
        (await LoadOnlyPayloadAsync(seed.WorkflowId)).GetProperty("authorAssociation").GetString().ShouldBe("member", "the payload says who wrote the PR whichever filter the activation chose");
    }

    [Fact]
    public async Task A_lookup_gitlab_could_not_answer_is_asked_once_per_delivery_not_once_per_activation()
    {
        using var gitlab = new StubProviderHost();
        var seed = await SeedAsync(ProviderKind.GitLab, gitlab.BaseUrl);
        var sibling = await SeedSiblingWorkflowAsync(seed);
        gitlab.Answer("GET", $"/api/v4/projects/{seed.ExternalId}/members/all/51", 500, """{"message":"boom"}""");
        await SeedActivationAsync(seed, Opened, """{"authors":"members"}""");
        await SeedActivationAsync(sibling, Opened, """{"authors":"members"}""");

        await PublishAsync(Event(Opened, seed.RepositoryId, number: 11, new PullRequestOrigin { AuthorExternalId = "51", RepositoryVisibility = RepositoryVisibility.Public }));

        gitlab.Requests.Count(r => r.PathAndQuery.Contains("/members/all/51")).ShouldBe(1, customMessage: "an unanswered lookup costs up to its timeout; every matching activation repeating it would hold the delivery past the provider's own timeout");
        (await CountRunsAsync(seed.WorkflowId)).ShouldBe(0);
        (await CountRunsAsync(sibling.WorkflowId)).ShouldBe(0);
    }

    // ─── Debounce ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Opened)]
    [InlineData(Updated)]
    public async Task A_burst_for_one_head_starts_one_run_per_activation_and_records_the_debounce_once(string typeKey)
    {
        // A close/reopen loop: every reopen is a genuinely signed delivery for a head that already ran.
        var seed = await SeedAsync(ProviderKind.GitHub);
        var sibling = await SeedSiblingWorkflowAsync(seed);
        await SeedActivationAsync(seed, typeKey, "{}");
        await SeedActivationAsync(sibling, typeKey, "{}");

        for (var i = 0; i < 4; i++)
            await PublishAsync(Event(typeKey, seed.RepositoryId, number: 7, new PullRequestOrigin()));

        (await CountRunsAsync(seed.WorkflowId)).ShouldBe(1);
        (await CountRunsAsync(sibling.WorkflowId)).ShouldBe(1, customMessage: "the debounce is per activation: one workflow's run must not swallow another's");
        (await LoadRefusalsAsync(seed.TeamId, WorkflowRunRequestRejectionReasons.PullRequestDebounced)).Count.ShouldBe(2, customMessage: "one row per (activation, PR, head) per day, however many events the burst held");
    }

    [Theory]
    [InlineData(Opened)]
    [InlineData(Updated)]
    public async Task A_new_head_inside_the_window_still_starts_its_own_run(string typeKey)
    {
        // A member amending a push seconds later: the commit that ends up on the PR must get a run, on any repository.
        var seed = await SeedAsync(ProviderKind.GitHub);
        await SeedActivationAsync(seed, typeKey, "{}");

        await PublishAsync(Event(typeKey, seed.RepositoryId, number: 15, new PullRequestOrigin { AuthorAssociation = PullRequestAuthorAssociation.Member, RepositoryVisibility = RepositoryVisibility.Private }, headSha: "sha-first"));
        await PublishAsync(Event(typeKey, seed.RepositoryId, number: 15, new PullRequestOrigin { AuthorAssociation = PullRequestAuthorAssociation.Member, RepositoryVisibility = RepositoryVisibility.Private }, headSha: "sha-amended"));

        (await CountRunsAsync(seed.WorkflowId)).ShouldBe(2, customMessage: "the debounce holds off the same head, never newer code — check PullRequestTriggerAdmission.BuildDebounceKey");
        (await LoadRefusalsAsync(seed.TeamId, WorkflowRunRequestRejectionReasons.PullRequestDebounced)).ShouldBeEmpty();
        if (typeKey == Updated) (await LoadPayloadsAsync(seed.WorkflowId, "newHeadSha")).ShouldBe(new[] { "sha-first", "sha-amended" }, ignoreOrder: true);
    }

    [Fact]
    public async Task A_delivery_posted_again_after_the_window_holds_off_nothing()
    {
        // The delivery already started its run, so posting it again — an operator's Redeliver, or a captured body with
        // its own id — must not take the debounce claim a second time and swallow the next real event for that head.
        var seed = await SeedAsync(ProviderKind.GitHub);
        await SeedActivationAsync(seed, Opened, "{}");
        var original = Event(Opened, seed.RepositoryId, number: 16, new PullRequestOrigin());

        await PublishAsync(original);
        await ExpireDebounceClaimsAsync(seed.RepositoryId);
        await PublishAsync(Event(Opened, seed.RepositoryId, number: 16, new PullRequestOrigin(), original.ProviderEventId));
        await PublishAsync(Event(Opened, seed.RepositoryId, number: 16, new PullRequestOrigin()));

        (await CountRunsAsync(seed.WorkflowId)).ShouldBe(2, customMessage: "the reposted delivery is a duplicate and must leave the window alone — check the already-started check in RunSourceDispatcher.FireIfMatchesAsync");
        (await LoadRefusalsAsync(seed.TeamId, WorkflowRunRequestRejectionReasons.PullRequestDebounced)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_deliveries_for_one_pr_racing_each_other_start_one_run_per_activation()
    {
        var seed = await SeedAsync(ProviderKind.GitHub);
        var sibling = await SeedSiblingWorkflowAsync(seed);
        await SeedActivationAsync(seed, Opened, "{}");
        await SeedActivationAsync(sibling, Opened, "{}");

        using var firstScope = _fixture.BeginScope();
        using var secondScope = _fixture.BeginScope();
        await using var first = await firstScope.Resolve<CodeSpaceDbContext>().Database.BeginTransactionAsync();
        await using var second = await secondScope.Resolve<CodeSpaceDbContext>().Database.BeginTransactionAsync();

        await firstScope.Resolve<IMediator>().Publish(Event(Opened, seed.RepositoryId, number: 12, new PullRequestOrigin()));
        var contender = secondScope.Resolve<IMediator>().Publish(Event(Opened, seed.RepositoryId, number: 12, new PullRequestOrigin()));

        await Task.Delay(200);
        contender.IsCompleted.ShouldBeFalse("the second delivery must wait on the first's uncommitted debounce claim, not read past it");

        await first.CommitAsync();
        await contender.WaitAsync(TimeSpan.FromSeconds(10));
        await second.CommitAsync();

        (await CountRunsAsync(seed.WorkflowId)).ShouldBe(1);
        (await CountRunsAsync(sibling.WorkflowId)).ShouldBe(1, customMessage: "a reopen racing a reopen is still one run per activation — check the ON CONFLICT ... WHERE in WebhookClaimStore and the activation order in RunSourceDispatcher.LoadActiveActivationsAsync");
        (await LoadRefusalsAsync(seed.TeamId, WorkflowRunRequestRejectionReasons.PullRequestDebounced)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_redelivery_of_the_same_delivery_is_a_duplicate_not_a_debounce()
    {
        var seed = await SeedAsync(ProviderKind.GitHub);
        await SeedActivationAsync(seed, Opened, "{}");
        var delivery = Event(Opened, seed.RepositoryId, number: 8, new PullRequestOrigin());

        await PublishAsync(delivery);
        await PublishAsync(Event(Opened, seed.RepositoryId, number: 8, new PullRequestOrigin(), delivery.ProviderEventId));

        (await CountRunsAsync(seed.WorkflowId)).ShouldBe(1);
        (await LoadRefusalsAsync(seed.TeamId, WorkflowRunRequestRejectionReasons.PullRequestDebounced)).ShouldBeEmpty("the same delivery id holds the debounce claim, so the provider redelivering it is deduplicated by the run's idempotency key, as it always was");
    }

    [Fact]
    public async Task A_different_pull_request_is_not_debounced()
    {
        var seed = await SeedAsync(ProviderKind.GitHub);
        await SeedActivationAsync(seed, Opened, "{}");

        await PublishAsync(Event(Opened, seed.RepositoryId, number: 9, new PullRequestOrigin()));
        await PublishAsync(Event(Opened, seed.RepositoryId, number: 10, new PullRequestOrigin()));

        (await CountRunsAsync(seed.WorkflowId)).ShouldBe(2);
    }

    // ─── Events ────────────────────────────────────────────────────────────────

    /// <summary>One head unless a test says otherwise, so a repeated event is the same PR state arriving again — a reopen, a redelivery.</summary>
    private static NormalizedEvent Event(string typeKey, Guid repositoryId, int number, PullRequestOrigin origin, string? deliveryId = null, string headSha = "head-1")
    {
        var id = deliveryId ?? $"d-{Guid.NewGuid():N}";

        if (typeKey == Updated)
            return new PullRequestSynchronizedEvent { RepositoryId = repositoryId, ProviderEventId = id, OccurredAt = DateTimeOffset.UtcNow, ExternalPullRequestId = $"{number}", Number = number, PreviousHeadSha = "a", NewHeadSha = headSha, Origin = origin };

        return new PullRequestOpenedEvent { RepositoryId = repositoryId, ProviderEventId = id, OccurredAt = DateTimeOffset.UtcNow, ExternalPullRequestId = $"{number}", Number = number, Title = "t", SourceBranch = "main", TargetBranch = "main", AuthorExternalId = "9", AuthorName = "someone", WebUrl = "https://x", HeadSha = headSha, Origin = origin };
    }

    /// <summary>The window passing, without waiting it out: every debounce claim on this repository ends a minute ago.</summary>
    private async Task ExpireDebounceClaimsAsync(Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var pattern = $"pr-debounce:%:{repositoryId:N}:%";
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE webhook_claim SET claimed_at = now() - interval '2 minutes', expires_at = now() - interval '1 minute' WHERE claim_key LIKE {pattern}");
    }

    private async Task PublishAsync(NormalizedEvent ev)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IMediator>().Publish(ev);
    }

    // ─── Seeds ─────────────────────────────────────────────────────────────────

    private sealed record Seed(Guid TeamId, Guid UserId, Guid WorkflowId, Guid RepositoryId, string ExternalId);

    private async Task<Seed> SeedAsync(ProviderKind provider, string? baseUrl = null)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var externalId = Random.Shared.Next(100_000, 999_999).ToString();
        var repositoryId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = provider, DisplayName = "P", BaseUrl = baseUrl ?? $"https://p-{Guid.NewGuid():N}.invalid", ApiUrl = baseUrl };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, AuthType = AuthType.Pat, DisplayName = "PAT", EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt("{\"token\":\"glpat-fake-test-token\"}") };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(new Repository { Id = repositoryId, TeamId = teamId, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = externalId, NamespacePath = "acme", Name = "api", FullPath = "acme/api", WebUrl = "https://x/acme/api" });
        await db.SaveChangesAsync();

        return new Seed(teamId, userId, workflowId, repositoryId, externalId);
    }

    /// <summary>A second member of the same team publishing a second workflow — a delivery only ever reaches its own team.</summary>
    private async Task<Seed> SeedSiblingWorkflowAsync(Seed seed)
    {
        var userId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.User.Add(new User { Id = userId, Email = $"sibling-{userId:N}@test.local", Name = $"sibling-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = seed.TeamId, UserId = userId, Role = TeamRole.Admin, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
            await db.SaveChangesAsync();
        }

        return seed with { UserId = userId, WorkflowId = await CreateWorkflowAsync(seed.TeamId, userId) };
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = "admission-" + Guid.NewGuid().ToString("N")[..8], Description = null, Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = new List<WorkflowActivationInput>(), Enabled = true });
    }

    private async Task SeedActivationAsync(Seed seed, string typeKey, string configJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowActivation.Add(new WorkflowActivation { Id = Guid.NewGuid(), WorkflowId = seed.WorkflowId, TypeKey = typeKey, ConfigJson = configJson, Enabled = true, CreatedBy = seed.UserId, LastModifiedBy = seed.UserId });
        await db.SaveChangesAsync();
    }

    // ─── Reads ─────────────────────────────────────────────────────────────────

    private async Task<int> CountRunsAsync(Guid workflowId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().CountAsync(r => r.WorkflowId == workflowId);
    }

    private async Task<JsonElement> LoadOnlyPayloadAsync(Guid workflowId)
    {
        using var scope = _fixture.BeginScope();
        var payload = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.WorkflowId == workflowId).Select(r => r.RunRequest.NormalizedPayloadJson).SingleAsync();
        return JsonDocument.Parse(payload).RootElement;
    }

    private async Task<List<string>> LoadPayloadsAsync(Guid workflowId, string field)
    {
        using var scope = _fixture.BeginScope();
        var payloads = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.WorkflowId == workflowId).Select(r => r.RunRequest.NormalizedPayloadJson).ToListAsync();
        return payloads.Select(p => JsonDocument.Parse(p).RootElement.GetProperty(field).GetString()!).ToList();
    }

    private async Task<List<string>> LoadRefusalsAsync(Guid teamId, string reason)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRequest.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.Status == WorkflowRunRequestStatus.Rejected && r.Error!.StartsWith(reason + ":"))
            .Select(r => r.Error!)
            .ToListAsync();
    }
}
