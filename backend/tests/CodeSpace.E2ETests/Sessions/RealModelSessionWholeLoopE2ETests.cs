using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Sessions;

/// <summary>
/// 🟢 THE real-model SESSION WHOLE-LOOP gate — the headline join the sessions audit found missing: a LIVE brain
/// continues a thread on a digest the PRODUCTION path actually built from a REAL first turn. Unlike the component eval
/// (<c>RealModelSessionContinueFlowTests</c>, which scores the decider over HAND-RENDERED golden digests), this runs a
/// REAL first turn through the production launch + durable engine + a fake CLI (so turn-1's summary is real and
/// persisted), then has the PRODUCTION <see cref="ISessionContextBuilder"/> build the digest from that persisted turn,
/// composes the continue goal with the real <see cref="AgentNodeMapping.ComposeGoal"/>, and drives the production
/// <see cref="LlmSupervisorDecider"/> over it — so a green verdict means the live brain continued a thread on the EXACT
/// digest a real continuing run would inject, end to end.
///
/// <para>The engine→digest→compose scaffold is asserted ALWAYS (so the harness is proven even when the model is
/// skipped); the live-brain decision is the only part gated on the <c>CODESPACE_LLM_*</c> secrets (absent → reported
/// skipped, so forks/local stay green at zero cost). A best-of-N capability-floor (a fresh decision per attempt)
/// keeps the blessed wire flake-safe. What is stubbed is the agent's CODING (the fake CLI's mechanical summary), so the
/// teeth are the WEAKER "proceeds-coherently + addresses-the-new-ask" signal — the sharp "don't redo shipped work" teeth
/// stays the rich-digest component eval; this gate's value is the PRODUCTION-PATH fidelity (real engine → real digest →
/// real brain), not a harder decision than the golden eval already measures.</para>
///
/// <para>Tier: 🟢 high-fidelity E2E (Surface=Engine) — real launch service + real <see cref="IWorkflowEngine"/> +
/// real executor + fake CLI over real Postgres, plus the live gateway brain. Skips on Windows (the fake CLI is a
/// /bin/sh script). Routed to the real-model CI lane by the <c>RealModel</c> trait, not the project.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelSessionWholeLoopE2ETests
{
    private const string Provider = "Anthropic";   // the blessed brain wire (RealModelGate gates it)

    private const string FirstGoal = "Add server-side email-format validation to the signup endpoint, rejecting malformed addresses with HTTP 400 and a clear message, with unit tests.";
    private const string FollowUp = "Now ALSO add per-IP rate limiting to the same signup endpoint: at most 5 requests per minute, return HTTP 429 when exceeded, with unit tests.";

    private readonly PostgresFixture _fixture;

    public RealModelSessionWholeLoopE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_real_model_continues_a_thread_on_a_production_built_digest_from_a_real_first_turn()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new SubtaskAwareFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // the agent.code suspend runs the REAL executor + runner + fake CLI

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // ── TURN 1: a REAL launch that executes to a real, persisted summary (real engine + executor + fake CLI). ──
        var first = await LaunchAsync(new TaskLaunchRequest
        {
            TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = FirstGoal, RequestedEffort = TaskEffortModes.Quick, Autonomy = "Confined",
            Overrides = new TaskExecutionOverrides { Harness = "codex-cli", RunnerKind = "local" },
        });

        await RunEngineAsync(first.RunId);
        await jobClient.WaitForPendingAsync();

        var firstRun = await LoadRunAsync(first.RunId);
        firstRun.Status.ShouldBe(WorkflowRunStatus.Success, "turn 1 must complete so its real summary exists to carry forward");

        var realSummary = SubtaskAwareFakeCli.ExpectedSummaryFor(FirstGoal);
        firstRun.OutputsJson.ShouldContain(realSummary, customMessage: "turn 1's real result is persisted on its OutputsJson — the source the production digest reads");

        // ── PRODUCTION DIGEST: the real SessionContextBuilder builds the continue digest from the persisted turn 1. ──
        var digest = await BuildContextAsync(first.SessionId, teamId);
        digest.ShouldNotBeNull("the production digest builder must produce a digest for a thread with a prior turn");
        digest!.ShouldContain(realSummary, customMessage: "the PRODUCTION digest carries turn 1's real summary forward — not a hand-rendered fixture");

        // ── REAL continue composition: stage turn 2 as a real CONTINUE launch and read the production-composed goal the
        //    agent would actually receive — the digest folded in by the real projection, end to end (no hand-built goal). ──
        var second = await LaunchAsync(new TaskLaunchRequest
        {
            TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = FollowUp, ContinueSessionId = first.SessionId, RequestedEffort = TaskEffortModes.Quick, Autonomy = "Confined",
            Overrides = new TaskExecutionOverrides { Harness = "codex-cli", RunnerKind = "local" },
        });
        await jobClient.WaitForPendingAsync();   // drain turn 2's auto-executed run so it can't leak into the shared queue

        var goal = await ReadAgentGoalAsync(second.RunId);
        goal.ShouldContain(realSummary, customMessage: "the real continue projection folded the prior-turn digest into turn 2's agent goal");
        goal.ShouldContain(FollowUp, customMessage: "the composed continue goal still carries the new follow-up ask");

        // Everything above is the ALWAYS-ON scaffold (engine → production digest → composition) — proven even when the
        // model is skipped below. Only the live-brain DECISION is gated on the secrets.
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — scaffold proven, no live brain)"); return; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip the blessed gate proving nothing.");

        var scenario = new SupervisorGoldenScenario
        {
            Name = "wholeloop-continue-incremental",
            Context = new SupervisorTurnContext { Goal = goal, TurnNumber = 0, PriorDecisions = Array.Empty<SupervisorPriorDecision>(), SupervisorModelId = SupervisorDecisionGoldenScenarios.BrainModelRowId },
            AcceptedKinds = new[] { SupervisorDecisionKinds.Plan },
            PayloadCheck = PlanAddresses("rate", "limit", "429", "throttl", "per-ip", "per ip"),
        };

        // The live brain must PLAN the new ask, building on the real prior turn — best-of-N flake floor on the blessed wire.
        await RealModelGate.AssessLiveBestOfNAsync(Provider, async () =>
        {
            var credential = new ResolvedModelCredential { Provider = Provider, BaseUrl = baseUrl!.TrimEnd('/'), ApiKey = apiKey! };
            var registry = new LLMClientRegistry(new ILLMClient[] { new Core.Services.Workflows.Llm.Anthropic.AnthropicClient(SharedHttp), new Core.Services.Workflows.Llm.OpenAi.OpenAiClient(SharedHttp) });
            var decider = new LlmSupervisorDecider(registry, new FixedCredentialSelector(model!, credential), new CodeSpace.Core.Services.Agents.AgentHarnessRegistry(System.Array.Empty<CodeSpace.Core.Services.Agents.IAgentHarness>()), new EmptyPersonaLibrary());

            var decision = await decider.DecideAsync(scenario.Context, CancellationToken.None);
            var score = SupervisorDecisionEval.Score(scenario, decision);

            return (score.Pass, $"{Provider} model '{model}' on a production-built continue digest → '{score.ActualKind}': {score.Note}");
        });
    }

    /// <summary>The plan must ADDRESS the new follow-up ask (proof the brain read past the prior-turn digest). Mirrors SessionContinueGoldenScenarios.PlanAddresses.</summary>
    private static Func<SupervisorDecision, (bool Ok, string Note)> PlanAddresses(params string[] anyOf) => decision =>
    {
        SupervisorPlanPayload? plan;
        try { plan = JsonSerializer.Deserialize<SupervisorPlanPayload>(decision.PayloadJson, AgentJson.Options); }
        catch (JsonException) { return (false, "plan payload did not deserialize"); }

        if (plan is null || plan.Subtasks.Count == 0) return (false, "plan had no subtasks");

        var text = string.Join(" ", plan.Subtasks.Select(s => $"{s.Title} {s.Instruction}")).ToLowerInvariant();

        return anyOf.Any(term => text.Contains(term.ToLowerInvariant(), StringComparison.Ordinal))
            ? (true, "ok")
            : (false, $"plan subtasks address none of [{string.Join(", ", anyOf)}] — the brain did not act on the new follow-up ask");
    };

    // ── Helpers (mirror WorkSessionContextFlowTests' real multi-turn pattern) ──────────────────────────────────────

    private async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskLaunchService>().LaunchAsync(request, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<string?> BuildContextAsync(Guid sessionId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISessionContextBuilder>().BuildAsync(sessionId, teamId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    /// <summary>Read the projected agent.code node's composed <c>goal</c> (the agent prompt) out of the run's frozen definition snapshot.</summary>
    private async Task<string> ReadAgentGoalAsync(Guid runId)
    {
        var run = await LoadRunAsync(runId);
        run.DefinitionSnapshotJson.ShouldNotBeNull("a launched task is a snapshot run with an inline frozen definition");

        var root = JsonDocument.Parse(run.DefinitionSnapshotJson!).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");
        return agent.GetProperty("config").GetProperty("goal").GetString()!;
    }

    private static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);

    private static readonly IHttpClientFactory SharedHttp = new SimpleHttpClientFactory();

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        // Matches the sibling real-model timeout: this gateway's progressive double-attempt runs ~50-90s/call.
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(150) };
    }

    /// <summary>A fixed-credential pool selector — the live decider resolves its model + key from this (the real-model wire); only the by-row-id resolve the decider uses is implemented. Mirrors the sibling real-model tests' selector.</summary>
    private sealed class FixedCredentialSelector : IModelPoolSelector
    {
        private readonly string _model;
        private readonly ResolvedModelCredential _credential;
        public FixedCredentialSelector(string model, ResolvedModelCredential credential) { _model = model; _credential = credential; }

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) =>
            Task.FromResult<ModelPoolPick?>(new ModelPoolPick { ModelId = _model, Credential = _credential });

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<CodeSpace.Core.Services.Agents.ModelCredentials.PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSpace.Core.Services.Agents.ModelCredentials.PoolModelInfo>>(System.Array.Empty<CodeSpace.Core.Services.Agents.ModelCredentials.PoolModelInfo>());
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    /// <summary>An empty persona library — the decider lists it to render the persona pool; this real-model session gate doesn't exercise per-agent personas.</summary>
    private sealed class EmptyPersonaLibrary : CodeSpace.Core.Services.Agents.IAgentDefinitionService
    {
        public Task<IReadOnlyList<CodeSpace.Messages.Dtos.Agents.AgentDefinitionSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CodeSpace.Messages.Dtos.Agents.AgentDefinitionSummary>>(System.Array.Empty<CodeSpace.Messages.Dtos.Agents.AgentDefinitionSummary>());

        public Task<CodeSpace.Messages.Dtos.Agents.AgentDefinitionSummary?> GetAsync(Guid teamId, Guid agentDefinitionId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<Guid> CreateAsync(Guid teamId, CodeSpace.Messages.Agents.AgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task UpdateAsync(Guid teamId, Guid agentDefinitionId, CodeSpace.Messages.Agents.AgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<Guid> ImportAsync(Guid teamId, CodeSpace.Messages.Agents.ImportedAgentDefinitionInput input, Guid actorUserId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task DeleteAsync(Guid teamId, Guid agentDefinitionId, Guid actorUserId, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
