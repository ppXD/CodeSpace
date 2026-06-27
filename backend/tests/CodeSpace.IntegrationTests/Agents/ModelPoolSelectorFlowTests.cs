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
/// model under an active credential of the right provider — the pool is capability-GENERIC (no structured-output gate;
/// the structured CLIENT degrades a model that can't do forced tool-use to a prompt-only JSON floor) — bounded by the
/// allowed pool (empty = all), the pin if set, preferring a supervisor-recommended one — and the backing credential is
/// decrypted. Nothing qualifies → null (the caller fails closed). No env "system" key, no default model.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ModelPoolSelectorFlowTests
{
    private readonly PostgresFixture _fixture;

    public ModelPoolSelectorFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task It_picks_a_pool_model_and_decrypts_its_credential()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-team");
        await AddModelAsync(credId, "claude-opus-4-8");

        var pick = await SelectAsync(teamId, "Anthropic");

        pick.ShouldNotBeNull();
        pick!.ModelId.ShouldBe("claude-opus-4-8");
        pick.Credential.ApiKey.ShouldBe("sk-team", "the chosen pool row's backing credential is decrypted");
        pick.Credential.Provider.ShouldBe("Anthropic");
    }

    [Fact]
    public async Task An_unpinned_pick_is_deterministic_by_model_id_order()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk");
        await AddModelAsync(credId, "claude-sonnet-4-6");
        await AddModelAsync(credId, "claude-opus-4-8");

        // No pin, no recommendation flag — the total order (model id, then row id) decides; 'opus' sorts before 'sonnet'.
        (await SelectAsync(teamId, "Anthropic"))!.ModelId.ShouldBe("claude-opus-4-8");
    }

    [Fact]
    public async Task An_unpinned_pick_prefers_the_operator_default_over_model_id_order()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk");
        await AddModelAsync(credId, "claude-opus-4-8");                      // sorts FIRST alphabetically
        await AddModelAsync(credId, "claude-sonnet-4-6", isDefault: true);   // the operator's starred default — must win

        // A1: #746 made the AGENT plane honor IsDefault; this brings the in-process BRAIN plane (planner / synthesis /
        // llm.complete) to parity. The starred model wins the unpinned auto pick even though 'opus' sorts before 'sonnet'.
        (await SelectAsync(teamId, "Anthropic"))!.ModelId.ShouldBe("claude-sonnet-4-6", "the operator's per-credential default outranks alphabetical order");
    }

    [Fact]
    public async Task Any_enabled_credentialed_model_is_selectable_the_pool_is_capability_generic()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk");
        await AddModelAsync(credId, "metis-coder-max");   // a custom gateway model with no "capability" flag

        // The pool no longer gates on a structured-output flag: the structured CLIENT degrades a model that doesn't
        // natively support forced tool-use to a prompt-only JSON floor, so ANY enabled credentialed model is selectable
        // (Dify's model-node model). A custom gateway model needs no per-model capability declaration to plan/decide.
        (await SelectAsync(teamId, "Anthropic"))!.ModelId.ShouldBe("metis-coder-max");
    }

    [Fact]
    public async Task An_empty_pool_considers_all_models_but_the_allowed_pool_bounds_it()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk");
        await AddModelAsync(credId, "claude-opus-4-8");
        await AddModelAsync(credId, "claude-sonnet-4-6");

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
        await AddModelAsync(credId, "claude-opus-4-8");
        await AddModelAsync(credId, "claude-sonnet-4-6");

        // The pin overrides the default id-order pick (opus would sort first).
        (await SelectAsync(teamId, "Anthropic", pinned: "claude-sonnet-4-6"))!.ModelId.ShouldBe("claude-sonnet-4-6");

        // A pin that isn't a qualifying pool model → null (never silently substituted).
        (await SelectAsync(teamId, "Anthropic", pinned: "not-in-pool")).ShouldBeNull();
    }

    [Theory]
    [InlineData(false, true, "Anthropic")]    // disabled → excluded
    [InlineData(true, false, "Anthropic")]    // revoked credential → excluded
    [InlineData(true, true, "OpenAI")]        // wrong provider for the Anthropic client → excluded
    public async Task Only_an_enabled_model_under_an_active_credential_of_the_provider_qualifies(bool enabled, bool active, string provider)
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, provider, key: "sk", active: active);
        await AddModelAsync(credId, "the-model", enabled: enabled);

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
        await AddModelAsync(credId, "claude-opus-4-8");

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
        await AddModelAsync(credA, "claude-opus-4-8");
        await AddModelAsync(credB, "claude-opus-4-8");

        // The total tie-break (model id, then row id) makes the pick STABLE across calls — never an arbitrary key.
        (await SelectAsync(teamId, "Anthropic"))!.Credential.ApiKey
            .ShouldBe((await SelectAsync(teamId, "Anthropic"))!.Credential.ApiKey);
    }

    [Fact]
    public async Task A_keyless_credentials_model_picks_with_a_null_key_and_its_base_url()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: null, baseUrl: "https://gw/v1");
        await AddModelAsync(credId, "claude-opus-4-8");

        var pick = await SelectAsync(teamId, "Anthropic");
        pick.ShouldNotBeNull();
        pick!.Credential.ApiKey.ShouldBeNull("a keyless gateway model is valid — reached over its base url");
        pick.Credential.BaseUrl.ShouldBe("https://gw/v1");
    }

    // ─── SelectBrainRowIdAsync (P3b): the Auto/Deep supervisor brain auto-pick ───

    [Fact]
    public async Task SelectBrainRowId_picks_an_eligible_provider_row_deterministically_and_returns_its_row_id()
    {
        var teamId = await SeedTeamAsync();
        var anthropicCred = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-a");
        var sonnetRow = await AddModelReturningIdAsync(anthropicCred, "claude-sonnet-4-6");
        var opusRow = await AddModelReturningIdAsync(anthropicCred, "claude-opus-4-8");

        // Deterministic total order (model id, then row id): 'claude-opus-4-8' sorts before 'claude-sonnet-4-6'.
        var picked = await SelectBrainRowIdAsync(teamId, "Anthropic", "OpenAI");

        picked.ShouldBe(opusRow, "the brain auto-pick is the first row in the deterministic order whose provider has a structured client — replay-stable");
        opusRow.ShouldNotBe(sonnetRow);
    }

    [Fact]
    public async Task SelectBrainRowId_prefers_the_operator_default_over_model_id_order()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-a");
        await AddModelReturningIdAsync(cred, "claude-opus-4-8");                            // sorts first
        var sonnetRow = await AddModelReturningIdAsync(cred, "claude-sonnet-4-6", isDefault: true);   // starred — must become the brain

        // A1: the supervisor brain auto-pick now honors the operator's default model, at parity with the agent plane.
        (await SelectBrainRowIdAsync(teamId, "Anthropic", "OpenAI")).ShouldBe(sonnetRow, "a starred eligible model becomes the auto brain, outranking model-id order");
    }

    [Fact]
    public async Task Two_credentials_each_with_a_default_break_the_tie_deterministically_and_replay_stable()
    {
        var teamId = await SeedTeamAsync();
        var credA = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-a");
        var credB = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-b");
        var opusRow = await AddModelReturningIdAsync(credA, "claude-opus-4-8", isDefault: true);   // both starred — IsDefault is PER-credential
        await AddModelReturningIdAsync(credB, "claude-sonnet-4-6", isDefault: true);

        // Two defaults can coexist (one per credential). The total order is IsDefault desc, THEN model id, THEN row id —
        // so the tie is broken deterministically (alphabetical 'opus' wins) and the pick is REPLAY-STABLE across calls.
        // Matches the agent plane's documented multi-default contract (ModelCredentialResolver).
        (await SelectBrainRowIdAsync(teamId, "Anthropic", "OpenAI")).ShouldBe(opusRow);
        (await SelectBrainRowIdAsync(teamId, "Anthropic", "OpenAI")).ShouldBe(opusRow, "repeated calls re-derive the SAME brain — a stable total order over the frozen pool snapshot");

        // The ambient (SelectAsync) plane breaks the same multi-default tie identically.
        (await SelectAsync(teamId, "Anthropic"))!.ModelId.ShouldBe("claude-opus-4-8");
    }

    [Fact]
    public async Task SelectBrainRowId_does_not_let_a_default_override_provider_eligibility()
    {
        var teamId = await SeedTeamAsync();
        var ollamaCred = await SeedCredentialAsync(teamId, "Ollama", key: "sk-o");
        await AddModelReturningIdAsync(ollamaCred, "llama3", isDefault: true);   // STARRED, but Ollama has no structured client
        var anthropicCred = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-a");
        var opusRow = await AddModelReturningIdAsync(anthropicCred, "zzz-opus");

        // The default orders WITHIN the eligible set; it never bypasses the fail-closed provider-eligibility floor. A
        // starred model whose provider can't run the brain is still skipped to the next eligible row.
        (await SelectBrainRowIdAsync(teamId, "Anthropic")).ShouldBe(opusRow, "the operator default is a tie-break inside the eligible set, not a bypass of provider eligibility");
    }

    [Fact]
    public async Task SelectBrainRowId_skips_a_row_whose_provider_has_no_structured_client()
    {
        var teamId = await SeedTeamAsync();
        var ollamaCred = await SeedCredentialAsync(teamId, "Ollama", key: "sk-o");
        await AddModelAsync(ollamaCred, "llama3");   // alphabetically first, but Ollama is NOT eligible
        var anthropicCred = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-a");
        var opusRow = await AddModelReturningIdAsync(anthropicCred, "zzz-opus");   // sorts LAST, but its provider is eligible

        // Only Anthropic is an eligible (structured-capable) provider → the Ollama row is skipped even though it sorts first.
        (await SelectBrainRowIdAsync(teamId, "Anthropic")).ShouldBe(opusRow, "a row whose provider has no structured client is never auto-picked as the brain");
    }

    [Fact]
    public async Task An_unpinned_pick_prefers_the_higher_capability_tier_over_model_id_order()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk");
        await AddModelAsync(credId, "aaa-basic", tier: ModelCapabilityTier.Basic);          // sorts FIRST alphabetically
        await AddModelAsync(credId, "zzz-frontier", tier: ModelCapabilityTier.Frontier);    // sorts LAST, but the strongest

        // The precedence ladder is IsDefault > tier > alphabetical — so "auto = the strongest available", not the
        // alphabetical-first model.
        (await SelectAsync(teamId, "Anthropic"))!.ModelId.ShouldBe("zzz-frontier", "auto prefers the higher capability tier over model-id order");
    }

    [Fact]
    public async Task An_untiered_pool_breaks_the_model_id_tie_by_ordinal_locale_independently()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk");
        await AddModelAsync(credId, "GPT-4o");        // both un-tiered (tier=null); ids differ only by case
        await AddModelAsync(credId, "gpt-4o-mini");

        // The in-memory tie-break is StringComparer.Ordinal, NOT the DB collation — so 'GPT-4o' ('G'=0x47) sorts before
        // 'gpt-4o-mini' ('g'=0x67) deterministically, the SAME in CI and locally (the old DB-collation order could flip
        // by locale). This pins the no-tier path explicitly.
        (await SelectAsync(teamId, "Anthropic"))!.ModelId.ShouldBe("GPT-4o", "an un-tiered pool's tie-break is locale-independent Ordinal order");
    }

    [Fact]
    public async Task An_operator_default_still_outranks_a_higher_tier()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic", key: "sk");
        await AddModelAsync(credId, "frontier-model", tier: ModelCapabilityTier.Frontier);
        await AddModelAsync(credId, "starred-basic", tier: ModelCapabilityTier.Basic, isDefault: true);   // the operator's pick

        // IsDefault wins FIRST — the operator's deliberate choice outranks the inferred tier (the human pin is the top of the ladder).
        (await SelectAsync(teamId, "Anthropic"))!.ModelId.ShouldBe("starred-basic", "the operator default outranks a higher tier — IsDefault is the top of the precedence ladder");
    }

    [Fact]
    public async Task SelectBrainRowId_prefers_the_higher_tier_then_falls_back_to_model_id()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId, "Anthropic", key: "sk-a");
        await AddModelReturningIdAsync(cred, "aaa-basic", tier: ModelCapabilityTier.Basic);                  // sorts first
        var frontierRow = await AddModelReturningIdAsync(cred, "zzz-frontier", tier: ModelCapabilityTier.Frontier);

        // The auto brain is the strongest eligible model, not the alphabetical-first one.
        (await SelectBrainRowIdAsync(teamId, "Anthropic", "OpenAI")).ShouldBe(frontierRow, "the supervisor brain auto-pick prefers the higher capability tier");
    }

    [Fact]
    public async Task SelectBrainRowId_picks_a_Custom_provider_row_when_Custom_is_eligible()
    {
        // Custom endpoints to the supervisor: once "Custom" is a registered structured provider, a Custom-tagged pool
        // model is an eligible supervisor BRAIN — so a team whose pool is all-Custom still bakes a brain (no NoBrainModelStop).
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Custom", key: "gw-key");
        var row = await AddModelReturningIdAsync(credId, "metis-coder-max");

        (await SelectBrainRowIdAsync(teamId, "Custom")).ShouldBe(row, "a Custom-tagged credentialed model is an eligible brain row");
        (await SelectBrainRowIdAsync(teamId, "OpenAI", "Anthropic")).ShouldBeNull("but only when 'Custom' is in the eligible set — it is not OpenAI/Anthropic");
    }

    [Fact]
    public async Task SelectBrainRowId_returns_null_when_no_enabled_row_has_an_eligible_provider()
    {
        var teamId = await SeedTeamAsync();
        var ollamaCred = await SeedCredentialAsync(teamId, "Ollama", key: "sk-o");
        await AddModelAsync(ollamaCred, "llama3");

        (await SelectBrainRowIdAsync(teamId, "Anthropic", "OpenAI")).ShouldBeNull("no eligible-provider row → null → the builder emits no brain → fail-closed at decide time (the honest floor)");
        (await SelectBrainRowIdAsync(teamId)).ShouldBeNull("an empty eligible-provider set → null");
    }

    // ─── Helpers ───

    private async Task<Guid?> SelectBrainRowIdAsync(Guid teamId, params string[] eligibleProviders)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IModelPoolSelector>().SelectBrainRowIdAsync(teamId, eligibleProviders, CancellationToken.None);
    }

    private async Task<Guid> AddModelReturningIdAsync(Guid credId, string modelId, bool isDefault = false, ModelCapabilityTier? tier = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = id, ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true, IsDefault = isDefault, CapabilityTier = tier });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowed = null, string? pinned = null)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IModelPoolSelector>().SelectAsync(teamId, provider, allowed, pinned, CancellationToken.None);
    }

    private async Task AddModelAsync(Guid credId, string modelId, bool enabled = true, bool isDefault = false, ModelCapabilityTier? tier = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.ModelCredentialModel.Add(new ModelCredentialModel
        {
            Id = Guid.NewGuid(), ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual,
            Enabled = enabled, IsDefault = isDefault, CapabilityTier = tier,
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
