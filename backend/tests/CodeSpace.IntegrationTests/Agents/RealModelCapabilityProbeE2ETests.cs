using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// Real-model E2E for the opaque-id capability PROBE: drives the REAL <see cref="ModelCapabilityProbeService"/> through
/// the REAL CustomClient (real HTTP, the real battery) over real Postgres. A live model is seeded under an OPAQUE alias
/// (capability_tier='Unknown') and probed; the assertion is a SMOKE of the production wiring — a verdict, if produced, is
/// Basic or Strong (never Frontier, never Unknown-as-a-verdict). A no-verdict (gateway infra / a model below the easy
/// floor) is NON-gating, exactly like the tier E2E. The deterministic discrimination (garbage ⇒ no verdict, score ⇒ tier,
/// monotonic, opaque-only) is owned by the fake-client integration + unit suites. Gated on CODESPACE_LLM_* (green-skip).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
public sealed class RealModelCapabilityProbeE2ETests
{
    private const string Custom = "Custom";

    private readonly PostgresFixture _fixture;

    public RealModelCapabilityProbeE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_live_model_under_an_opaque_alias_is_probed_to_a_coarse_tier()
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip (honest CI/fork behaviour)

        var teamId = await SeedTeamAsync();
        // Seed the live model under an OPAQUE id (capability_tier='Unknown') so the probe selects it.
        var cred = await SeedCredentialAsync(teamId, baseUrl, apiKey);
        await AddOpaqueModelAsync(cred, model);

        await RealModelGate.AssessLiveAsync(Custom, async () =>
        {
            using var scope = _fixture.BeginScope();
            var service = new ModelCapabilityProbeService(RealModelLiveWire.Registry(), scope.Resolve<IPayloadEncryptor>(), scope.Resolve<CodeSpaceDbContext>(), NullLogger<ModelCapabilityProbeService>.Instance);
            await service.ProbeTeamAsync(teamId, CancellationToken.None);

            var probed = await ProbedTierOf(teamId, model);

            // A null verdict is most likely a gateway-infra blip (battery calls infra-failed → responded=0 → no verdict),
            // which is non-gating; a genuine "battery too strict for real models" regression needs the gateway UP and the
            // model failing the trivial easy band, which the deterministic suites guard. A produced verdict must be a
            // coarse Basic/Strong — NEVER Frontier (the cap) and never written as Unknown.
            if (probed is null)
                return (true, $"{model}: no probed verdict (gateway infra / below the easy floor) — not gating");

            var ok = probed is ModelCapabilityTier.Basic or ModelCapabilityTier.Strong;
            return (ok, $"{model}: probed {probed} — must be Basic/Strong, never Frontier");
        });
    }

    // ─── Helpers ───

    private async Task<ModelCapabilityTier?> ProbedTierOf(Guid teamId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ModelCredentialModel.AsNoTracking()
            .Where(m => m.Credential.TeamId == teamId && m.ModelId == modelId).Select(m => m.ProbedCapabilityTier).SingleAsync();
    }

    private async Task AddOpaqueModelAsync(Guid credId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = Guid.NewGuid(), ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true, CapabilityTier = ModelCapabilityTier.Unknown });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = id, TeamId = teamId, Provider = Custom, DisplayName = "live opaque gateway",
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"rmprobe-{userId:N}@test.local", Name = $"rmprobe-{userId:N}" });
        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"rmprobe-{teamId:N}", Name = "RM Probe Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return teamId;
    }
}
