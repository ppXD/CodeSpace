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

    [Fact]
    public async Task An_unpinned_run_defaults_to_the_pools_first_enabled_model()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedCredentialAsync(teamId, "OpenAI", key: "sk-team-openai");
        await SeedModelAsync(id, "metis-coder-max", enabled: true);
        await SeedModelAsync(id, "metis-coder", enabled: true);

        var resolved = await ResolveAsync(NoPin(), teamId, Projector("OpenAI"));

        // The first ENABLED model by id across the pool — an "auto" run (no pinned model) runs on one of the team's OWN
        // models, so a custom gateway never falls back to the CLI default (codex gpt-5.5) it can't serve.
        resolved!.DefaultModel.ShouldBe("metis-coder");
    }

    [Fact]
    public async Task An_unpinned_run_picks_the_first_model_across_the_FULL_pool_with_the_matching_key()
    {
        var teamId = await SeedTeamAsync();
        // Credential B is created LATER (the old recency-first pick would choose it) and holds an alphabetically-LATE
        // model; credential A (earlier) holds an alphabetically-EARLY one. The full-pool pick must land on A's model,
        // AND the resolved key must come from A — the model + key are ONE row, never a recency-credential mismatch.
        var a = await SeedCredentialAsync(teamId, "OpenAI", key: "sk-a");
        await SeedModelAsync(a, "aaa-early", enabled: true);
        var b = await SeedCredentialAsync(teamId, "Custom", key: "sk-b");
        await SeedModelAsync(b, "zzz-late", enabled: true);

        var resolved = await ResolveAsync(NoPin(), teamId, Projector("OpenAI", "Custom"));

        resolved!.DefaultModel.ShouldBe("aaa-early", "model-first across the WHOLE pool — not the most-recent credential's first row");
        resolved.ApiKey.ShouldBe("sk-a", "the key comes from the SAME credential as the picked model");
    }

    [Fact]
    public async Task The_pool_default_excludes_models_under_a_revoked_credential()
    {
        var teamId = await SeedTeamAsync();
        var revoked = await SeedCredentialAsync(teamId, "OpenAI", key: "sk-revoked", status: CredentialStatus.Revoked);
        await SeedModelAsync(revoked, "aaa-from-revoked", enabled: true);   // alphabetically FIRST, but under a revoked credential
        var active = await SeedCredentialAsync(teamId, "OpenAI", key: "sk-active");
        await SeedModelAsync(active, "bbb-from-active", enabled: true);

        var resolved = await ResolveAsync(NoPin(), teamId, Projector("OpenAI"));

        resolved!.DefaultModel.ShouldBe("bbb-from-active", "a model under a revoked credential is not in the pool, even alphabetically first");
        resolved.ApiKey.ShouldBe("sk-active");
    }

    [Fact]
    public async Task The_default_model_skips_disabled_models()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedCredentialAsync(teamId, "OpenAI", key: "sk");
        await SeedModelAsync(id, "aaa-disabled", enabled: false);
        await SeedModelAsync(id, "zzz-enabled", enabled: true);

        (await ResolveAsync(NoPin(), teamId, Projector("OpenAI")))!.DefaultModel.ShouldBe("zzz-enabled");
    }

    [Fact]
    public async Task A_credential_with_no_models_has_a_null_default_model()
    {
        var teamId = await SeedTeamAsync();
        await SeedCredentialAsync(teamId, "OpenAI", key: "sk");

        // No registered models → null, so the CLI default stands — correct for an official vendor that hosts everything.
        (await ResolveAsync(NoPin(), teamId, Projector("OpenAI")))!.DefaultModel.ShouldBeNull();
    }

    [Fact]
    public async Task A_pinned_credential_also_carries_its_default_model()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedCredentialAsync(teamId, "OpenAI", key: "sk");
        await SeedModelAsync(id, "metis-coder-max", enabled: true);

        (await ResolveAsync(TaskWith(id), teamId, Projector("OpenAI")))!.DefaultModel.ShouldBe("metis-coder-max");
    }

    [Fact]
    public async Task An_unpinned_run_prefers_the_operator_marked_default_over_the_alphabetical_first()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedCredentialAsync(teamId, "OpenAI", key: "sk");
        await SeedModelAsync(id, "aaa-first-alpha", enabled: true);
        await SeedModelAsync(id, "zzz-marked-default", enabled: true, isDefault: true);   // alphabetically LAST, but the marked default

        // The operator-marked default wins the pool pick — that's how "auto" uses the model the operator knows works
        // (e.g. a gateway's metis-coder-max), not the misconfigured alphabetical-first.
        (await ResolveAsync(NoPin(), teamId, Projector("OpenAI")))!.DefaultModel.ShouldBe("zzz-marked-default");
    }

    [Fact]
    public async Task A_pinned_credential_prefers_its_marked_default_model()
    {
        var teamId = await SeedTeamAsync();
        var id = await SeedCredentialAsync(teamId, "OpenAI", key: "sk");
        await SeedModelAsync(id, "aaa-first-alpha", enabled: true);
        await SeedModelAsync(id, "zzz-marked-default", enabled: true, isDefault: true);

        // The pinned path (operator pinned THIS credential) also prefers the credential's marked default over alpha-first.
        (await ResolveAsync(TaskWith(id), teamId, Projector("OpenAI")))!.DefaultModel.ShouldBe("zzz-marked-default");
    }

    private async Task SeedModelAsync(Guid credentialId, string modelId, bool enabled, bool isDefault = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = Guid.NewGuid(), ModelCredentialId = credentialId, ModelId = modelId, Enabled = enabled, IsDefault = isDefault, Source = ModelSource.Manual });
        await db.SaveChangesAsync();
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
