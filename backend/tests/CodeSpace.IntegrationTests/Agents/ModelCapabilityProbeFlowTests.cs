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
/// 🟢 Integration (real Postgres + the REAL <see cref="ModelCapabilityProbeService"/>, a SCRIPTED <see cref="ILLMClient"/>
/// at the battery seam): the opaque-id capability PROBE runs the battery on ONLY opaque (capability_tier='Unknown') rows,
/// maps the score to a coarse {Basic, Strong} tier (never Frontier), writes it as a MONOTONIC upgrade, leaves a garbage /
/// unreachable model un-verdicted (re-probing on staleness), and backs off via last_probed_capability_at. The brain
/// verdict (capability_tier) is never touched.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ModelCapabilityProbeFlowTests
{
    private const string Provider = "Anthropic";
    private readonly PostgresFixture _fixture;

    public ModelCapabilityProbeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task It_probes_an_opaque_model_to_Strong()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId);
        await AddModelAsync(cred, "metis-coder-max", capabilityTier: ModelCapabilityTier.Unknown);

        var client = Responder(easyCorrect: true, hardCorrect: true);
        await ProbeAsync(teamId, client);

        (await ProbedTierOf(teamId, "metis-coder-max")).ShouldBe(ModelCapabilityTier.Strong, "a model that clears both bands is probed Strong");
        client.Calls.ShouldBe(ModelCapabilityProbeBattery.Tasks.Count, "one battery run for the opaque row");
        (await LastProbedAtOf(teamId, "metis-coder-max")).ShouldNotBeNull("the staleness gate is stamped");
    }

    [Fact]
    public async Task It_probes_an_easy_only_model_to_Basic()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId);
        await AddModelAsync(cred, "weak-alias", capabilityTier: ModelCapabilityTier.Unknown);

        await ProbeAsync(teamId, Responder(easyCorrect: true, hardCorrect: false));

        (await ProbedTierOf(teamId, "weak-alias")).ShouldBe(ModelCapabilityTier.Basic, "clearing only the easy band is Basic, not Strong");
    }

    [Fact]
    public async Task A_garbage_model_gets_no_verdict_but_stamps_the_attempt()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId);
        await AddModelAsync(cred, "garbage-alias", capabilityTier: ModelCapabilityTier.Unknown);

        await ProbeAsync(teamId, Responder(easyCorrect: false, hardCorrect: false));

        (await ProbedTierOf(teamId, "garbage-alias")).ShouldBeNull("a model that fails the battery is never promoted — it stays Unknown");
        (await LastProbedAtOf(teamId, "garbage-alias")).ShouldNotBeNull("but the attempt is stamped for back-off");
    }

    [Fact]
    public async Task Only_opaque_unknown_rows_are_probed()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId);
        await AddModelAsync(cred, "claude-opus-4-8", capabilityTier: ModelCapabilityTier.Frontier);   // brain-tiered — not opaque
        await AddModelAsync(cred, "never-tiered");                                                     // capability_tier NULL — tiering's job
        await AddModelAsync(cred, "metis-coder-max", capabilityTier: ModelCapabilityTier.Unknown);    // opaque → probed

        var client = Responder(easyCorrect: true, hardCorrect: true);
        await ProbeAsync(teamId, client);

        (await ProbedTierOf(teamId, "claude-opus-4-8")).ShouldBeNull("a brain-tiered row is never re-probed");
        (await ProbedTierOf(teamId, "never-tiered")).ShouldBeNull("a never-tiered (NULL) row belongs to the tiering producer, not the probe");
        (await ProbedTierOf(teamId, "metis-coder-max")).ShouldBe(ModelCapabilityTier.Strong);
        client.Calls.ShouldBe(ModelCapabilityProbeBattery.Tasks.Count, "exactly one battery run — only the single opaque row");
    }

    [Fact]
    public async Task A_re_probe_never_downgrades_a_higher_verdict()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId);
        await AddModelAsync(cred, "metis-coder-max", capabilityTier: ModelCapabilityTier.Unknown);

        await ProbeAsync(teamId, Responder(true, true));
        (await ProbedTierOf(teamId, "metis-coder-max")).ShouldBe(ModelCapabilityTier.Strong);

        await MakeStaleAsync(teamId, "metis-coder-max");
        await ProbeAsync(teamId, Responder(true, false));   // a later, weaker (Basic-scoring) run

        (await ProbedTierOf(teamId, "metis-coder-max")).ShouldBe(ModelCapabilityTier.Strong, "monotonic — a later Basic-scoring run never downgrades a Strong verdict");
    }

    [Fact]
    public async Task A_fresh_probe_backs_off_and_re_runs_only_when_stale()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId);
        await AddModelAsync(cred, "metis-coder-max", capabilityTier: ModelCapabilityTier.Unknown);

        var first = Responder(true, true);
        await ProbeAsync(teamId, first);
        first.Calls.ShouldBe(ModelCapabilityProbeBattery.Tasks.Count);

        var fresh = Responder(true, true);
        await ProbeAsync(teamId, fresh);
        fresh.Calls.ShouldBe(0, "within the days-long back-off window — not re-probed");

        await MakeStaleAsync(teamId, "metis-coder-max");
        var stale = Responder(true, true);
        await ProbeAsync(teamId, stale);
        stale.Calls.ShouldBe(ModelCapabilityProbeBattery.Tasks.Count, "stale → re-probed");
    }

    [Fact]
    public async Task An_unreachable_model_gets_no_verdict_then_tiers_once_reachable()
    {
        var teamId = await SeedTeamAsync();
        var cred = await SeedCredentialAsync(teamId);
        await AddModelAsync(cred, "metis-coder-max", capabilityTier: ModelCapabilityTier.Unknown);

        await ProbeAsync(teamId, new DeadClient(Provider));   // every battery call is a transport failure
        (await ProbedTierOf(teamId, "metis-coder-max")).ShouldBeNull("no usable response → no verdict (infra, not a capability fail)");

        await MakeStaleAsync(teamId, "metis-coder-max");
        await ProbeAsync(teamId, Responder(true, true));
        (await ProbedTierOf(teamId, "metis-coder-max")).ShouldBe(ModelCapabilityTier.Strong, "once reachable, the battery runs and tiers it");
    }

    // ─── Helpers ───

    private async Task ProbeAsync(Guid teamId, ILLMClient client)
    {
        using var scope = _fixture.BeginScope();
        var service = new ModelCapabilityProbeService(new FakeClients(client), scope.Resolve<IPayloadEncryptor>(), scope.Resolve<CodeSpaceDbContext>(), NullLogger<ModelCapabilityProbeService>.Instance);
        await service.ProbeTeamAsync(teamId, CancellationToken.None);
    }

    private async Task<ModelCapabilityTier?> ProbedTierOf(Guid teamId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ModelCredentialModel.AsNoTracking()
            .Where(m => m.Credential.TeamId == teamId && m.ModelId == modelId).Select(m => m.ProbedCapabilityTier).SingleAsync();
    }

    private async Task<DateTimeOffset?> LastProbedAtOf(Guid teamId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ModelCredentialModel.AsNoTracking()
            .Where(m => m.Credential.TeamId == teamId && m.ModelId == modelId).Select(m => m.LastProbedCapabilityAt).SingleAsync();
    }

    private async Task MakeStaleAsync(Guid teamId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var row = await db.ModelCredentialModel.SingleAsync(m => m.Credential.TeamId == teamId && m.ModelId == modelId);
        row.LastProbedCapabilityAt = DateTimeOffset.UtcNow.AddDays(-8);   // past the 7-day window
        await db.SaveChangesAsync();
    }

    private async Task AddModelAsync(Guid credId, string modelId, ModelCapabilityTier? capabilityTier = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = Guid.NewGuid(), ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true, CapabilityTier = capabilityTier });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = id, TeamId = teamId, Provider = Provider, DisplayName = Provider + " cred",
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
        db.User.Add(new User { Id = userId, Email = $"probe-{userId:N}@test.local", Name = $"probe-{userId:N}" });
        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"probe-{teamId:N}", Name = "Probe Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        return teamId;
    }

    // ─── Fakes (the battery seam only — resolve + persistence are REAL) ───

    /// <summary>A client that answers each battery task correctly or with garbage per its band — so a test can synthesise a Strong / Basic / garbage model deterministically.</summary>
    private static ScriptedClient Responder(bool easyCorrect, bool hardCorrect) => new(Provider, prompt =>
    {
        var task = ModelCapabilityProbeBattery.Tasks.First(t => t.Prompt == prompt);
        var giveCorrect = task.Band == ProbeBand.Easy ? easyCorrect : hardCorrect;
        return giveCorrect ? CorrectAnswers[prompt] : "I cannot help with that.";
    });

    private static readonly IReadOnlyDictionary<string, string> CorrectAnswers = BuildCorrectAnswers();

    private static Dictionary<string, string> BuildCorrectAnswers()
    {
        var correct = new[] { "12", "21", "maerts", "{\"sum\": 13, \"product\": 42}", "40", "2,3,5" };
        var tasks = ModelCapabilityProbeBattery.Tasks;
        var map = new Dictionary<string, string>();
        for (var i = 0; i < tasks.Count; i++) map[tasks[i].Prompt] = correct[i];
        return map;
    }

    private sealed class FakeClients : ILLMClientRegistry
    {
        public FakeClients(ILLMClient client) => All = new[] { client };
        public IReadOnlyList<ILLMClient> All { get; }
        public ILLMClient Resolve(string provider) => All.First();
    }

    private sealed class ScriptedClient : ILLMClient
    {
        private readonly Func<string, string> _answer;
        public ScriptedClient(string provider, Func<string, string> answer) { Provider = provider; _answer = answer; }
        public string Provider { get; }
        public int Calls { get; private set; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LLMCompletion { Text = _answer(request.UserPrompt), Model = request.Model });
        }
    }

    private sealed class DeadClient : ILLMClient
    {
        public DeadClient(string provider) => Provider = provider;
        public string Provider { get; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) =>
            throw new LlmApiException(Provider, null, LlmErrorCategory.Transient, "connection refused");
    }
}
