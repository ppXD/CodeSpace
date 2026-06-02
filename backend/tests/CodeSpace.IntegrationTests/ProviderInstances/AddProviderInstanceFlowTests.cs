using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.ProviderInstances;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.ProviderInstances;

/// <summary>
/// AddProviderInstanceCommand end-to-end. Partial unique index
/// <c>idx_provider_instance_team_provider_url_active</c> covers
/// (team_id, provider, base_url) WHERE deleted_date IS NULL. Tests confirm:
///   • The happy path returns the new id.
///   • A second add with same (provider, baseUrl) under the same team is rejected with a
///     clear message — not the cryptic 23505 wrapped as 500.
///   • The check is base_url-NORMALISED (trailing slash collapsed).
///   • Soft-deleted instances don't block reuse (the partial index ignores them).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AddProviderInstanceFlowTests
{
    private readonly PostgresFixture _fixture;

    public AddProviderInstanceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Add_creates_provider_instance_and_returns_id()
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var id = await mediator.Send(new AddProviderInstanceCommand
        {
            Provider = ProviderKind.GitHub,
            DisplayName = "GitHub",
            BaseUrl = "https://github.com",
            OauthClientId = "abc",
            OauthClientSecret = "def",
            OauthDefaultScopes = new[] { "repo", "read:user" }
        }).ConfigureAwait(false);

        id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Add_with_duplicate_team_provider_base_url_same_client_id_throws_clear_error()
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var command = new AddProviderInstanceCommand
        {
            Provider = ProviderKind.GitHub,
            DisplayName = "GitHub",
            BaseUrl = "https://github.com",
            OauthClientId = "same-client",
            OauthClientSecret = "secret"
        };

        await mediator.Send(command).ConfigureAwait(false);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => mediator.Send(command)).ConfigureAwait(false);

        // Same (team, provider, base_url, oauth_client_id) tuple → duplicate. Message must
        // name the client_id so the operator can see which existing row collides.
        ex.Message.ShouldContain("GitHub");
        ex.Message.ShouldContain("github.com");
        ex.Message.ShouldContain("same-client");
        ex.Message.ShouldContain("already exists");
    }

    [Fact]
    public async Task Add_two_oauth_apps_at_same_host_with_different_client_ids_succeeds()
    {
        // The intentional multi-app pattern: one team registering "Admin GitHub" (broader
        // scopes) and "Read-only GitHub" (narrower) for the same host. Different OAuth
        // apps → different client_ids → both rows coexist under one (team, provider, host).
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var firstId = await mediator.Send(new AddProviderInstanceCommand
        {
            Provider = ProviderKind.GitHub,
            DisplayName = "Admin GitHub",
            BaseUrl = "https://github.com",
            OauthClientId = "admin-app",
            OauthClientSecret = "admin-secret",
            OauthDefaultScopes = new[] { "repo", "admin:org" }
        }).ConfigureAwait(false);

        var secondId = await mediator.Send(new AddProviderInstanceCommand
        {
            Provider = ProviderKind.GitHub,
            DisplayName = "Read-only GitHub",
            BaseUrl = "https://github.com",
            OauthClientId = "readonly-app",
            OauthClientSecret = "readonly-secret",
            OauthDefaultScopes = new[] { "public_repo", "read:user" }
        }).ConfigureAwait(false);

        firstId.ShouldNotBe(Guid.Empty);
        secondId.ShouldNotBe(Guid.Empty);
        firstId.ShouldNotBe(secondId);
    }

    [Theory]
    [InlineData(null, "some-secret")]     // secret without client_id
    [InlineData("some-client", null)]     // client_id without secret
    [InlineData("   ", "some-secret")]    // whitespace-only client_id collapses to null → still mismatched
    public async Task Add_with_exactly_one_oauth_credential_is_rejected(string? clientId, string? clientSecret)
    {
        // OAuth client ID + secret are all-or-nothing. Exactly one is ambiguous (a client_id with no
        // secret can't exchange a code; a secret with no id is meaningless), so we reject it. Both-blank
        // is allowed — see Add_token_only_creates_instance_with_no_oauth. Enforced backend-side so
        // non-UI callers can't sidestep the frontend either way.
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => mediator.Send(new AddProviderInstanceCommand
            {
                Provider = ProviderKind.GitHub,
                DisplayName = "GitHub",
                BaseUrl = "https://github.com",
                OauthClientId = clientId,
                OauthClientSecret = clientSecret
            })).ConfigureAwait(false);

        ex.Message.ShouldContain("OAuth client ID");
        ex.Message.ShouldContain("token-only");
    }

    [Fact]
    public async Task Add_token_only_creates_instance_with_no_oauth()
    {
        // Both OAuth fields blank → a token-only provider (members connect with a personal access
        // token; no OAuth app). The instance persists with no client id/secret → oauthEnabled=false,
        // and Connect-remote → Personal offers "Use a token".
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        Guid id;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            id = await scope.Resolve<IMediator>().Send(new AddProviderInstanceCommand
            {
                Provider = ProviderKind.GitLab,
                DisplayName = "Token-only GitLab",
                BaseUrl = "https://gitlab.token-only.test"
            }).ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScope();
        var instance = await verify.Resolve<CodeSpaceDbContext>().ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == id).ConfigureAwait(false);
        instance.OauthClientId.ShouldBeNull("token-only provider stores no OAuth client id");
        instance.OauthClientSecretEnc.ShouldBeNull();
    }

    [Theory]
    [InlineData("https://github.com/", "https://github.com")]
    [InlineData("https://github.com",  "https://github.com/")]
    public async Task Add_treats_trailing_slash_variants_as_duplicates(string firstBaseUrl, string secondBaseUrl)
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await mediator.Send(new AddProviderInstanceCommand
        {
            Provider = ProviderKind.GitHub,
            DisplayName = "GitHub",
            BaseUrl = firstBaseUrl,
            OauthClientId = "client",
            OauthClientSecret = "secret"
        }).ConfigureAwait(false);

        await Should.ThrowAsync<InvalidOperationException>(
            () => mediator.Send(new AddProviderInstanceCommand
            {
                Provider = ProviderKind.GitHub,
                DisplayName = "GitHub 2",
                BaseUrl = secondBaseUrl,
                OauthClientId = "client",                // same client_id → still triggers duplicate
                OauthClientSecret = "secret"
            })).ConfigureAwait(false);
    }

    [Fact]
    public async Task Add_after_soft_delete_succeeds()
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        Guid firstId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            firstId = await mediator.Send(new AddProviderInstanceCommand
            {
                Provider = ProviderKind.GitHub,
                DisplayName = "GitHub",
                BaseUrl = "https://github.com",
                OauthClientId = "client-v1",
                OauthClientSecret = "secret-v1"
            }).ConfigureAwait(false);
        }

        // Tombstone the first instance directly. Production would do this through a
        // dedicated soft-delete command; the partial unique index is what we care about.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var existing = await db.ProviderInstance.SingleAsync(p => p.Id == firstId).ConfigureAwait(false);
            existing.DeletedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            // Same client_id is fine because the first row is tombstoned (partial index
            // ignores deleted_date IS NOT NULL).
            var secondId = await mediator.Send(new AddProviderInstanceCommand
            {
                Provider = ProviderKind.GitHub,
                DisplayName = "GitHub take 2",
                BaseUrl = "https://github.com",
                OauthClientId = "client-v1",
                OauthClientSecret = "secret-v1"
            }).ConfigureAwait(false);

            secondId.ShouldNotBe(firstId);
        }
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team", OwnerUserId = owner.Id };

        db.User.Add(owner);
        db.Team.Add(team);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return team.Id;
    }
}
