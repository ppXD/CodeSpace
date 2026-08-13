using System.Text.Json;
using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// The read path behind the Webhook tab, driven through the mediator so the authorization
/// behaviors are part of what is proven.
///
/// <para>What was wrong before it existed: the only thing said about a repository's webhooks was
/// <c>RepositoryDetail.ActiveWebhooksCount</c>, which counts Registered rows. A repository whose
/// single hook dead-lettered four hours ago reported zero — the same answer as a repository that
/// never had one. Every field these tests read was already in the database and reachable by nothing.</para>
///
/// <para>The secret is the exception, and the reason it is a separate endpoint: it is the value an
/// inbound delivery is authenticated against, so it must be asked for rather than arrive with a
/// tab. Two tests here hold that line — one that the list never carries it, one that taking it
/// needs the capability that manages repositories.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RepositoryWebhookVisibilityTests
{
    private const string StagedSecret = "whsec-tab-9d41c7";

    private readonly PostgresFixture _fixture;

    public RepositoryWebhookVisibilityTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_registered_hook_reads_back_with_everything_the_tab_shows()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(team).ConfigureAwait(false);
        var webhookId = await SeedWebhookAsync(repositoryId, RepositoryWebhookRegistrationStatus.Registered, externalId: "hook-42").ConfigureAwait(false);

        var webhooks = await ListAsync(team.Admin, team.TeamId, repositoryId).ConfigureAwait(false);

        var hook = webhooks.ShouldHaveSingleItem();
        hook.Id.ShouldBe(webhookId);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered);
        hook.Active.ShouldBeTrue();
        hook.ExternalId.ShouldBe("hook-42", customMessage: "the provider's own id is how an operator finds this hook in the provider's UI");
        hook.CallbackUrl.ShouldContain(webhookId.ToString());
        hook.SubscribedEvents.ShouldBe(new[] { "push", "merge_request" });
        hook.LastReceivedDate.ShouldNotBeNull(customMessage: "'registered but never fired' and 'firing normally' are different problems, and only this field separates them");
        hook.AttemptTimeline.ShouldBeEmpty(customMessage: "nothing failed, so there is nothing in the timeline");
    }

    [Fact]
    public async Task A_dead_lettered_hook_carries_its_whole_attempt_timeline()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(team).ConfigureAwait(false);
        var webhookId = await SeedWebhookAsync(repositoryId, RepositoryWebhookRegistrationStatus.DeadLettered, attempts: 3).ConfigureAwait(false);

        // Two unanswered calls then a refusal — the shape the attempt table exists to make legible.
        await SeedAttemptAsync(webhookId, 1, minutesAgo: 30, statusCode: null, error: "The operation was canceled.").ConfigureAwait(false);
        await SeedAttemptAsync(webhookId, 2, minutesAgo: 20, statusCode: null, error: "The operation was canceled.").ConfigureAwait(false);
        await SeedAttemptAsync(webhookId, 3, minutesAgo: 10, statusCode: 403, error: "Git returned HTTP 403 for RegisterWebhookAsync").ConfigureAwait(false);

        var hook = (await ListAsync(team.Admin, team.TeamId, repositoryId).ConfigureAwait(false)).ShouldHaveSingleItem();

        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.DeadLettered);
        hook.Attempts.ShouldBe(3);
        hook.AttemptTimeline.Count.ShouldBe(3, customMessage: "a timeline with holes cannot be read — every attempt has to arrive, not just the last");
        hook.AttemptTimeline.Select(a => a.AttemptNumber).ShouldBe(new[] { 1, 2, 3 }, customMessage: "oldest first: the order IS the story");
        hook.AttemptTimeline.Take(2).ShouldAllBe(a => a.StatusCode == null, customMessage: "a call that never reached HTTP records no status, and that absence is what names it a timeout rather than a refusal");
        hook.AttemptTimeline.Last().StatusCode.ShouldBe(403);
        hook.AttemptTimeline.Last().ResponseBody.ShouldContain("Forbidden", customMessage: "the provider's own words are the diagnosis");
        hook.AttemptTimeline.Last().RequestMethod.ShouldBe("POST");
        hook.AttemptTimeline.Last().RequestUrl.ShouldContain("/hooks");
        hook.AttemptTimeline.Last().RequestHeadersJson.ShouldNotBeNullOrWhiteSpace(customMessage: "which auth scheme was used is half of why a 403 happened");
    }

    [Fact]
    public async Task Another_teams_repository_is_refused()
    {
        var owning = await SeedTeamAsync().ConfigureAwait(false);
        var outsider = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(owning).ConfigureAwait(false);
        await SeedWebhookAsync(repositoryId, RepositoryWebhookRegistrationStatus.Registered).ConfigureAwait(false);

        // An admin of their OWN team, asking about someone else's repository by id.
        var thrown = await Record.ExceptionAsync(() => ListAsync(outsider.Admin, outsider.TeamId, repositoryId)).ConfigureAwait(false);

        thrown.ShouldBeOfType<TenantAccessDeniedException>(
            customMessage: "a webhook carries a callback URL and an event subscription for a repository the caller has no standing on");
    }

    [Fact]
    public async Task The_list_never_carries_the_secret()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(team).ConfigureAwait(false);
        await SeedWebhookAsync(repositoryId, RepositoryWebhookRegistrationStatus.Registered).ConfigureAwait(false);

        var webhooks = await ListAsync(team.Admin, team.TeamId, repositoryId).ConfigureAwait(false);

        // The whole serialized answer, because a field added later would leak through any assertion
        // that only names the fields we know about today.
        JsonSerializer.Serialize(webhooks).ShouldNotContain(StagedSecret,
            customMessage: "opening the tab put the signing secret on the wire. Anyone who can read a response body can now forge a delivery this repository accepts.");
    }

    [Fact]
    public async Task Revealing_the_secret_needs_the_repository_management_permission()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(team).ConfigureAwait(false);
        var webhookId = await SeedWebhookAsync(repositoryId, RepositoryWebhookRegistrationStatus.Registered).ConfigureAwait(false);

        var refused = await Record.ExceptionAsync(() => RevealAsync(team.Member, team.TeamId, repositoryId, webhookId)).ConfigureAwait(false);

        refused.ShouldBeOfType<TenantAccessDeniedException>(
            customMessage: "a plain Member took the value that authenticates every inbound delivery. It is gated at repos.manage — the tier that decides which repositories the team has at all.");

        var revealed = await RevealAsync(team.Admin, team.TeamId, repositoryId, webhookId).ConfigureAwait(false);

        revealed.WebhookId.ShouldBe(webhookId);
        revealed.Secret.ShouldBe(StagedSecret, customMessage: "the point of the endpoint is the plaintext — an operator re-enters it at the provider by hand");
    }

    [Fact]
    public async Task Retrying_a_dead_lettered_hook_puts_it_back_in_the_queue()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(team).ConfigureAwait(false);
        var webhookId = await SeedWebhookAsync(repositoryId, RepositoryWebhookRegistrationStatus.DeadLettered, attempts: 10).ConfigureAwait(false);

        var retried = await RetryAsync(team.Admin, team.TeamId, repositoryId, webhookId).ConfigureAwait(false);

        retried.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Pending,
            customMessage: "DeadLettered is terminal and the reconciler never revives it — the operator's retry is the only way out, and it has to land in the state the dispatcher reads.");
        retried.Attempts.ShouldBe(0,
            customMessage: "the row still sits at MaxAttempts, so the first transient timeout re-buries it and the operator who just fixed the credential is back where they started");

        // The row has moved on by the time we look: the dispatch is deferred to after the command
        // commits, and it ran. Enqueued is the proof that the retry reached the dispatcher rather
        // than only rewriting a column and leaving the hook parked for the reconciler.
        using var verify = _fixture.BeginScope();
        var stored = await verify.Resolve<CodeSpaceDbContext>().RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == webhookId).ConfigureAwait(false);
        stored.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Enqueued);
        stored.NextAttemptAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow, customMessage: "'retry now' means now — a next_attempt_at in the future is the backoff the operator asked to skip");
    }

    [Fact]
    public async Task Retrying_a_hook_that_is_not_failing_is_refused()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(team).ConfigureAwait(false);
        var webhookId = await SeedWebhookAsync(repositoryId, RepositoryWebhookRegistrationStatus.Registered, externalId: "hook-live").ConfigureAwait(false);

        var thrown = await Record.ExceptionAsync(() => RetryAsync(team.Admin, team.TeamId, repositoryId, webhookId)).ConfigureAwait(false);

        thrown.ShouldBeOfType<InvalidOperationException>(
            customMessage: "re-queueing a live Registered hook would re-run the provider call for a hook that is already working");

        using var verify = _fixture.BeginScope();
        var stored = await verify.Resolve<CodeSpaceDbContext>().RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == webhookId).ConfigureAwait(false);
        stored.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered, customMessage: "the refusal must leave the row alone");
    }

    /// <summary>
    /// The one filter standing between an admin and another team's signing secret.
    ///
    /// <para>The pipeline vets the REPOSITORY against the caller's team. Nothing vets the webhook id, so the
    /// service keys the secret load on both — and with only the repository check, an admin of any repository
    /// could name a webhook id belonging to someone else's and be handed the secret that signs their deliveries.
    /// Every other test here passes with that filter deleted, which is why this one exists.</para>
    /// </summary>
    [Fact]
    public async Task A_webhook_id_from_another_repository_is_not_readable_through_one_you_hold()
    {
        var mine = await SeedTeamAsync().ConfigureAwait(false);
        var theirs = await SeedTeamAsync().ConfigureAwait(false);

        var myRepository = await SeedRepositoryAsync(mine).ConfigureAwait(false);
        var theirRepository = await SeedRepositoryAsync(theirs).ConfigureAwait(false);
        var theirWebhook = await SeedWebhookAsync(theirRepository, RepositoryWebhookRegistrationStatus.Registered, externalId: "hook-theirs").ConfigureAwait(false);

        // The caller is a legitimate admin of THEIR OWN repository, and names a webhook that is not on it.
        var thrown = await Record.ExceptionAsync(() => RevealAsync(mine.Admin, mine.TeamId, myRepository, theirWebhook)).ConfigureAwait(false);

        thrown.ShouldBeOfType<KeyNotFoundException>(
            customMessage: "a webhook id is not a capability — holding one repository must not read a secret belonging to another");

        thrown.Message.ShouldNotContain(StagedSecret);
    }

    /// <summary>The same reach, through the list rather than the reveal — a timeline can carry a provider's response body.</summary>
    [Fact]
    public async Task Listing_shows_only_the_webhooks_of_the_repository_asked_for()
    {
        var mine = await SeedTeamAsync().ConfigureAwait(false);
        var theirs = await SeedTeamAsync().ConfigureAwait(false);

        var myRepository = await SeedRepositoryAsync(mine).ConfigureAwait(false);
        var theirRepository = await SeedRepositoryAsync(theirs).ConfigureAwait(false);
        var theirWebhook = await SeedWebhookAsync(theirRepository, RepositoryWebhookRegistrationStatus.Registered, externalId: "hook-theirs").ConfigureAwait(false);

        var listed = await ListAsync(mine.Admin, mine.TeamId, myRepository).ConfigureAwait(false);

        listed.ShouldNotContain(w => w.Id == theirWebhook, customMessage: "the list is scoped to the repository in the route, not to everything the query could reach");
    }

    // ─── Dispatch ───────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<Messages.Dtos.Repositories.RepositoryWebhookDetail>> ListAsync(Guid userId, Guid teamId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        return await scope.Resolve<IMediator>().Send(new ListRepositoryWebhooksQuery { RepositoryId = repositoryId }).ConfigureAwait(false);
    }

    private async Task<Messages.Dtos.Repositories.RepositoryWebhookSecret> RevealAsync(Guid userId, Guid teamId, Guid repositoryId, Guid webhookId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        return await scope.Resolve<IMediator>().Send(new RevealRepositoryWebhookSecretCommand { RepositoryId = repositoryId, WebhookId = webhookId }).ConfigureAwait(false);
    }

    private async Task<Messages.Dtos.Repositories.RepositoryWebhookDetail> RetryAsync(Guid userId, Guid teamId, Guid repositoryId, Guid webhookId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        return await scope.Resolve<IMediator>().Send(new RetryRepositoryWebhookRegistrationCommand { RepositoryId = repositoryId, WebhookId = webhookId }).ConfigureAwait(false);
    }

    // ─── Seeding ────────────────────────────────────────────────────────────────

    /// <summary>A team with one Admin (holds repos.manage) and one Member (does not) — the two sides of the reveal gate.</summary>
    private async Task<SeededTeam> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var admin = new User { Id = Guid.NewGuid(), Email = $"adm-{suffix}@x", Name = "admin" };
        var member = new User { Id = Guid.NewGuid(), Email = $"mem-{suffix}@x", Name = "member" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"hooks-{suffix}", Name = "Hooks" };

        db.User.AddRange(admin, member);
        db.Team.Add(team);
        db.Project.Add(TestProjectSeed.BuildDefaultProject(team.Id, admin.Id));
        db.TeamMembership.AddRange(
            new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = admin.Id, Role = TeamRole.Admin },
            new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = member.Id, Role = TeamRole.Member });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SeededTeam(team.Id, admin.Id, member.Id);
    }

    private async Task<Guid> SeedRepositoryAsync(SeededTeam team)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.TeamId, Provider = ProviderKind.Git, DisplayName = "Test", BaseUrl = "https://test.local" };
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.TeamId,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.Pat,
            DisplayName = "PAT",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "pat-xxx" }))
        };
        var repositoryId = Guid.NewGuid();

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(new Repository
        {
            Id = repositoryId,
            TeamId = team.TeamId,
            ProviderInstanceId = instance.Id,
            CredentialId = credential.Id,
            ExternalId = $"id-tab-{suffix}",
            NamespacePath = "acme",
            Name = $"tab-{suffix}",
            FullPath = $"acme/tab-{suffix}",
            DefaultBranch = "main",
            Visibility = RepositoryVisibility.Private,
            WebUrl = "https://test.local",
            Status = RepositoryStatus.Active
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return repositoryId;
    }

    private async Task<Guid> SeedWebhookAsync(Guid repositoryId, RepositoryWebhookRegistrationStatus status, int attempts = 0, string? externalId = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var webhookId = Guid.NewGuid();

        db.RepositoryWebhook.Add(new RepositoryWebhook
        {
            Id = webhookId,
            RepositoryId = repositoryId,
            ExternalId = externalId,
            CallbackUrl = $"https://test.local/api/webhooks/{webhookId}",
            SecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt(StagedSecret),
            SubscribedEvents = new List<string> { "push", "merge_request" },
            Active = true,
            RegistrationStatus = status,
            Attempts = attempts,
            LastReceivedDate = DateTimeOffset.UtcNow.AddMinutes(-5),
            NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1)
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return webhookId;
    }

    private async Task SeedAttemptAsync(Guid webhookId, int attemptNumber, int minutesAgo, int? statusCode, string error)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.RepositoryWebhookAttempt.Add(new RepositoryWebhookAttempt
        {
            Id = Guid.NewGuid(),
            RepositoryWebhookId = webhookId,
            AttemptNumber = attemptNumber,
            AttemptedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            Error = error,
            StatusCode = statusCode,
            ResponseBody = statusCode == null ? null : """{"message":"403 Forbidden - Insufficient permissions"}""",
            RequestMethod = statusCode == null ? null : "POST",
            RequestUrl = statusCode == null ? null : "https://test.local/api/v4/projects/1/hooks",
            RequestBody = statusCode == null ? null : """{"url":"https://test.local/api/webhooks/x","token":"***"}""",
            RequestHeadersJson = statusCode == null ? null : """{"PRIVATE-TOKEN":"***"}"""
        });

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private sealed record SeededTeam(Guid TeamId, Guid Admin, Guid Member);
}
