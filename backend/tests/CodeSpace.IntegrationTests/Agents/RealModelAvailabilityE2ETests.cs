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
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// Real-transport E2E for the availability PRODUCER: drives the REAL <see cref="ModelAvailabilityProbeService"/> through
/// the REAL <c>CustomClient</c> (real HTTP) over real Postgres. Two halves: (1) a LIVE Custom gateway pings available —
/// gated on the <c>CODESPACE_LLM_*</c> secrets (green-skip without them); (2) a dead loopback endpoint pings unavailable
/// — DETERMINISTIC (a refused connection), asserted DIRECTLY, NOT through <c>RealModelGate</c> (which would swallow the
/// ConnectionRefused as a non-gating infra skip and let a "dead ⇒ not-marked-false" regression pass green). The fake-client
/// integration suite owns the exception-mapping state machine; this proves the production transport wiring end to end.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
public sealed class RealModelAvailabilityE2ETests
{
    private const string Custom = "Custom";

    private readonly PostgresFixture _fixture;

    public RealModelAvailabilityE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_live_custom_gateway_pings_available()
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip (honest CI/fork behaviour)

        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId, Custom, baseUrl, apiKey);
        await AddModelAsync(cred, model);

        await ProbeAsync(teamId);

        (await AvailableOf(teamId, model)).ShouldBe(true,
            customMessage: $"the live gateway at {baseUrl} responded to the minimal ping — reachable. If this REDs, confirm the CODESPACE_LLM_* endpoint is up (curl it).");
    }

    [Fact]
    public async Task A_dead_custom_endpoint_pings_unavailable()
    {
        var teamId = await SeedTeamAsync();
        // A refused loopback port — no live secret needed. The real CustomClient's transport maps the ConnectionRefused
        // to LlmApiException{StatusCode:null} ⇒ the probe records available=false.
        var cred = await SeedCredentialAsync(teamId, Custom, "http://127.0.0.1:1/v1", apiKey: "sk-unused");
        await AddModelAsync(cred, "ping-test");

        await ProbeAsync(teamId);

        (await AvailableOf(teamId, "ping-test")).ShouldBe(false,
            customMessage: "a refused connection to a dead endpoint must record available=false through the real CustomClient transport");
    }

    // ─── Helpers ───

    private async Task ProbeAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        // The REAL CustomClient (real HTTP) via RealModelLiveWire's registry; the REAL encryptor + DbContext.
        var service = new ModelAvailabilityProbeService(RealModelLiveWire.Registry(), scope.Resolve<IPayloadEncryptor>(), scope.Resolve<CodeSpaceDbContext>(), NullLogger<ModelAvailabilityProbeService>.Instance);
        await service.ProbeTeamAsync(teamId, CancellationToken.None);
    }

    private async Task<bool?> AvailableOf(Guid teamId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ModelCredentialModel.AsNoTracking()
            .Where(m => m.Credential.TeamId == teamId && m.ModelId == modelId).Select(m => m.Available).SingleAsync();
    }

    private async Task AddModelAsync(Guid credId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = Guid.NewGuid(), ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, string provider, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = id, TeamId = teamId, Provider = provider, DisplayName = provider + " cred",
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
        db.User.Add(new User { Id = userId, Email = $"rmavail-{userId:N}@test.local", Name = $"rmavail-{userId:N}" });
        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"rmavail-{teamId:N}", Name = "RM Avail Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return teamId;
    }
}
