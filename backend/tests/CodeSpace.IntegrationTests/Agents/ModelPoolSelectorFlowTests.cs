using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The shared POOL-DRIVEN model selection (<see cref="IModelPoolSelector"/>) every in-process LLM caller uses — the
/// supervisor decider, the workflow planner, the <c>llm.complete</c> node, the supervisor synthesis — against real
/// Postgres: the model + key come entirely from the team's credentialed-model pool. A qualifying row is an enabled
/// model under an active credential of the right provider, narrowed to structured-capable only when the caller asks
/// (the decider/planner do; a free-text reduce does not), bounded by the allowed pool (empty = all), the pin if set,
/// preferring a supervisor-recommended one — and the backing credential is decrypted. Nothing qualifies → null
/// (the caller fails closed). No env "system" key, no default model.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ModelPoolSelectorFlowTests
{
    private readonly PostgresFixture _fixture;

    public ModelPoolSelectorFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task It_picks_a_structured_capable_model_and_decrypts_its_credential()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-team");
        await AddModelAsync(credId, "claude-opus-4-8", structured: true);

        var pick = await SelectAsync(teamId, "Anthropic");

        pick.ShouldNotBeNull();
        pick!.ModelId.ShouldBe("claude-opus-4-8");
        pick.Credential.ApiKey.ShouldBe("sk-team", "the chosen pool row's backing credential is decrypted");
        pick.Credential.Provider.ShouldBe("Anthropic");
    }

    [Fact]
    public async Task It_prefers_a_supervisor_recommended_model()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk");
        await AddModelAsync(credId, "claude-sonnet-4-6", structured: true);                      // capable, not recommended
        await AddModelAsync(credId, "claude-opus-4-8", structured: true, recommended: true);     // recommended

        (await SelectAsync(teamId, "Anthropic"))!.ModelId.ShouldBe("claude-opus-4-8");
    }

    [Fact]
    public async Task A_free_text_caller_may_pick_a_non_structured_model()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk");
        await AddModelAsync(credId, "claude-haiku-4-5", structured: false);   // not structured-capable

        // requireStructured:false (a free-text reduce, e.g. synthesis) accepts it; requireStructured:true excludes it.
        (await SelectAsync(teamId, "Anthropic", requireStructured: false))!.ModelId.ShouldBe("claude-haiku-4-5");
        (await SelectAsync(teamId, "Anthropic", requireStructured: true)).ShouldBeNull();
    }

    [Fact]
    public async Task An_empty_pool_considers_all_models_but_the_allowed_pool_bounds_it()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk");
        await AddModelAsync(credId, "claude-opus-4-8", structured: true);
        await AddModelAsync(credId, "claude-sonnet-4-6", structured: true);

        // Empty pool → all qualify (the recommended-tie-break / id order decides).
        (await SelectAsync(teamId, "Anthropic", allowed: null)).ShouldNotBeNull();

        // Allowed pool bounds it to exactly the named model.
        (await SelectAsync(teamId, "Anthropic", allowed: new[] { "claude-sonnet-4-6" }))!.ModelId.ShouldBe("claude-sonnet-4-6");

        // A pool naming only an UNCONFIGURED model → nothing qualifies → fail-closed.
        (await SelectAsync(teamId, "Anthropic", allowed: new[] { "not-configured" })).ShouldBeNull();
    }

    [Fact]
    public async Task The_pin_wins_and_must_be_a_qualifying_pool_model()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk");
        await AddModelAsync(credId, "claude-opus-4-8", structured: true, recommended: true);
        await AddModelAsync(credId, "claude-sonnet-4-6", structured: true);

        // The pin overrides the recommended-preference.
        (await SelectAsync(teamId, "Anthropic", pinned: "claude-sonnet-4-6"))!.ModelId.ShouldBe("claude-sonnet-4-6");

        // A pin that isn't a qualifying pool model → null (never silently substituted).
        (await SelectAsync(teamId, "Anthropic", pinned: "not-in-pool")).ShouldBeNull();
    }

    [Theory]
    [InlineData(false, true, true, "Anthropic")]    // not structured → excluded
    [InlineData(true, false, true, "Anthropic")]    // disabled → excluded
    [InlineData(true, true, false, "Anthropic")]    // revoked credential → excluded
    [InlineData(true, true, true, "OpenAI")]        // wrong provider for the Anthropic client → excluded
    public async Task Only_an_enabled_structured_model_under_an_active_credential_of_the_provider_qualifies(bool structured, bool enabled, bool active, string provider)
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, provider, key: "sk", active: active);
        await AddModelAsync(credId, "the-model", structured: structured, enabled: enabled);

        (await SelectAsync(teamId, "Anthropic")).ShouldBeNull();
    }

    [Fact]
    public async Task An_empty_pool_with_no_models_is_null()
    {
        var teamId = await SeedTeamAsync();
        await SeedCredentialAsync(teamId, "Anthropic", key: "sk");   // a credential, but no models on it

        (await SelectAsync(teamId, "Anthropic")).ShouldBeNull();
    }

    [Fact]
    public async Task Provider_and_pool_matching_are_case_insensitive_matching_the_agent_side_convention()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "anthropic", key: "sk");   // lower-case provider
        await AddModelAsync(credId, "claude-opus-4-8", structured: true);

        // The structured client's provider tag is "Anthropic" (upper) — the credential is "anthropic" (lower): a match.
        (await SelectAsync(teamId, "Anthropic"))!.ModelId.ShouldBe("claude-opus-4-8");

        // An allowed pool / pin authored in a different case still matches the pool's exact model id (S4 clamp parity).
        (await SelectAsync(teamId, "Anthropic", allowed: new[] { "CLAUDE-OPUS-4-8" }))!.ModelId.ShouldBe("claude-opus-4-8");
        (await SelectAsync(teamId, "Anthropic", pinned: "Claude-Opus-4-8"))!.ModelId.ShouldBe("claude-opus-4-8");
    }

    [Fact]
    public async Task Two_credentials_of_the_same_provider_with_the_same_model_pick_deterministically()
    {
        var teamId = await SeedTeamAsync();
        var credA = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-a");
        var credB = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-b");
        await AddModelAsync(credA, "claude-opus-4-8", structured: true, recommended: true);
        await AddModelAsync(credB, "claude-opus-4-8", structured: true, recommended: true);

        // The total tie-break (model id, then row id) makes the pick STABLE across calls — never an arbitrary key.
        (await SelectAsync(teamId, "Anthropic"))!.Credential.ApiKey
            .ShouldBe((await SelectAsync(teamId, "Anthropic"))!.Credential.ApiKey);
    }

    [Fact]
    public async Task A_keyless_credentials_model_picks_with_a_null_key_and_its_base_url()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: null, baseUrl: "https://gw/v1");
        await AddModelAsync(credId, "claude-opus-4-8", structured: true);

        var pick = await SelectAsync(teamId, "Anthropic");
        pick.ShouldNotBeNull();
        pick!.Credential.ApiKey.ShouldBeNull("a keyless gateway model is valid — reached over its base url");
        pick.Credential.BaseUrl.ShouldBe("https://gw/v1");
    }

    // ─── Helpers ───

    private async Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, bool requireStructured = true, IReadOnlyList<string>? allowed = null, string? pinned = null)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IModelPoolSelector>().SelectAsync(teamId, provider, requireStructured, allowed, pinned, CancellationToken.None);
    }

    private async Task AddModelAsync(Guid credId, string modelId, bool structured = false, bool enabled = true, bool recommended = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.ModelCredentialModel.Add(new ModelCredentialModel
        {
            Id = Guid.NewGuid(), ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual,
            SupportsStructuredOutput = structured, RecommendedForSupervisor = recommended, Enabled = enabled,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, string provider, string? key, bool active = true, string? baseUrl = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = id, TeamId = teamId, Provider = provider, DisplayName = provider + " cred",
            EncryptedApiKey = key is null ? null : scope.Resolve<IPayloadEncryptor>().Encrypt(key),
            BaseUrl = baseUrl,
            Status = active ? CredentialStatus.Active : CredentialStatus.Revoked,
            DeletedDate = active ? null : DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"sms-{userId:N}@test.local", Name = $"sms-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"sms-{teamId:N}", Name = "Selector Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
