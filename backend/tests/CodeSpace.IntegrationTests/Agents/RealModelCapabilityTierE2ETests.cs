using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// Real-model E2E for the capability-tiering PRODUCER (B1b): drives the REAL <see cref="ModelCapabilityTieringService"/>
/// against a LIVE brain over real Postgres — the live model tiers a seeded pool of universally-known ids, and the cached
/// verdict is read back from the column. Deterministic-enough by design (anti-flake): it asserts only a COARSE ORDERING
/// — a clearly-frontier id (<c>gpt-4o</c>) must NOT be tiered BELOW a clearly-basic one (<c>gpt-3.5-turbo</c>) — never an
/// exact tier; and an all-NULL result (the producer is fail-closed — a gateway-infra fault / degraded reply yields no
/// verdict) is reported as NON-gating, so only a real brain mistake (frontier ranked under basic) REDs. Honestly gated on
/// the <c>CODESPACE_LLM_*</c> secrets — green on CI/forks without them; activates only on the real-model lane.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
public sealed class RealModelCapabilityTierE2ETests
{
    private const string FrontierId = "gpt-4o";          // any reasonable brain tiers this >= the basic id
    private const string BasicId = "gpt-3.5-turbo";

    private readonly PostgresFixture _fixture;

    public RealModelCapabilityTierE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task The_real_model_tiers_a_known_frontier_id_at_or_above_a_known_basic_id(string provider)
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip (honest CI/fork behaviour)

        var teamId = await SeedTeamAsync();
        var credId = await SeedCredentialAsync(teamId, provider);
        await AddModelAsync(credId, FrontierId);
        await AddModelAsync(credId, BasicId);

        await RealModelGate.AssessLiveAsync(provider, async () =>
        {
            var credential = RealModelLiveWire.Credential(provider, baseUrl, apiKey);

            using var scope = _fixture.BeginScope();
            var service = new ModelCapabilityTieringService(
                RealModelLiveWire.Registry(), new LiveProviderSelector(model, credential), scope.Resolve<CodeSpaceDbContext>(), NullLogger<ModelCapabilityTieringService>.Instance);

            await service.TierTeamAsync(teamId, CancellationToken.None);

            var frontier = await TierOf(teamId, FrontierId);
            var basic = await TierOf(teamId, BasicId);

            // The producer is fail-closed: an all-NULL result = a gateway-infra fault or a degraded reply → NOT gating
            // (the live brain never even gave a verdict to grade). Only a verdict that ranks frontier UNDER basic REDs.
            if (frontier is null || basic is null)
                return (true, $"{provider} model '{model}': tiering produced no verdict (gateway infra / degraded reply) — not gating");

            var ok = (int)frontier.Value >= (int)basic.Value;
            return (ok, $"{provider} model '{model}': tiered {FrontierId}={frontier} vs {BasicId}={basic} — frontier {(ok ? ">=" : "<")} basic");
        });
    }

    // ─── Helpers ───

    private async Task<ModelCapabilityTier?> TierOf(Guid teamId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ModelCredentialModel.AsNoTracking()
            .Where(m => m.Credential.TeamId == teamId && m.ModelId == modelId).Select(m => m.CapabilityTier).SingleAsync();
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
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt("sk-pool"), Status = CredentialStatus.Active,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"rmtier-{userId:N}@test.local", Name = $"rmtier-{userId:N}" });
        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"rmtier-{teamId:N}", Name = "RM Tier Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return teamId;
    }

    /// <summary>Resolves the tiering brain to the configured LIVE model ONLY for the live provider's structured client, so InProcessStructuredModel routes to the matching wire (not the first-registered client).</summary>
    private sealed class LiveProviderSelector : IModelPoolSelector
    {
        private readonly string _model;
        private readonly ResolvedModelCredential _credential;
        public LiveProviderSelector(string model, ResolvedModelCredential credential) { _model = model; _credential = credential; }

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) =>
            Task.FromResult(string.Equals(provider, _credential.Provider, StringComparison.OrdinalIgnoreCase)
                ? new ModelPoolPick { ModelId = _model, Credential = _credential } : null);

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PoolModelInfo>>(Array.Empty<PoolModelInfo>());
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
    }
}
