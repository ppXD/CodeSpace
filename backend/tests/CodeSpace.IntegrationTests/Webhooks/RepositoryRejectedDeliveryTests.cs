using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Webhooks;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// The deliveries that arrived and were refused — both halves of making them visible.
///
/// <para>What was wrong before: every rejection was already audited, with the reason, the delivery
/// id, the redacted headers and the verifier's diagnostic — and no repository. So the rows existed
/// and the operator, who is standing on a repository page, could not reach a single one of them.
/// The first half of these tests is that every rejection path now names the repository it was for;
/// the second is the read that repository-scoped question finally has.</para>
///
/// <para>Driven through a GitLab provider instance on purpose. The test provider's verifier always
/// passes and its normaliser always returns null, so it can only ever reach one of the four
/// ingestion branches; the real <c>GitLabSignatureVerifier</c> and <c>GitLabEventNormalizer</c>
/// reach all four from a plain HTTP delivery, which is what the operator's are.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RepositoryRejectedDeliveryTests
{
    private const string StagedSecret = "whsec-rejected-4b17c9";

    /// <summary>A push body with every field <c>GitLabPushEventSubscription</c> reads, so the only thing under test is what happens AFTER it normalises.</summary>
    private const string ValidPushBody = """
        {"ref":"refs/heads/main","before":"0000000000000000000000000000000000000000","after":"a1b2c3d4e5f60718293a4b5c6d7e8f9012345678","user_id":417,"user_name":"Mars P","commits":[{"id":"a1b2c3d4e5f60718293a4b5c6d7e8f9012345678","message":"Fix the thing","author":{"email":"mars@test.local","name":"Mars P"}}]}
        """;

    private readonly PostgresFixture _fixture;

    public RepositoryRejectedDeliveryTests(PostgresFixture fixture) { _fixture = fixture; }

    // ─── Every rejection path names the repository it was for ───────────────────

    [Fact]
    public async Task A_delivery_whose_signature_does_not_match_names_the_repository()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(team).ConfigureAwait(false);
        var webhookId = await SeedWebhookAsync(repositoryId).ConfigureAwait(false);

        // No X-Gitlab-Token at all — the shape of a forged delivery, and of a hook whose secret at
        // GitLab was never the one here.
        await Should.ThrowAsync<UnauthorizedAccessException>(() => IngestAsync(webhookId, "{}", Headers(token: null, gitlabEvent: "Push Hook"))).ConfigureAwait(false);

        var refusal = await SingleRefusalAsync(team.TeamId).ConfigureAwait(false);

        refusal.RepositoryId.ShouldBe(repositoryId,
            customMessage: "a signature failure is the one refusal that means something is BROKEN, and without the repository on the row the operator standing on that repository never sees it");
        refusal.Error.ShouldStartWith(WorkflowRunRequestRejectionReasons.SignatureInvalid);
    }

    [Fact]
    public async Task A_delivery_to_a_hook_that_is_switched_off_names_the_repository()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(team).ConfigureAwait(false);
        var webhookId = await SeedWebhookAsync(repositoryId, active: false).ConfigureAwait(false);

        await Should.ThrowAsync<InvalidOperationException>(() => IngestAsync(webhookId, "{}", Headers())).ConfigureAwait(false);

        var refusal = await SingleRefusalAsync(team.TeamId).ConfigureAwait(false);

        refusal.RepositoryId.ShouldBe(repositoryId,
            customMessage: "this rejection precedes signature verification, so it is the branch most likely to be left unattributed — and it is the one that says GitLab is still sending into a hook nobody turned back on");
        refusal.Error.ShouldStartWith(WorkflowRunRequestRejectionReasons.WebhookInactive);
    }

    [Fact]
    public async Task A_body_that_is_not_the_promised_shape_names_the_repository()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(team).ConfigureAwait(false);
        var webhookId = await SeedWebhookAsync(repositoryId).ConfigureAwait(false);

        // Signature passes; the push normaliser then reaches for "commits" and finds nothing.
        await IngestAsync(webhookId, "{}", Headers(gitlabEvent: "Push Hook")).ConfigureAwait(false);

        var refusal = await SingleRefusalAsync(team.TeamId).ConfigureAwait(false);

        refusal.RepositoryId.ShouldBe(repositoryId);
        refusal.Error.ShouldStartWith(WorkflowRunRequestRejectionReasons.MalformedPayload);
    }

    [Fact]
    public async Task An_event_type_nothing_acts_on_names_the_repository()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(team).ConfigureAwait(false);
        var webhookId = await SeedWebhookAsync(repositoryId).ConfigureAwait(false);

        await IngestAsync(webhookId, "{}", Headers(gitlabEvent: "Deployment Hook")).ConfigureAwait(false);

        var refusal = await SingleRefusalAsync(team.TeamId).ConfigureAwait(false);

        refusal.RepositoryId.ShouldBe(repositoryId);
        refusal.Error.ShouldStartWith(WorkflowRunRequestRejectionReasons.EventNotMapped);
    }

    /// <summary>
    /// The whole way through: a real, correctly signed, correctly shaped GitLab push that nothing
    /// subscribes to. This is the rejection that is NOT a fault, and it comes from a different
    /// writer than the four above — the dispatcher's, which takes its repository from the normalised
    /// event rather than from a webhook row.
    /// </summary>
    [Fact]
    public async Task A_push_nothing_is_listening_for_names_the_repository()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(team).ConfigureAwait(false);
        var webhookId = await SeedWebhookAsync(repositoryId).ConfigureAwait(false);

        await IngestAsync(webhookId, ValidPushBody, Headers(gitlabEvent: "Push Hook")).ConfigureAwait(false);

        var refusal = await SingleRefusalAsync(team.TeamId).ConfigureAwait(false);

        refusal.RepositoryId.ShouldBe(repositoryId,
            customMessage: "the delivery was verified, read, and understood — leaving it unattributed hides the ONE refusal that proves the hook is working");
        refusal.Error.ShouldStartWith(WorkflowRunRequestRejectionReasons.NoMatchingActivation);
    }

    // ─── The read the repository page finally has ───────────────────────────────

    [Fact]
    public async Task Refusals_come_back_newest_first_with_the_reason_split_off_the_error()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(team).ConfigureAwait(false);

        await SeedRefusalAsync(team.TeamId, repositoryId, WorkflowRunRequestRejectionReasons.EventNotMapped, "normalizer returned null", minutesAgo: 30).ConfigureAwait(false);
        await SeedRefusalAsync(team.TeamId, repositoryId, WorkflowRunRequestRejectionReasons.SignatureInvalid, "signature did not validate", minutesAgo: 5,
            verificationResultJson: """{"validated":false,"verifier_class":"GitLabRepositoryProvider"}""").ConfigureAwait(false);

        var answer = await ListAsync(team.Admin, team.TeamId, repositoryId).ConfigureAwait(false);

        answer.Deliveries.Count.ShouldBe(2);
        answer.Deliveries[0].Reason.ShouldBe(WorkflowRunRequestRejectionReasons.SignatureInvalid,
            customMessage: "newest first — the refusal being asked about is the one that just happened");
        answer.Deliveries[0].Detail.ShouldBe("signature did not validate",
            customMessage: "the reason belongs in its own field: the tab picks the operator's sentence from it, and a detail that still carried the prefix would put an identifier in front of a person");
        answer.Deliveries[0].ExternalEventId.ShouldNotBeNullOrEmpty(customMessage: "the provider's delivery id is how this refusal is matched against the delivery in GitLab's own UI");
        answer.Deliveries[0].RawHeadersRedactedJson.ShouldNotBeNullOrEmpty();
        answer.Deliveries[0].VerificationResultJson.ShouldContain("verifier_class",
            customMessage: "the verifier diagnostic is the whole reason a signature failure is actionable rather than a shrug");
        answer.Deliveries[1].Reason.ShouldBe(WorkflowRunRequestRejectionReasons.EventNotMapped);
    }

    [Fact]
    public async Task Another_repositorys_refusals_are_not_in_the_answer()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var mine = await SeedRepositoryAsync(team).ConfigureAwait(false);
        var sibling = await SeedRepositoryAsync(team).ConfigureAwait(false);

        await SeedRefusalAsync(team.TeamId, sibling, WorkflowRunRequestRejectionReasons.SignatureInvalid, "not mine", minutesAgo: 1).ConfigureAwait(false);

        var answer = await ListAsync(team.Admin, team.TeamId, mine).ConfigureAwait(false);

        answer.Deliveries.ShouldBeEmpty(
            customMessage: "a team with forty repositories would get forty repositories' refusals on every one of their pages — the scoping IS the feature");
    }

    /// <summary>
    /// A refusal that never resolved a repository is still a delivery that arrived and was thrown
    /// away, so it stays in the answer and the tab says it could not be placed. Which makes the team
    /// filter load-bearing rather than decorative: it is the only thing keeping one team's
    /// unplaceable refusals off another team's page.
    /// </summary>
    [Fact]
    public async Task A_refusal_that_names_no_repository_is_shown_but_only_to_its_own_team()
    {
        var mine = await SeedTeamAsync().ConfigureAwait(false);
        var theirs = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(mine).ConfigureAwait(false);

        await SeedRefusalAsync(mine.TeamId, repositoryId: null, WorkflowRunRequestRejectionReasons.MalformedPayload, "could not be placed", minutesAgo: 2).ConfigureAwait(false);
        await SeedRefusalAsync(theirs.TeamId, repositoryId: null, WorkflowRunRequestRejectionReasons.MalformedPayload, "someone else's", minutesAgo: 1).ConfigureAwait(false);

        var answer = await ListAsync(mine.Admin, mine.TeamId, repositoryId).ConfigureAwait(false);

        var unattributed = answer.Deliveries.ShouldHaveSingleItem();
        unattributed.RepositoryId.ShouldBeNull(customMessage: "the row is carried through unplaced rather than invented into a repository it was never known to be for");
        unattributed.Detail.ShouldBe("could not be placed");
    }

    [Fact]
    public async Task Another_teams_repository_is_refused()
    {
        var owning = await SeedTeamAsync().ConfigureAwait(false);
        var outsider = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(owning).ConfigureAwait(false);

        await SeedRefusalAsync(owning.TeamId, repositoryId, WorkflowRunRequestRejectionReasons.SignatureInvalid, "theirs", minutesAgo: 1).ConfigureAwait(false);

        var thrown = await Record.ExceptionAsync(() => ListAsync(outsider.Admin, outsider.TeamId, repositoryId)).ConfigureAwait(false);

        thrown.ShouldBeOfType<TenantAccessDeniedException>(
            customMessage: "a refusal carries the provider's delivery id and the headers of a request sent to a repository the caller has no standing on");
    }

    /// <summary>
    /// An unreachable instance retries on a ladder and writes thousands of these in an afternoon.
    /// The answer stops at the cap and carries the cap, so the page can say "the most recent N"
    /// instead of letting a truncated list read as the whole count.
    /// </summary>
    [Fact]
    public async Task The_answer_stops_at_the_cap_and_carries_it()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(team).ConfigureAwait(false);

        var overflow = RejectedDeliveryReader.MaxDeliveries + 5;
        for (var i = 0; i < overflow; i++)
            await SeedRefusalAsync(team.TeamId, repositoryId, WorkflowRunRequestRejectionReasons.SignatureInvalid, $"delivery {i}", minutesAgo: overflow - i).ConfigureAwait(false);

        var answer = await ListAsync(team.Admin, team.TeamId, repositoryId).ConfigureAwait(false);

        answer.Cap.ShouldBe(RejectedDeliveryReader.MaxDeliveries);
        answer.Deliveries.Count.ShouldBe(RejectedDeliveryReader.MaxDeliveries,
            customMessage: $"{overflow} refusals were staged and the read handed back all of them — an unreachable instance would put thousands of rows into the browser");
        answer.Deliveries[0].Detail.ShouldBe($"delivery {overflow - 1}",
            customMessage: "the cap has to keep the NEWEST rows: an operator who is still being refused right now needs the refusal from a minute ago, not the one from this morning");
    }

    // ─── Dispatch ───────────────────────────────────────────────────────────────

    private async Task IngestAsync(Guid webhookId, string body, IReadOnlyDictionary<string, string> headers)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWebhookIngestionService>().IngestAsync(webhookId, body, headers, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<RepositoryRejectedDeliveries> ListAsync(Guid userId, Guid teamId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        return await scope.Resolve<IMediator>().Send(new ListRepositoryRejectedDeliveriesQuery { RepositoryId = repositoryId }).ConfigureAwait(false);
    }

    private async Task<WorkflowRunRequest> SingleRefusalAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRequest.AsNoTracking()
            .SingleAsync(r => r.TeamId == teamId && r.Status == WorkflowRunRequestStatus.Rejected).ConfigureAwait(false);
    }

    /// <summary>A delivery as GitLab sends one. `token` defaults to the staged secret so the signature passes; pass null for the forged case.</summary>
    private static Dictionary<string, string> Headers(string? token = StagedSecret, string gitlabEvent = "Push Hook")
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Gitlab-Event"] = gitlabEvent,
            ["X-Gitlab-Event-UUID"] = Guid.NewGuid().ToString("N"),
            ["Content-Type"] = "application/json",
        };

        if (token != null) headers["X-Gitlab-Token"] = token;

        return headers;
    }

    // ─── Seeding ────────────────────────────────────────────────────────────────

    private async Task<SeededTeam> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var admin = new User { Id = Guid.NewGuid(), Email = $"rej-adm-{suffix}@x", Name = "admin" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"rej-{suffix}", Name = "Rejected" };

        db.User.Add(admin);
        db.Team.Add(team);
        db.Project.Add(TestProjectSeed.BuildDefaultProject(team.Id, admin.Id));
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = admin.Id, Role = TeamRole.Admin });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SeededTeam(team.Id, admin.Id);
    }

    /// <summary>One Admin is enough here: the refusal list is readable by anyone who can open the tab, so there is no permission boundary inside the team to stand on both sides of.</summary>
    private sealed record SeededTeam(Guid TeamId, Guid Admin);

    private async Task<Guid> SeedRepositoryAsync(SeededTeam team)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        // Own instance per repository, on its own host: (team, provider, url) is uniquely indexed, and
        // one test here seeds two repositories in one team to prove they do not see each other's refusals.
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.TeamId, Provider = ProviderKind.GitLab, DisplayName = "GitLab", BaseUrl = $"https://gitlab-{suffix}.test" };
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.TeamId,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.Pat,
            DisplayName = "PAT",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "pat-xxx" })),
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
            ExternalId = $"id-rej-{suffix}",
            NamespacePath = "acme",
            Name = $"rej-{suffix}",
            FullPath = $"acme/rej-{suffix}",
            DefaultBranch = "main",
            Visibility = RepositoryVisibility.Private,
            WebUrl = "https://gitlab.test",
            Status = RepositoryStatus.Active,
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return repositoryId;
    }

    private async Task<Guid> SeedWebhookAsync(Guid repositoryId, bool active = true)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var webhookId = Guid.NewGuid();

        db.RepositoryWebhook.Add(new RepositoryWebhook
        {
            Id = webhookId,
            RepositoryId = repositoryId,
            ExternalId = $"hook-{Guid.NewGuid().ToString("N")[..6]}",
            CallbackUrl = $"https://codespace.test/api/webhooks/{webhookId}",
            SecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt(StagedSecret),
            SubscribedEvents = new List<string> { "push", "merge_request" },
            RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered,
            Active = active,
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return webhookId;
    }

    /// <summary>
    /// A refusal staged straight into the journal — the read path's fixture. Written in the auditor's
    /// own shape (<c>"{reason}: {detail}"</c>) so a change to that shape breaks these tests rather
    /// than silently reshaping what the tab reads.
    /// </summary>
    private async Task SeedRefusalAsync(Guid teamId, Guid? repositoryId, string reason, string detail, int minutesAgo, string? verificationResultJson = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            RepositoryId = repositoryId,
            SourceType = $"{WorkflowRunSourceTypes.ProviderPrefix}gitlab",
            ExternalEventId = Guid.NewGuid().ToString("N"),
            ActorType = WorkflowRunActorTypes.Webhook,
            Status = WorkflowRunRequestStatus.Rejected,
            Error = $"{reason}: {detail}",
            RawHeadersRedactedJson = """{"X-Gitlab-Event":"Push Hook","X-Gitlab-Token":"[REDACTED]"}""",
            VerificationResultJson = verificationResultJson,
            ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        });

        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
