using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="ModelPoolSelector"/> at the resolve seam, a FAKE structured client
/// at the LLM seam): the capability-tiering PRODUCER writes the brain's verdict to <c>capability_tier</c> + <c>last_tiered_at</c>
/// for a team's not-yet-tiered pool models, is IDEMPOTENT (a re-run is a cache hit — no second LLM call, already-tiered
/// rows skipped), and FAIL-CLOSED (a throwing client leaves the rows un-tiered, never crashes). Also pins the CONSUMER
/// side: <see cref="ModelPoolSelector.ListPoolAsync"/> surfaces the cached tier (dedup by id+provider carries the best).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ModelCapabilityTieringFlowTests
{
    private readonly PostgresFixture _fixture;

    public ModelCapabilityTieringFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task It_tiers_a_teams_pending_models_and_is_an_idempotent_cache()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic");
        await AddModelAsync(credId, "claude-opus-4-8");
        await AddModelAsync(credId, "metis-coder-max");   // an opaque gateway alias the brain returns 'unknown' for

        var client = new CannedClient("Anthropic", Tiers(("claude-opus-4-8", "frontier"), ("metis-coder-max", "unknown")));
        await TierAsync(teamId, client);

        client.Calls.ShouldBe(1, "one structured call tiers the whole batch");
        (await TierOf(teamId, "claude-opus-4-8")).ShouldBe(ModelCapabilityTier.Frontier);
        (await TierOf(teamId, "metis-coder-max")).ShouldBe(ModelCapabilityTier.Unknown, "an unrecognised opaque id caches as Unknown — the later objective-probe hook");
        (await LastTieredAtOf(teamId, "claude-opus-4-8")).ShouldNotBeNull("the staleness gate is stamped");

        // Idempotent: a second run finds nothing pending (both rows now non-null) → NO second LLM call.
        await TierAsync(teamId, client);
        client.Calls.ShouldBe(1, "already-tiered rows are skipped — a re-run is a cache hit, not a second live call");
    }

    [Fact]
    public async Task A_tiering_failure_leaves_the_models_untiered_fail_closed()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic");
        await AddModelAsync(credId, "claude-opus-4-8");

        await TierAsync(teamId, new ThrowingClient("Anthropic"));   // the client throws — tiering is advisory, must not crash

        (await TierOf(teamId, "claude-opus-4-8")).ShouldBeNull("a tiering miss leaves the row un-tiered (NULL); the catalog renders byte-identically");
    }

    [Fact]
    public async Task A_partial_reply_stamps_the_omitted_id_backs_it_off_then_re_tries_when_stale()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic");
        await AddModelAsync(credId, "claude-opus-4-8");
        await AddModelAsync(credId, "mystery-model");

        // The brain returns a verdict for only ONE of the two ids (a truncated / omitted reply).
        var client = new CannedClient("Anthropic", Tiers(("claude-opus-4-8", "frontier")));
        await TierAsync(teamId, client);

        client.Calls.ShouldBe(1);
        (await TierOf(teamId, "claude-opus-4-8")).ShouldBe(ModelCapabilityTier.Frontier);
        (await TierOf(teamId, "mystery-model")).ShouldBeNull("the omitted id stays un-tiered (null)");
        (await LastTieredAtOf(teamId, "mystery-model")).ShouldNotBeNull("but the ATTEMPT is stamped — the back-off gate skips it next tick");

        // An immediate re-run does NOT re-fire a live call (the omitted id is within the back-off window).
        await TierAsync(teamId, client);
        client.Calls.ShouldBe(1, "an omitted/truncated id backs off via last_tiered_at — no unbounded re-tiering every tick");

        // Once the attempt is older than the back-off window, the still-null id is re-tried + tiered.
        await MakeStaleAsync(teamId, "mystery-model");
        var client2 = new CannedClient("Anthropic", Tiers(("mystery-model", "strong")));
        await TierAsync(teamId, client2);

        client2.Calls.ShouldBe(1, "a stale un-tiered row is re-attempted after the back-off window");
        (await TierOf(teamId, "mystery-model")).ShouldBe(ModelCapabilityTier.Strong);
    }

    [Fact]
    public async Task An_empty_reply_stamps_the_attempt_and_does_not_re_fire_every_tick()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic");
        await AddModelAsync(credId, "claude-opus-4-8");

        var client = new CannedClient("Anthropic", Tiers());   // a degenerate {"models":[]} reply
        await TierAsync(teamId, client);
        await TierAsync(teamId, client);

        (await TierOf(teamId, "claude-opus-4-8")).ShouldBeNull("an empty reply leaves the row un-tiered");
        (await LastTieredAtOf(teamId, "claude-opus-4-8")).ShouldNotBeNull("but stamps the attempt");
        client.Calls.ShouldBe(1, "the empty reply backs off — not re-fired every tick");
    }

    [Fact]
    public async Task ListPool_surfaces_the_cached_tier()
    {
        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, "Anthropic");
        await AddModelAsync(credId, "claude-opus-4-8");

        await TierAsync(teamId, new CannedClient("Anthropic", Tiers(("claude-opus-4-8", "frontier"))));

        using var scope = _fixture.BeginScope();
        var pool = await scope.Resolve<IModelPoolSelector>().ListPoolAsync(teamId, allowedRowIds: null, CancellationToken.None);
        pool.Single(p => p.ModelId == "claude-opus-4-8").Tier.ShouldBe(ModelCapabilityTier.Frontier, "the catalog reader surfaces the cached tier");
    }

    // ─── Helpers ───

    private async Task TierAsync(Guid teamId, IStructuredLLMClient client)
    {
        using var scope = _fixture.BeginScope();
        var service = new ModelCapabilityTieringService(new FakeClients(client), scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>(), NullLogger<ModelCapabilityTieringService>.Instance);
        await service.TierTeamAsync(teamId, CancellationToken.None);
    }

    private async Task<ModelCapabilityTier?> TierOf(Guid teamId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ModelCredentialModel.AsNoTracking()
            .Where(m => m.Credential.TeamId == teamId && m.ModelId == modelId).Select(m => m.CapabilityTier).SingleAsync();
    }

    private async Task<DateTimeOffset?> LastTieredAtOf(Guid teamId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ModelCredentialModel.AsNoTracking()
            .Where(m => m.Credential.TeamId == teamId && m.ModelId == modelId).Select(m => m.LastTieredAt).SingleAsync();
    }

    private static JsonElement Tiers(params (string Id, string Tier)[] models) =>
        JsonSerializer.SerializeToElement(new { models = models.Select(m => new { id = m.Id, tier = m.Tier }) });

    /// <summary>Age a row's last_tiered_at past the back-off window so the next tick re-attempts it (the staleness recovery path).</summary>
    private async Task MakeStaleAsync(Guid teamId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var row = await db.ModelCredentialModel.SingleAsync(m => m.Credential.TeamId == teamId && m.ModelId == modelId);
        row.LastTieredAt = DateTimeOffset.UtcNow.AddDays(-2);
        await db.SaveChangesAsync();
    }

    private async Task AddModelAsync(Guid credId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = Guid.NewGuid(), ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, string provider)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = id, TeamId = teamId, Provider = provider, DisplayName = provider + " cred",
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt("sk-team"), Status = CredentialStatus.Active,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"tier-{userId:N}@test.local", Name = $"tier-{userId:N}" });
        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"tier-{teamId:N}", Name = "Tier Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return teamId;
    }

    // ─── Fakes (the LLM seam only — the resolve + persistence seams are REAL) ───

    private sealed class FakeClients : ILLMClientRegistry
    {
        public FakeClients(IStructuredLLMClient structured) => All = new ILLMClient[] { (ILLMClient)structured };
        public IReadOnlyList<ILLMClient> All { get; }
        public ILLMClient Resolve(string provider) => All.First();
    }

    private sealed class CannedClient : ILLMClient, IStructuredLLMClient
    {
        private readonly JsonElement _json;
        public CannedClient(string provider, JsonElement json) { Provider = provider; _json = json; }
        public string Provider { get; }
        public int Calls { get; private set; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new StructuredLLMCompletion { Json = _json, Model = request.Model });
        }
    }

    private sealed class ThrowingClient : ILLMClient, IStructuredLLMClient
    {
        public ThrowingClient(string provider) => Provider = provider;
        public string Provider { get; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) => throw new InvalidOperationException("boom");
    }
}
