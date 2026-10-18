using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>The rows of one GitLab connection: a team, its provider instance, an OAuth credential and a repository bound through it.</summary>
public sealed record SeededGitLabCredential(Guid UserId, Guid TeamId, Guid InstanceId, Guid CredentialId, Guid RepositoryId, string ExternalId);

/// <summary>
/// Seeds a team's connection to a <see cref="GitLabDoorkeeperStub"/>: an instance whose OAuth client is the stub's default
/// application, and an OAuth credential holding the pair the test says the row holds. That pair is not put in the stub:
/// a test that wants GitLab to know it calls <see cref="GitLabDoorkeeperStub.Seed"/>, and one that wants a row holding a
/// token GitLab never heard of — a lost rotation — simply does not.
/// </summary>
public static class GitLabCredentialSeed
{
    /// <summary>
    /// <paramref name="expired"/> puts the payload's expiry a minute in the past, so the first caller must refresh;
    /// otherwise it is two hours out. An <paramref name="existingTeam"/> is reused with its user; the instance, credential
    /// and repository are new either way.
    /// </summary>
    public static async Task<SeededGitLabCredential> SeedAsync(PostgresFixture fixture, string gitLabBaseUrl, string accessToken, string refreshToken, bool expired = true, RepositoryVisibility visibility = RepositoryVisibility.Private, (Guid TeamId, Guid UserId)? existingTeam = null)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (team, userId) = existingTeam ?? await AddTeamAsync(db, suffix).ConfigureAwait(false);
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team, Provider = ProviderKind.GitLab, DisplayName = "gitlab", BaseUrl = gitLabBaseUrl, ApiUrl = gitLabBaseUrl, OauthClientId = GitLabDoorkeeperStub.DefaultClientId, OauthClientSecretEnc = encryptor.Encrypt(GitLabDoorkeeperStub.DefaultClientSecret) };
        var payload = new OAuthPayload { AccessToken = accessToken, RefreshToken = refreshToken, ExpiresAt = expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(2) };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = instance.Id, AuthType = AuthType.OAuth, DisplayName = "GitLab · NACHO.L", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload)), ExpiresDate = payload.ExpiresAt, Status = CredentialStatus.Active };
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = Random.Shared.Next(100_000, 999_999).ToString(), NamespacePath = "acme", Name = "api", FullPath = "acme/api", DefaultBranch = "main", Visibility = visibility, WebUrl = $"{gitLabBaseUrl}/acme/api", CloneUrlHttps = $"{gitLabBaseUrl}/acme/api.git", Status = RepositoryStatus.Active };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repository);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SeededGitLabCredential(userId, team, instance.Id, credential.Id, repository.Id, repository.ExternalId);
    }

    /// <summary>What the credential row holds now, decrypted — what the next refresh would present.</summary>
    public static async Task<OAuthPayload> ReadStoredPayloadAsync(PostgresFixture fixture, Guid credentialId)
    {
        using var scope = fixture.BeginScope();
        var credential = await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);

        return (OAuthPayload)scope.Resolve<ICredentialResolver>().Resolve(credential);
    }

    public static async Task<Credential> ReadCredentialAsync(PostgresFixture fixture, Guid credentialId)
    {
        using var scope = fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);
    }

    private static async Task<(Guid TeamId, Guid UserId)> AddTeamAsync(CodeSpaceDbContext db, string suffix)
    {
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "nacho" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };

        db.User.Add(user);
        db.Team.Add(team);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (team.Id, user.Id);
    }
}
