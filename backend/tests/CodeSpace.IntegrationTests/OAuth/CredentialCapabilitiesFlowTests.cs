using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// Pins the capability-probe semantics that drive the Connect-remote "Missing access" chips:
///   • Scopes == null (we never captured this token's scopes) → treat as UNKNOWN, surface nothing.
///   • A sufficiently-scoped token → every capability available.
///   • A known-but-insufficient token → the unsatisfied capabilities correctly warn.
/// Regression guard for the PAT-link bug where scopes were never captured, so a fully-scoped
/// token showed every capability as "missing".
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CredentialCapabilitiesFlowTests
{
    private readonly PostgresFixture _fixture;

    public CredentialCapabilitiesFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(null)]    // scopes unknown (e.g. PAT linked before scope-capture) → never a false warning
    [InlineData("api")]   // fully-scoped GitLab token → everything satisfied
    public async Task Capabilities_show_no_warnings_when_scopes_unknown_or_sufficient(string? singleScope)
    {
        var scopes = singleScope == null ? null : new List<string> { singleScope };
        var (userId, teamId, credId) = await SeedGitLabPatAsync(scopes).ConfigureAwait(false);

        var caps = await GetCapabilitiesAsync(userId, teamId, credId).ConfigureAwait(false);

        caps.Capabilities.ShouldNotBeEmpty();
        caps.Capabilities.ShouldAllBe(c => c.IsAvailable, "an unknown (null) or sufficiently-scoped token must not surface any missing-capability warning");
    }

    [Fact]
    public async Task Capabilities_flag_missing_when_scopes_known_but_insufficient()
    {
        // read_repository alone can't satisfy GitLab's api/read_api capabilities — those MUST warn,
        // proving the null-safe path doesn't blanket-allow a genuinely under-scoped token.
        var (userId, teamId, credId) = await SeedGitLabPatAsync(new List<string> { "read_repository" }).ConfigureAwait(false);

        var caps = await GetCapabilitiesAsync(userId, teamId, credId).ConfigureAwait(false);

        var repoCatalog = caps.Capabilities.Single(c => c.Capability == nameof(IRepositoryCatalogCapability));
        repoCatalog.IsAvailable.ShouldBeFalse();
        repoCatalog.MissingScopes.ShouldNotBeEmpty();
    }

    private async Task<CredentialCapabilitiesResponse> GetCapabilitiesAsync(Guid userId, Guid teamId, Guid credId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<ICredentialService>().GetCapabilitiesAsync(credId, default).ConfigureAwait(false);
    }

    private async Task<(Guid UserId, Guid TeamId, Guid CredId)> SeedGitLabPatAsync(List<string>? scopes)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team", OwnerUserId = user.Id };
        var project = TestProjectSeed.BuildDefaultProject(team.Id, user.Id);
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = ProviderKind.GitLab,
            DisplayName = "instance",
            BaseUrl = $"https://gitlab-{suffix}.local",
            OauthClientId = "client",
            OauthClientSecretEnc = encryptor.Encrypt("secret")
        };
        var cred = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            OwnerUserId = user.Id,
            Ownership = CredentialOwnership.Personal,
            AuthType = AuthType.Pat,
            DisplayName = "test-cred",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "glpat_xxx" })),
            Status = CredentialStatus.Active,
            Scopes = scopes
        };

        db.User.Add(user);
        db.Team.Add(team);
        db.Project.Add(project);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(cred);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.Id, team.Id, cred.Id);
    }
}
