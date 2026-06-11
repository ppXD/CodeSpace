using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Persistence round-trip for the team-scoped <see cref="ModelCredential"/> store. Proves the API key is
/// encrypted AT REST (the raw column never holds the plaintext key), the enum-as-string status mapping, a
/// keyless provider (NULL key + base URL), and team isolation. Real Postgres via the migration (Rule 12).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ModelCredentialPersistenceTests
{
    private const string PlaintextKey = "sk-ant-secret-roundtrip-value";

    private readonly PostgresFixture _fixture;

    public ModelCredentialPersistenceTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Api_key_is_encrypted_at_rest_and_round_trips()
    {
        var teamId = await SeedTeamAsync();
        var id = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            var encryptor = scope.Resolve<IPayloadEncryptor>();
            var db = scope.Resolve<CodeSpaceDbContext>();

            db.ModelCredential.Add(new ModelCredential
            {
                Id = id,
                TeamId = teamId,
                Provider = "Anthropic",
                DisplayName = "Team Anthropic key",
                EncryptedApiKey = encryptor.Encrypt(PlaintextKey),
                Status = CredentialStatus.Active,
            });
            await db.SaveChangesAsync();
        }

        // The raw column must NOT contain the plaintext key — encryption-at-rest, asserted against the DB
        // directly (not through the entity), so a future "store the key plainly" regression fails here.
        var rawColumn = await ReadRawEncryptedApiKeyAsync(id);
        rawColumn.ShouldNotBeNull();
        rawColumn!.ShouldNotContain(PlaintextKey);

        using (var scope = _fixture.BeginScope())
        {
            var encryptor = scope.Resolve<IPayloadEncryptor>();
            var db = scope.Resolve<CodeSpaceDbContext>();
            var c = await db.ModelCredential.SingleAsync(x => x.Id == id);

            c.Provider.ShouldBe("Anthropic");
            c.DisplayName.ShouldBe("Team Anthropic key");
            c.Status.ShouldBe(CredentialStatus.Active);          // enum-as-string round-trips
            c.BaseUrl.ShouldBeNull();
            c.DeletedDate.ShouldBeNull();
            encryptor.Decrypt(c.EncryptedApiKey!).ShouldBe(PlaintextKey);   // decrypts back to the original
        }
    }

    [Fact]
    public async Task Keyless_provider_stores_null_key_with_a_base_url()
    {
        var teamId = await SeedTeamAsync();
        var id = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ModelCredential.Add(new ModelCredential
            {
                Id = id,
                TeamId = teamId,
                Provider = "Ollama",
                DisplayName = "Local Ollama",
                EncryptedApiKey = null,                           // keyless — reached over base URL
                BaseUrl = "http://localhost:11434",
            });
            await db.SaveChangesAsync();
        }

        using var verify = _fixture.BeginScope();
        var c = await verify.Resolve<CodeSpaceDbContext>().ModelCredential.SingleAsync(x => x.Id == id);

        c.EncryptedApiKey.ShouldBeNull();
        c.BaseUrl.ShouldBe("http://localhost:11434");             // plaintext (non-secret) round-trips
    }

    [Fact]
    public async Task A_team_scoped_query_returns_only_that_teams_credentials()
    {
        var teamA = await SeedTeamAsync();
        var teamB = await SeedTeamAsync();

        await InsertAsync(teamA, "OpenAI", "A's key");
        await InsertAsync(teamB, "OpenAI", "B's key");

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var forA = await db.ModelCredential.Where(c => c.TeamId == teamA && c.DeletedDate == null).ToListAsync();

        forA.ShouldHaveSingleItem().DisplayName.ShouldBe("A's key");
    }

    private async Task InsertAsync(Guid teamId, string provider, string displayName)
    {
        using var scope = _fixture.BeginScope();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ModelCredential.Add(new ModelCredential
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Provider = provider,
            DisplayName = displayName,
            EncryptedApiKey = encryptor.Encrypt("sk-" + Guid.NewGuid().ToString("N")),
        });
        await db.SaveChangesAsync();
    }

    private async Task<string?> ReadRawEncryptedApiKeyAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("SELECT encrypted_api_key FROM model_credential WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);

        return await cmd.ExecuteScalarAsync() as string;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"mc-{userId:N}@test.local", Name = $"mc-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"mc-{teamId:N}", Name = "Model Cred Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
