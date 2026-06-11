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
/// The security matrix for <see cref="IModelCredentialResolver"/> against real Postgres: the pinned path is
/// team-scoped and fails clean for a foreign / revoked / deleted credential (never a fall-through); the
/// team-default path is provider-matched; rotation takes effect on re-resolve; and the operator-global key is
/// the documented single-tenant last resort. Decryption is verified end-to-end via the shared encryptor.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ModelCredentialResolverFlowTests
{
    private readonly PostgresFixture _fixture;

    public ModelCredentialResolverFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_pinned_credential_resolves_and_decrypts()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-pinned", baseUrl: "https://x/v1");

        var resolved = await ResolveAsync(TaskWith(id), teamId, Projector("Anthropic"));

        resolved.ShouldNotBeNull();
        resolved!.Provider.ShouldBe("Anthropic");
        resolved.ApiKey.ShouldBe("sk-pinned");
        resolved.BaseUrl.ShouldBe("https://x/v1");
    }

    [Fact]
    public async Task A_pinned_credential_from_another_team_fails_clean()
    {
        var teamA = await SeedTeamAsync();
        var teamB = await SeedTeamAsync();
        var idInB = await SeedCredentialAsync(teamB, "Anthropic", key: "sk-b");

        // Resolving team B's credential under team A's id must throw — team comes from the run, never the envelope.
        await Should.ThrowAsync<ModelCredentialResolutionException>(() => ResolveAsync(TaskWith(idInB), teamA, Projector("Anthropic")));
    }

    [Theory]
    [InlineData(CredentialStatus.Revoked)]
    public async Task A_pinned_but_revoked_credential_fails_clean_and_never_falls_back(CredentialStatus status)
    {
        var teamId = await SeedTeamAsync();
        var pinned = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-revoked", status: status);
        // A perfectly good team-default for the SAME provider exists — the resolver must STILL fail (the author
        // pinned the revoked one; silently using a different credential would be a confused-deputy).
        await SeedCredentialAsync(teamId, "Anthropic", key: "sk-other-active");

        await Should.ThrowAsync<ModelCredentialResolutionException>(() => ResolveAsync(TaskWith(pinned), teamId, Projector("Anthropic")));
    }

    [Fact]
    public async Task A_pinned_credential_for_a_provider_the_harness_cannot_drive_fails_clean()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-anthropic");

        // The pinned credential is Anthropic but the harness only drives OpenAI → fail clean (typed), before decrypt.
        var ex = await Should.ThrowAsync<ModelCredentialResolutionException>(() => ResolveAsync(TaskWith(id), teamId, Projector("OpenAI")));
        ex.Message.ShouldNotContain("sk-anthropic");
    }

    [Fact]
    public async Task A_pinned_but_soft_deleted_credential_fails_clean()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-deleted", deleted: true);

        await Should.ThrowAsync<ModelCredentialResolutionException>(() => ResolveAsync(TaskWith(id), teamId, Projector("Anthropic")));
    }

    [Fact]
    public async Task An_unpinned_run_uses_the_teams_active_credential_for_a_supported_provider()
    {
        var teamId = await SeedTeamAsync();
        await SeedCredentialAsync(teamId, "OpenAI", key: "sk-team-openai");

        var resolved = await ResolveAsync(NoPin(), teamId, Projector("OpenAI", "OpenRouter"));

        resolved.ShouldNotBeNull();
        resolved!.ApiKey.ShouldBe("sk-team-openai", "no pin → the team's active credential for a provider the harness can drive");
    }

    [Fact]
    public async Task An_unpinned_run_ignores_a_team_credential_for_an_unsupported_provider()
    {
        var teamId = await SeedTeamAsync();
        await SeedCredentialAsync(teamId, "OpenAI", key: "sk-team-openai");   // team has only an OpenAI key

        // The harness drives Anthropic only, and no operator-global key is set → no credential applies.
        var resolved = await ResolveAsync(NoPin(), teamId, Projector("Anthropic"));

        resolved.ShouldBeNull("an OpenAI credential is not usable by an Anthropic-only harness");
    }

    [Fact]
    public async Task Rotation_takes_effect_on_re_resolve()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-old");

        (await ResolveAsync(TaskWith(id), teamId, Projector("Anthropic")))!.ApiKey.ShouldBe("sk-old");

        // Rotate the stored key (re-encrypt a new value), then resolve again — only the id is ever frozen, so a
        // replayed / re-claimed run picks up the new key live.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var row = await db.ModelCredential.SingleAsync(c => c.Id == id);
            row.EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt("sk-rotated");
            await db.SaveChangesAsync();
        }

        (await ResolveAsync(TaskWith(id), teamId, Projector("Anthropic")))!.ApiKey.ShouldBe("sk-rotated", "re-resolve reads the live row");
    }

    [Fact]
    public async Task The_operator_global_key_is_the_last_resort_when_the_team_has_none()
    {
        var teamId = await SeedTeamAsync();   // no team credential

        var original = Environment.GetEnvironmentVariable(ModelCredentialResolver.OpenAIOperatorKeyEnvVar);
        try
        {
            // CODESPACE_OPENAI_API_KEY is read by nothing else, so setting it here can't pollute parallel tests.
            Environment.SetEnvironmentVariable(ModelCredentialResolver.OpenAIOperatorKeyEnvVar, "sk-operator-global");

            var resolved = await ResolveAsync(NoPin(), teamId, Projector("OpenAI"));

            resolved.ShouldNotBeNull();
            resolved!.Provider.ShouldBe("OpenAI");
            resolved.ApiKey.ShouldBe("sk-operator-global", "single-tenant last resort — superseded by any team credential");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModelCredentialResolver.OpenAIOperatorKeyEnvVar, original);
        }
    }

    [Fact]
    public async Task A_keyless_credential_resolves_with_a_null_key_and_a_base_url()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedCredentialAsync(teamId, "Ollama", key: null, baseUrl: "http://localhost:11434");

        var resolved = await ResolveAsync(TaskWith(id), teamId, Projector("Ollama"));

        resolved.ShouldNotBeNull();
        resolved!.ApiKey.ShouldBeNull();
        resolved.BaseUrl.ShouldBe("http://localhost:11434");
    }

    [Fact]
    public async Task A_harness_with_no_projector_and_no_pin_resolves_nothing()
    {
        var teamId = await SeedTeamAsync();
        await SeedCredentialAsync(teamId, "Anthropic", key: "sk-team");

        // No projector → the resolver can't know which providers apply, so an unpinned run gets nothing.
        (await ResolveAsync(NoPin(), teamId, projector: null)).ShouldBeNull();
    }

    private static AgentTask TaskWith(Guid modelCredentialId) => new() { Goal = "g", Harness = "h", ModelCredentialId = modelCredentialId };
    private static AgentTask NoPin() => new() { Goal = "g", Harness = "h" };

    private static IModelCredentialProjector Projector(params string[] providers) => new StubProjector(providers);

    private async Task<ResolvedModelCredential?> ResolveAsync(AgentTask task, Guid teamId, IModelCredentialProjector? projector)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IModelCredentialResolver>().ResolveAsync(task, teamId, projector, CancellationToken.None);
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, string provider, string? key, string? baseUrl = null, CredentialStatus status = CredentialStatus.Active, bool deleted = false)
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
            Status = status,
            DeletedDate = deleted ? DateTimeOffset.UtcNow : null,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"mcr-{userId:N}@test.local", Name = $"mcr-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"mcr-{teamId:N}", Name = "Resolver Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
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
