using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// The Webhook tab's read under connection-wide scope. Under that mode a repository has NO hook of
/// its own, so the tab's list is legitimately empty — and an empty tab is indistinguishable from a
/// repository nothing is registered for, which is exactly the invisibility the tab exists to end.
/// This is the read that lets it say which hook covers the repository and what that hook is doing.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ConnectionWebhookCoverageTests
{
    private readonly PostgresFixture _fixture;

    public ConnectionWebhookCoverageTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_repository_under_a_group_hook_is_told_which_hook_covers_it()
    {
        var seed = await SeedAsync("acme", RepositoryWebhookRegistrationStatus.Registered, withFailedAttempt: true).ConfigureAwait(false);

        var coverage = await ReadAsync(seed.RepositoryId).ConfigureAwait(false);

        coverage.Scope.ShouldBe(ProviderWebhookScope.Connection);
        coverage.OwnerPath.ShouldBe("acme",
            customMessage: "The tab has to NAME the hook — 'covered by something elsewhere' sends the reader hunting.");
        coverage.Hook.ShouldNotBeNull(customMessage: "A registered group hook covering this repository must come back, or the tab is blank for a repository that is working.");
        coverage.Hook!.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered);

        // The timeline is the other half: an operator asking why nothing arrives needs the attempts,
        // in the same shape the repository hook's diagnosis card already reads.
        coverage.Hook.AttemptTimeline.Count.ShouldBe(1);
        coverage.Hook.AttemptTimeline[0].StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task A_repository_in_a_subgroup_is_covered_by_the_hook_on_its_ancestor()
    {
        // The read has to follow the same ancestor rule the registration does, or the page shows one
        // hook while a different one is delivering.
        var seed = await SeedAsync("acme", RepositoryWebhookRegistrationStatus.Registered, namespacePath: "acme/platform/web").ConfigureAwait(false);

        var coverage = await ReadAsync(seed.RepositoryId).ConfigureAwait(false);

        coverage.OwnerPath.ShouldBe("acme");
        coverage.Hook.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_retired_hook_is_not_reported_as_covering_anything()
    {
        // Cancelled is evidence of what was tried, not a claim that events arrive. Reporting it as
        // coverage would be the page's worst lie — the one state where nothing is delivering at all.
        var seed = await SeedAsync("acme", RepositoryWebhookRegistrationStatus.Cancelled).ConfigureAwait(false);

        var coverage = await ReadAsync(seed.RepositoryId).ConfigureAwait(false);

        coverage.Scope.ShouldBe(ProviderWebhookScope.Connection);
        coverage.Hook.ShouldBeNull();
        coverage.OwnerPath.ShouldBeNull();
    }

    [Fact]
    public async Task A_per_repository_connection_says_so_and_adds_nothing()
    {
        // The mode every existing connection is in. The list of its own hooks is the whole answer,
        // and this read must not invent a second one beside it.
        var seed = await SeedAsync("acme", RepositoryWebhookRegistrationStatus.Registered, scope: ProviderWebhookScope.Repository).ConfigureAwait(false);

        var coverage = await ReadAsync(seed.RepositoryId).ConfigureAwait(false);

        coverage.Scope.ShouldBe(ProviderWebhookScope.Repository);
        coverage.Hook.ShouldBeNull();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Messages.Dtos.Repositories.RepositoryWebhookCoverage> ReadAsync(Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IMediator>().Send(new GetRepositoryWebhookCoverageQuery { RepositoryId = repositoryId }).ConfigureAwait(false);
    }

    private async Task<Seed> SeedAsync(string ownerPath, RepositoryWebhookRegistrationStatus status, string namespacePath = "acme", bool withFailedAttempt = false, ProviderWebhookScope scope = ProviderWebhookScope.Connection)
    {
        using var lifetime = _fixture.BeginScope();
        var db = lifetime.Resolve<CodeSpaceDbContext>();
        var encryptor = lifetime.Resolve<IPayloadEncryptor>();
        var serializer = lifetime.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitLab, DisplayName = "Conn", BaseUrl = $"https://cov-{suffix}.local", WebhookScope = scope };

        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.Pat,
            DisplayName = "PAT",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "pat-xxx" }))
        };

        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            CredentialId = credential.Id,
            ExternalId = $"ext-{suffix}",
            NamespacePath = namespacePath,
            Name = $"api-{suffix}",
            FullPath = $"{namespacePath}/api-{suffix}",
            DefaultBranch = "main",
            Visibility = RepositoryVisibility.Private,
            WebUrl = "https://cov.local/x",
            Status = RepositoryStatus.Active
        };

        var hookId = Guid.NewGuid();

        db.User.Add(owner);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Owner });
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repository);
        db.ConnectionWebhook.Add(new ConnectionWebhook
        {
            Id = hookId,
            ProviderInstanceId = instance.Id,
            CredentialId = credential.Id,
            OwnerPath = ownerPath,
            ExternalId = "remote-group-hook",
            CallbackUrl = $"https://codespace.test/api/webhooks/connection/{hookId}",
            SecretEnc = encryptor.Encrypt("conn-secret"),
            SubscribedEvents = new List<string> { "push" },
            RegistrationStatus = status
        });

        if (withFailedAttempt)
        {
            db.ConnectionWebhookAttempt.Add(new ConnectionWebhookAttempt
            {
                Id = Guid.NewGuid(),
                ConnectionWebhookId = hookId,
                AttemptNumber = 1,
                AttemptedAt = DateTimeOffset.UtcNow,
                Error = "403 Forbidden",
                StatusCode = 403,
                ResponseBody = """{"message":"403 Forbidden - Group hooks are available in GitLab Premium"}""",
                RequestMethod = "POST",
                RequestUrl = "https://gitlab.test/api/v4/groups/acme/hooks"
            });
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Seed { RepositoryId = repository.Id };
    }

    private sealed record Seed
    {
        public required Guid RepositoryId { get; init; }
    }
}
