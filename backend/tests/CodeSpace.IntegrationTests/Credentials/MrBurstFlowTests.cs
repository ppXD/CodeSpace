using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Webhooks;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// 🟢 High fidelity — a GitLab merge request that is labelled, closed and reopened, from the webhook to the token endpoint.
/// Every delivery goes in through the real <c>ReceiveWebhookCommand</c> (the repository webhook's secret, GitLab's own
/// payload through the real normalizer), then the real <c>RunSourceDispatcher</c> and <c>PullRequestTriggerAdmission</c>,
/// which asks GitLab for the author's standing with the repository's OAuth credential — refreshing it first when the access
/// token has aged out — against real Postgres. The one fake is GitLab, a <see cref="GitLabDoorkeeperStub"/>: refresh tokens
/// are single use and a refresh kills the access token it replaces, so a burst that refreshed twice would show as
/// <c>invalid_grant</c> in its counters.
///
/// <para>These are characterizations: they describe what the product does today, and they pass on it. They are the
/// scaffolding the fixes for the provider-credential failures stand on, and each fix adds its own red-to-green scenario
/// beside them.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MrBurstFlowTests
{
    private const string Opened = "trigger.pr.opened";

    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public MrBurstFlowTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task B1_a_burst_of_reopens_after_the_token_aged_out_refreshes_once_and_every_delivery_learns_the_authors_standing()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);

        // GitLab sends a close and a reopen within a second or two; three reopens racing is the harsher case: three API
        // requests, each in a scope of its own, each asking GitLab for the author's standing with the same expired token.
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => DeliverAsync(world, MergeRequestHook("reopen", iid: 5, headSha: "head-1")))).ConfigureAwait(false);

        Dump(gitlab);
        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "one refresh for the whole burst");
        gitlab.Count(StubEvent.Refused).ShouldBe(0, $"a delivery presented a refresh token GitLab had already consumed: {string.Join(", ", gitlab.RefusedRefreshTokens())}");
        gitlab.Count(StubEvent.Api401).ShouldBe(0, "no delivery asked GitLab with a token it had revoked");
        gitlab.Count(StubEvent.Api200).ShouldBe(3, "one standing lookup per delivery, however many activations match it, and each answered");
        (await CountRunsAsync(world.TeamId, world.WorkflowId).ConfigureAwait(false)).ShouldBe(1, "the three deliveries are one head: one run per activation, the rest debounced");
        (await CountRunsAsync(world.TeamId, world.SiblingWorkflowId).ConfigureAwait(false)).ShouldBe(1);
        (await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, world.Credential.CredentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-2", "the row holds the pair GitLab issued");
    }

    [Fact]
    public async Task B3_a_label_edit_and_a_close_start_nothing_a_reopen_starts_one_run_per_activation_and_a_second_one_inside_the_window_is_debounced()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);

        // Adding a label is an `update` with no `oldrev`: the head did not move, so nothing about the code changed.
        await DeliverAsync(world, MergeRequestHook("update", iid: 6, headSha: "head-1", labels: "bug")).ConfigureAwait(false);
        await ShouldHaveStartedAsync(world, 0, "a label edit").ConfigureAwait(false);
        gitlab.Count(StubEvent.Api200).ShouldBe(0, "a delivery that starts nothing asks GitLab nothing");

        await DeliverAsync(world, MergeRequestHook("close", iid: 6, headSha: "head-1")).ConfigureAwait(false);
        await ShouldHaveStartedAsync(world, 0, "a close").ConfigureAwait(false);

        await DeliverAsync(world, MergeRequestHook("reopen", iid: 6, headSha: "head-1")).ConfigureAwait(false);
        await ShouldHaveStartedAsync(world, 1, "the reopen").ConfigureAwait(false);

        await DeliverAsync(world, MergeRequestHook("reopen", iid: 6, headSha: "head-1")).ConfigureAwait(false);
        await ShouldHaveStartedAsync(world, 1, "a second reopen of the same head inside the debounce window").ConfigureAwait(false);

        (await DebouncedRefusalsAsync(world).ConfigureAwait(false)).ShouldBe(2, "one refusal row per activation");
        gitlab.Count(StubEvent.Rotated).ShouldBe(1);
        gitlab.Count(StubEvent.Refused).ShouldBe(0);
    }

    [Fact]
    public async Task S11_a_delivery_whose_standing_lookup_gives_up_mid_refresh_still_leaves_the_pair_stored_and_the_next_delivery_works()
    {
        // The standing lookup has five seconds (PullRequestTriggerAdmission.AuthorLookupTimeout). GitLab rotates the pair at
        // once and answers six seconds later: the lookup's deadline fires while the refresh is still at GitLab. The refresh
        // does not honour the caller's cancellation once it has asked, so the pair that arrives is stored.
        using var gitlab = new GitLabDoorkeeperStub { RotateReplyDelay = TimeSpan.FromSeconds(6) };
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);

        await DeliverAsync(world, MergeRequestHook("reopen", iid: 7, headSha: "head-1")).ConfigureAwait(false);

        gitlab.Count(StubEvent.Rotated).ShouldBe(1);
        (await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, world.Credential.CredentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-2", "GitLab consumed rt-0 while the lookup's deadline fired; the row must hold what it issued instead");

        gitlab.RotateReplyDelay = TimeSpan.Zero;

        await DeliverAsync(world, MergeRequestHook("reopen", iid: 7, headSha: "head-2")).ConfigureAwait(false);

        Dump(gitlab);
        gitlab.Count(StubEvent.Refused).ShouldBe(0, $"the next delivery presented a consumed refresh token: {string.Join(", ", gitlab.RefusedRefreshTokens())}");
        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "the stored pair is live, so the next delivery needs no refresh");
        (await CountRunsAsync(world.TeamId, world.WorkflowId).ConfigureAwait(false)).ShouldBeGreaterThan(0, "the next delivery's standing lookup got an answer, so a members-only activation admitted its author");
    }

    // ─── Deliveries ────────────────────────────────────────────────────────────

    /// <summary>GitLab's Merge Request Hook for MR <paramref name="iid"/>, authored by user 51 on an internal project. No <c>oldrev</c>: the head did not move.</summary>
    private static string MergeRequestHook(string action, int iid, string headSha, string? labels = null)
    {
        var labelJson = labels == null ? string.Empty : "{\"title\":\"" + labels + "\"}";

        return $$$"""
            {"object_kind":"merge_request","user":{"id":7,"username":"maintainer"},
             "project":{"id":15,"path_with_namespace":"acme/api","visibility_level":10},
             "object_attributes":{"id":{{{990 + iid}}},"iid":{{{iid}}},"author_id":51,"source_project_id":15,"target_project_id":15,
               "title":"t","source_branch":"feat","target_branch":"main","action":"{{{action}}}","url":"https://x","last_commit":{"id":"{{{headSha}}}"}},
             "labels":[{{{labelJson}}}]}
            """;
    }

    private async Task DeliverAsync(World world, string body)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<IMediator>().Send(new ReceiveWebhookCommand
        {
            WebhookId = world.WebhookId,
            Body = body,
            Headers = new Dictionary<string, string> { ["X-Gitlab-Event"] = "Merge Request Hook", ["X-Gitlab-Event-UUID"] = Guid.NewGuid().ToString("N"), ["X-Gitlab-Token"] = world.WebhookSecret }
        }).ConfigureAwait(false);
    }

    private void Dump(GitLabDoorkeeperStub gitlab)
    {
        _output.WriteLine($"GitLab saw: rotated={gitlab.Count(StubEvent.Rotated)} refused={gitlab.Count(StubEvent.Refused)} api-200={gitlab.Count(StubEvent.Api200)} api-401={gitlab.Count(StubEvent.Api401)}");

        foreach (var entry in gitlab.Log) _output.WriteLine($"  {entry.At:HH:mm:ss.fff} {entry.Kind}: {entry.Detail}");
    }

    // ─── Reads ─────────────────────────────────────────────────────────────────

    private async Task ShouldHaveStartedAsync(World world, int runsPerActivation, string after)
    {
        (await CountRunsAsync(world.TeamId, world.WorkflowId).ConfigureAwait(false)).ShouldBe(runsPerActivation, $"runs of the first activation after {after}");
        (await CountRunsAsync(world.TeamId, world.SiblingWorkflowId).ConfigureAwait(false)).ShouldBe(runsPerActivation, $"runs of the second activation after {after}");
    }

    private async Task<int> CountRunsAsync(Guid teamId, Guid workflowId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().CountAsync(r => r.TeamId == teamId && r.WorkflowId == workflowId).ConfigureAwait(false);
    }

    private async Task<int> DebouncedRefusalsAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRequest.AsNoTracking()
            .CountAsync(r => r.TeamId == world.TeamId && r.Status == WorkflowRunRequestStatus.Rejected && r.Error!.StartsWith(WorkflowRunRequestRejectionReasons.PullRequestDebounced + ":")).ConfigureAwait(false);
    }

    // ─── Seeds ─────────────────────────────────────────────────────────────────

    private sealed record World(SeededGitLabCredential Credential, Guid WorkflowId, Guid SiblingWorkflowId, Guid WebhookId, string WebhookSecret)
    {
        public Guid TeamId => Credential.TeamId;
    }

    /// <summary>
    /// A team with an internal GitLab project bound through an OAuth credential whose access token has aged out, two
    /// workflows whose pull-request triggers admit only members, and the repository's webhook. GitLab knows the pair the
    /// row holds.
    /// </summary>
    private async Task<World> SeedWorldAsync(GitLabDoorkeeperStub gitlab)
    {
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");

        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture).ConfigureAwait(false);
        var credential = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: true, RepositoryVisibility.Internal, team).ConfigureAwait(false);
        var workflow = await CreateWorkflowAsync(team.TeamId, team.UserId).ConfigureAwait(false);
        var sibling = await CreateWorkflowAsync(team.TeamId, await AddAdminAsync(team.TeamId).ConfigureAwait(false)).ConfigureAwait(false);
        var secret = $"gl-sec-{Guid.NewGuid():N}";

        await SeedMembersOnlyActivationAsync(workflow, team.UserId).ConfigureAwait(false);
        await SeedMembersOnlyActivationAsync(sibling, team.UserId).ConfigureAwait(false);

        return new World(credential, workflow, sibling, await SeedWebhookAsync(credential.RepositoryId, secret).ConfigureAwait(false), secret);
    }

    private async Task<Guid> AddAdminAsync(Guid teamId)
    {
        var userId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.User.Add(new User { Id = userId, Email = $"sibling-{userId:N}@test.local", Name = $"sibling-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Admin, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync().ConfigureAwait(false);

        return userId;
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = "burst-" + Guid.NewGuid().ToString("N")[..8], Description = null, Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = new List<WorkflowActivationInput>(), Enabled = true }).ConfigureAwait(false);
    }

    private async Task SeedMembersOnlyActivationAsync(Guid workflowId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowActivation.Add(new WorkflowActivation { Id = Guid.NewGuid(), WorkflowId = workflowId, TypeKey = Opened, ConfigJson = """{"authors":"members"}""", Enabled = true, CreatedBy = userId, LastModifiedBy = userId });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<Guid> SeedWebhookAsync(Guid repositoryId, string secret)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var webhook = new RepositoryWebhook { Id = Guid.NewGuid(), RepositoryId = repositoryId, ExternalId = $"wh-{Guid.NewGuid():N}"[..16], CallbackUrl = "https://x/cb", SecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt(secret), SubscribedEvents = new List<string> { "merge_requests" } };

        db.RepositoryWebhook.Add(webhook);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return webhook.Id;
    }
}
