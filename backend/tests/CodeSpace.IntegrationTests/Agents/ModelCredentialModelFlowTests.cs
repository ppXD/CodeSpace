using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The persistence contract for <see cref="ModelCredentialModel"/> against real Postgres: rows round-trip under
/// their credential; the (credential, model id) unique index is enforced and
/// is PER-credential not global; deleting the credential CASCADES its models; the DB defaults match the safe
/// floor; and — the non-negotiable acceptance gate of slice 1 — a populated model list does NOT perturb
/// just-in-time credential resolution (the new table is additive metadata the resolver never reads).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ModelCredentialModelFlowTests
{
    private readonly PostgresFixture _fixture;

    public ModelCredentialModelFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Models_persist_and_round_trip_under_their_credential()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-x");

        await AddModelAsync(credId, "claude-opus-4-8", displayName: "Opus 4.8", source: ModelSource.Reflected);
        await AddModelAsync(credId, "claude-haiku-4-5");   // defaults

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credential = await db.ModelCredential.Include(c => c.Models).SingleAsync(c => c.Id == credId);

        credential.Models.Count.ShouldBe(2);

        var opus = credential.Models.Single(m => m.ModelId == "claude-opus-4-8");
        opus.DisplayName.ShouldBe("Opus 4.8");
        opus.Source.ShouldBe(ModelSource.Reflected);
        opus.Enabled.ShouldBeTrue();

        var haiku = credential.Models.Single(m => m.ModelId == "claude-haiku-4-5");
        haiku.Source.ShouldBe(ModelSource.Manual, "an added model defaults to operator-typed");
        haiku.Enabled.ShouldBeTrue("a freshly added model is usable by default");
    }

    [Fact]
    public async Task A_duplicate_model_id_under_one_credential_is_rejected()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-x");

        await AddModelAsync(credId, "claude-sonnet-4-5");

        // The (credential, model id) unique index makes a reflection refresh idempotent.
        await Should.ThrowAsync<DbUpdateException>(() => AddModelAsync(credId, "claude-sonnet-4-5"));
    }

    [Fact]
    public async Task The_same_model_id_under_two_credentials_is_allowed()
    {
        var teamId = await SeedTeamAsync();
        var credA = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-a");
        var credB = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-b");

        await AddModelAsync(credA, "claude-sonnet-4-5");
        await AddModelAsync(credB, "claude-sonnet-4-5");   // same id, different credential — allowed

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.ModelCredentialModel.CountAsync(m => m.ModelId == "claude-sonnet-4-5" && (m.ModelCredentialId == credA || m.ModelCredentialId == credB)))
            .ShouldBe(2, "uniqueness is per-credential, not global");
    }

    [Fact]
    public async Task Deleting_the_credential_cascades_to_its_models()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-x");
        await AddModelAsync(credId, "claude-opus-4-8");
        await AddModelAsync(credId, "claude-haiku-4-5");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ModelCredential.Remove(await db.ModelCredential.SingleAsync(c => c.Id == credId));
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.ModelCredentialModel.CountAsync(m => m.ModelCredentialId == credId))
                .ShouldBe(0, "removing the credential cascades to its model rows (revoking a key removes its models)");
        }
    }

    [Fact]
    public async Task A_row_inserted_with_only_required_columns_takes_the_migration_db_defaults()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "OpenAI", key: "sk-x");

        // Insert via RAW SQL listing only (id, fk, model_id) so Postgres applies the migration's DEFAULT
        // clauses. An EF `.Add` would emit every mapped column from the C# initializers (Enabled=true,
        // Source=Manual) and never exercise the SQL defaults — this proves the migration's floor, not the POCO's.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO model_credential_model (id, model_credential_id, model_id) VALUES ({Guid.NewGuid()}, {credId}, {"gpt-5.4"})");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var row = await db.ModelCredentialModel.AsNoTracking().SingleAsync(m => m.ModelCredentialId == credId);
            row.Enabled.ShouldBeTrue("DB default: enabled = TRUE");
            row.Source.ShouldBe(ModelSource.Manual, "DB default: source = 'Manual' (string, not an int)");
            row.DisplayName.ShouldBeNull("nullable, no default → NULL");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_populated_model_list_does_not_change_credential_resolution(bool withModels)
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-pinned", baseUrl: "https://gw/v1");

        if (withModels)
        {
            await AddModelAsync(credId, "claude-opus-4-8");
            await AddModelAsync(credId, "claude-haiku-4-5");
        }

        var expected = new ResolvedModelCredential { Provider = "Anthropic", ApiKey = "sk-pinned", BaseUrl = "https://gw/v1" };

        // Both DB-touching resolver paths must return the identical decrypted credential whether or not the new
        // catalog table is populated — slice 1 is additive metadata, not a gate on existing runs. The PINNED
        // path and the unpinned TEAM-DEFAULT path are both covered; the operator-global path reads no DB row, so
        // the new table cannot affect it.
        (await ResolveAsync(new AgentTask { Goal = "g", Harness = "h", ModelCredentialId = credId }, teamId, "Anthropic"))
            .ShouldBe(expected, "pinned path is unperturbed by the model catalog");
        (await ResolveAsync(new AgentTask { Goal = "g", Harness = "h" }, teamId, "Anthropic"))
            .ShouldBe(expected, "team-default path is unperturbed by the model catalog");
    }

    // ─── Seeding helpers (mirror ModelCredentialResolverFlowTests) ───

    private async Task AddModelAsync(Guid credId, string modelId, string? displayName = null, ModelSource source = ModelSource.Manual)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.ModelCredentialModel.Add(new ModelCredentialModel
        {
            Id = Guid.NewGuid(),
            ModelCredentialId = credId,
            ModelId = modelId,
            DisplayName = displayName,
            Source = source,
        });
        await db.SaveChangesAsync();
    }

    private async Task<ResolvedModelCredential?> ResolveAsync(AgentTask task, Guid teamId, string provider)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IModelCredentialResolver>().ResolveAsync(task, teamId, new StubProjector(new[] { provider }), CancellationToken.None);
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, string provider, string? key, string? baseUrl = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = id,
            TeamId = teamId,
            Provider = provider,
            DisplayName = provider + " cred",
            EncryptedApiKey = key is null ? null : scope.Resolve<IPayloadEncryptor>().Encrypt(key),
            BaseUrl = baseUrl,
            Status = CredentialStatus.Active,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"mcm-{userId:N}@test.local", Name = $"mcm-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"mcm-{teamId:N}", Name = "Model Catalog Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    private sealed class StubProjector : IModelCredentialProjector
    {
        public StubProjector(IReadOnlyList<string> providers) => SupportedProviders = providers;
        public IReadOnlyList<string> SupportedProviders { get; }
        public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential) => new Dictionary<string, string> { ["KEY"] = credential.ApiKey ?? "" };
    }
}
