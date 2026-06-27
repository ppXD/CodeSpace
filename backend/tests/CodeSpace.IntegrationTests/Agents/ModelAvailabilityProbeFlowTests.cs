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
/// 🟢 Integration (real Postgres + the REAL <see cref="ModelAvailabilityProbeService"/>, a FAKE <see cref="ILLMClient"/>
/// at the ping seam): the availability PRODUCER pings each ENABLED Custom-gateway pool model and records reachability —
/// a 2xx ⇒ available; a RESPONSE on any status (401 / 429 / 400) ⇒ available (reachable, the highest-value correctness
/// case); a no-response transport failure or ping timeout ⇒ unavailable; an unexpected fault leaves it unchanged. Vendor
/// rows + Custom-with-no-base-url rows are never pinged. Re-probes on staleness (recovery) and backs off within the window.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ModelAvailabilityProbeFlowTests
{
    private const string Custom = "Custom";
    private readonly PostgresFixture _fixture;

    public ModelAvailabilityProbeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_reachable_custom_gateway_is_marked_available()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId, Custom, baseUrl: "https://gw.local/v1");
        await AddModelAsync(cred, "metis-coder-max");

        var client = new ReachableClient(Custom);
        await ProbeAsync(teamId, client);

        client.Calls.ShouldBe(1, "one ping per Custom-gateway model");
        (await AvailableOf(teamId, "metis-coder-max")).ShouldBe(true);
        (await LastPingedAtOf(teamId, "metis-coder-max")).ShouldNotBeNull("the staleness gate is stamped");
    }

    [Fact]
    public async Task A_no_response_transport_failure_is_marked_unavailable()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId, Custom, baseUrl: "https://dead.local/v1");
        await AddModelAsync(cred, "metis-coder-max");

        // A connection-refused / reset / DNS failure surfaces as LlmApiException with StatusCode == null.
        await ProbeAsync(teamId, new DeadClient(Custom));

        (await AvailableOf(teamId, "metis-coder-max")).ShouldBe(false, "a no-response transport failure (null status) means the gateway is unreachable");
    }

    [Theory]
    [InlineData(401, LlmErrorCategory.AuthFailed)]
    [InlineData(429, LlmErrorCategory.RateLimited)]
    [InlineData(400, LlmErrorCategory.BadRequest)]
    [InlineData(500, LlmErrorCategory.Transient)]
    public async Task A_responding_endpoint_is_available_whatever_the_status(int status, LlmErrorCategory category)
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId, Custom, baseUrl: "https://gw.local/v1");
        await AddModelAsync(cred, "metis-coder-max");

        // The endpoint RESPONDED (any HTTP status) ⇒ reachable. Availability must NOT conflate auth / quota / shape
        // errors with deadness — else a busy (429) or mis-keyed (401) gateway is wrongly pulled from rotation.
        await ProbeAsync(teamId, new RespondingClient(Custom, status, category));

        (await AvailableOf(teamId, "metis-coder-max")).ShouldBe(true,
            $"HTTP {status} ({category}) means the gateway RESPONDED — reachable, even if the call itself failed for another reason");
    }

    [Fact]
    public async Task A_ping_timeout_is_marked_unavailable()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId, Custom, baseUrl: "https://hang.local/v1");
        await AddModelAsync(cred, "metis-coder-max");

        // A half-open gateway that accepts the socket then hangs: the ping's linked timeout fires → OperationCanceledException
        // while the JOB token is NOT cancelled → unreachable.
        await ProbeAsync(teamId, new TimeoutClient(Custom));

        (await AvailableOf(teamId, "metis-coder-max")).ShouldBe(false, "a ping that times out without a response is unreachable");
    }

    [Fact]
    public async Task An_unexpected_fault_leaves_availability_unchanged_but_stamps_the_attempt()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId, Custom, baseUrl: "https://gw.local/v1");
        await AddModelAsync(cred, "metis-coder-max");

        // A raw, non-typed exception is a BUG, not a verdict about the endpoint — leave availability null, but stamp the
        // attempt so the back-off applies and we don't hammer it every tick.
        await ProbeAsync(teamId, new BugClient(Custom));

        (await AvailableOf(teamId, "metis-coder-max")).ShouldBeNull("an unexpected fault is not evidence of deadness");
        (await LastPingedAtOf(teamId, "metis-coder-max")).ShouldNotBeNull("but the attempt is stamped for back-off");
    }

    [Fact]
    public async Task A_vendor_model_is_never_pinged()
    {
        var teamId = await SeedTeamAsync();
        // A vendor cred (not "Custom") even WITH a base url (an OpenAI-proxied gateway) is trusted, never pinged.
        var cred = await SeedCredentialAsync(teamId, "OpenAI", baseUrl: "https://openrouter.ai/api/v1");
        await AddModelAsync(cred, "gpt-5.4-codex");

        var client = new ReachableClient(Custom);
        await ProbeAsync(teamId, client);

        client.Calls.ShouldBe(0, "vendor models are trusted, never pinged");
        (await AvailableOf(teamId, "gpt-5.4-codex")).ShouldBeNull("a vendor model stays NULL = assumed available");
    }

    [Fact]
    public async Task A_custom_model_with_no_base_url_is_never_pinged()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId, Custom, baseUrl: null);
        await AddModelAsync(cred, "metis-coder-max");

        var client = new ReachableClient(Custom);
        await ProbeAsync(teamId, client);

        client.Calls.ShouldBe(0, "a Custom row with no base url has no gateway to ping (pinging would fall back to api.openai.com)");
        (await AvailableOf(teamId, "metis-coder-max")).ShouldBeNull();
    }

    [Fact]
    public async Task A_disabled_custom_model_is_never_pinged()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId, Custom, baseUrl: "https://gw.local/v1");
        await AddModelAsync(cred, "metis-coder-max", enabled: false);

        var client = new ReachableClient(Custom);
        await ProbeAsync(teamId, client);

        client.Calls.ShouldBe(0, "a disabled row is not part of the usable pool");
        (await AvailableOf(teamId, "metis-coder-max")).ShouldBeNull();
    }

    [Fact]
    public async Task A_stale_unavailable_model_is_re_probed_and_recovers()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId, Custom, baseUrl: "https://gw.local/v1");
        await AddModelAsync(cred, "metis-coder-max");

        await ProbeAsync(teamId, new DeadClient(Custom));
        (await AvailableOf(teamId, "metis-coder-max")).ShouldBe(false);

        // Within the back-off window a fresh false is NOT re-probed…
        var fresh = new ReachableClient(Custom);
        await ProbeAsync(teamId, fresh);
        fresh.Calls.ShouldBe(0, "a freshly-probed row is within the back-off window — no re-ping");
        (await AvailableOf(teamId, "metis-coder-max")).ShouldBe(false);

        // …but once stale, the recovered gateway re-probes back to available.
        await MakeStaleAsync(teamId, "metis-coder-max");
        var recovered = new ReachableClient(Custom);
        await ProbeAsync(teamId, recovered);
        recovered.Calls.ShouldBe(1, "a stale row is re-probed after the back-off window — availability re-evaluates (unlike the write-once tier)");
        (await AvailableOf(teamId, "metis-coder-max")).ShouldBe(true);
    }

    // ─── Helpers ───

    private async Task ProbeAsync(Guid teamId, ILLMClient client)
    {
        using var scope = _fixture.BeginScope();
        var service = new ModelAvailabilityProbeService(new FakeClients(client), scope.Resolve<IPayloadEncryptor>(), scope.Resolve<CodeSpaceDbContext>(), NullLogger<ModelAvailabilityProbeService>.Instance);
        await service.ProbeTeamAsync(teamId, CancellationToken.None);
    }

    private async Task<bool?> AvailableOf(Guid teamId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ModelCredentialModel.AsNoTracking()
            .Where(m => m.Credential.TeamId == teamId && m.ModelId == modelId).Select(m => m.Available).SingleAsync();
    }

    private async Task<DateTimeOffset?> LastPingedAtOf(Guid teamId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ModelCredentialModel.AsNoTracking()
            .Where(m => m.Credential.TeamId == teamId && m.ModelId == modelId).Select(m => m.LastPingedAt).SingleAsync();
    }

    private async Task MakeStaleAsync(Guid teamId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var row = await db.ModelCredentialModel.SingleAsync(m => m.Credential.TeamId == teamId && m.ModelId == modelId);
        row.LastPingedAt = DateTimeOffset.UtcNow.AddHours(-1);   // past the 30-minute window
        await db.SaveChangesAsync();
    }

    private async Task AddModelAsync(Guid credId, string modelId, bool enabled = true)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = Guid.NewGuid(), ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = enabled });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, string provider, string? baseUrl)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = id, TeamId = teamId, Provider = provider, DisplayName = provider + " cred",
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt("sk-team"), BaseUrl = baseUrl, Status = CredentialStatus.Active,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"avail-{userId:N}@test.local", Name = $"avail-{userId:N}" });
        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"avail-{teamId:N}", Name = "Avail Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return teamId;
    }

    // ─── Fakes (the ping seam only — the resolve + persistence seams are REAL) ───

    private sealed class FakeClients : ILLMClientRegistry
    {
        public FakeClients(ILLMClient client) => All = new[] { client };
        public IReadOnlyList<ILLMClient> All { get; }
        public ILLMClient Resolve(string provider) => All.First();
    }

    private sealed class ReachableClient : ILLMClient
    {
        public ReachableClient(string provider) => Provider = provider;
        public string Provider { get; }
        public int Calls { get; private set; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LLMCompletion { Text = "ok", Model = request.Model });
        }
    }

    private sealed class DeadClient : ILLMClient
    {
        public DeadClient(string provider) => Provider = provider;
        public string Provider { get; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) =>
            throw new LlmApiException(Provider, null, LlmErrorCategory.Transient, "connection refused");
    }

    private sealed class RespondingClient : ILLMClient
    {
        private readonly int _status;
        private readonly LlmErrorCategory _category;
        public RespondingClient(string provider, int status, LlmErrorCategory category) { Provider = provider; _status = status; _category = category; }
        public string Provider { get; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) =>
            throw new LlmApiException(Provider, _status, _category, "the gateway responded with an error");
    }

    private sealed class TimeoutClient : ILLMClient
    {
        public TimeoutClient(string provider) => Provider = provider;
        public string Provider { get; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) =>
            throw new OperationCanceledException();   // the ping's linked timeout fired (the job token is not cancelled)
    }

    private sealed class BugClient : ILLMClient
    {
        public BugClient(string provider) => Provider = provider;
        public string Provider { get; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("an unexpected bug, not an endpoint verdict");
    }
}
