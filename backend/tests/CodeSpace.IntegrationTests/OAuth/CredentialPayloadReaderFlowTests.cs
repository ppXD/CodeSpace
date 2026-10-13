using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// A refresh overtaken by a write that blanked its row ends the pair it was issued the way that write ends tokens, and
/// tells the two writes apart by <see cref="ICredentialPayloadReader.IsRetiredAsync"/>: a retirement (connection delete,
/// departure, relink) soft-deletes the row; a Disconnect leaves it listed, Revoked. A row that is gone counts as retired,
/// so the refresher never ends a grant on a guess. Read through the production reader's detached connection against
/// Postgres, on rows written as each path leaves them.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CredentialPayloadReaderFlowTests
{
    private readonly PostgresFixture _fixture;

    public CredentialPayloadReaderFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    public enum StoredState { Active, Disconnected, Retired, Gone }

    [Theory]
    [InlineData(StoredState.Active, false)]
    [InlineData(StoredState.Disconnected, false)]
    [InlineData(StoredState.Retired, true)]
    [InlineData(StoredState.Gone, true)]
    public async Task A_credential_is_retired_once_soft_deleted_or_gone_and_not_while_listed(StoredState state, bool expectedRetired)
    {
        var credentialId = state == StoredState.Gone ? Guid.NewGuid() : await SeedCredentialAsync(state);

        using var scope = _fixture.BeginScope();
        var retired = await scope.Resolve<ICredentialPayloadReader>().IsRetiredAsync(credentialId, CancellationToken.None);

        retired.ShouldBe(expectedRetired, $"a {state} credential decides whether a refresh it overtook ends the user's whole grant — check the query in CredentialPayloadReader.IsRetiredAsync");
    }

    /// <summary>A credential as each path leaves it: a Disconnect blanks and revokes it in place; a retirement also soft-deletes it.</summary>
    private async Task<Guid> SeedCredentialAsync(StoredState state)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var team = new Team { Id = Guid.NewGuid(), Slug = $"reader-{Guid.NewGuid().ToString("N")[..8]}", Name = "Reader" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitHub, DisplayName = "GitHub", BaseUrl = "https://github.example" };
        var credential = new Credential
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.OAuth, DisplayName = "octocat",
            EncryptedPayload = state == StoredState.Active ? "CfDJ8-CANARY-live-token" : string.Empty,
            Status = state == StoredState.Active ? CredentialStatus.Active : CredentialStatus.Revoked,
            DeletedDate = state == StoredState.Retired ? DateTimeOffset.UtcNow : null
        };

        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        await db.SaveChangesAsync();

        return credential.Id;
    }
}
