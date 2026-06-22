using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.Messages.Agents;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// THE real-model TRAJECTORY gate — the multi-turn complement to the single-decision kill-gate. It drives the REAL
/// <see cref="LlmSupervisorDecider"/> against a REAL endpoint turn BY turn over the happy-path environment, and
/// asserts the live brain DRIVES TO COMPLETION and STOPS AFTER SHIPPING (plan → spawn → merge → stop), rather than
/// looping or quitting empty — the property a single decision cannot prove. Honestly gated on the <c>CODESPACE_LLM_*</c>
/// secrets (absent → skip, so CI/forks stay green); a <see cref="Theory"/> over both wires. The environment + scorer
/// are pinned always-on by <c>SupervisorTrajectoryEvalTests</c>, so a failure here is the MODEL's trajectory
/// judgment, not a broken harness.
/// </summary>
[Trait("Category", "RealModel")]
public sealed class RealModelSupervisorTrajectoryFlowTests
{
    [Theory]
    [InlineData("Anthropic", "happy")]                  // plan → spawn → merge(clean) → stop
    [InlineData("Anthropic", "conflict")]               // plan → spawn → merge(CONFLICT) → resolve(verified) → stop
    [InlineData("Anthropic", "failure")]                // plan → spawn(1 FAILED) → retry → merge(clean) → stop
    [InlineData("Anthropic", "persistent-conflict")]    // the FIRST resolve is UNVERIFIED → must resolve AGAIN before shipping
    [InlineData("Anthropic", "multi-failure")]          // BOTH subtasks fail → must retry EACH before a clean merge
    [InlineData("OpenAI", "happy")]
    [InlineData("OpenAI", "conflict")]
    [InlineData("OpenAI", "failure")]
    [InlineData("OpenAI", "persistent-conflict")]
    [InlineData("OpenAI", "multi-failure")]
    public async Task The_real_model_drives_a_run_to_completion_and_stops_after_shipping(string provider, string scenario)
    {
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip (honest CI/fork behaviour)

        var credential = new ResolvedModelCredential { Provider = provider, BaseUrl = BaseUrlFor(provider, baseUrl), ApiKey = apiKey };
        var registry = new LLMClientRegistry(new ILLMClient[] { new AnthropicClient(SharedHttp), new OpenAiClient(SharedHttp) });
        var decider = new LlmSupervisorDecider(registry, new FixedCredentialSelector(model, credential));

        // The SUCCESS path proves convergence; the four RECOVERY paths prove the live brain handles a merge conflict,
        // an agent failure, a PERSISTENT conflict (it must NOT accept the first unverified resolution and must resolve
        // again), and a MULTI-failure (it must retry EACH failed subtask) — multi-turn properties a happy-path run
        // cannot exercise. The scorer reads the SAME real shippable head off the ledger across all five.
        var environment = scenario switch
        {
            "conflict" => SupervisorTrajectoryEnvironments.ConflictThenResolve,
            "failure" => SupervisorTrajectoryEnvironments.FailureThenRetry,
            "persistent-conflict" => SupervisorTrajectoryEnvironments.ConflictThenUnverifiedThenVerified,
            "multi-failure" => SupervisorTrajectoryEnvironments.MultiFailureThenRetry,
            _ => SupervisorTrajectoryEnvironments.HappyPath,
        };

        // A wall-clock deadline bounds the WHOLE trajectory independently of the per-call HTTP timeout, so a slow or
        // hanging endpoint surfaces the scorer's clean "did not converge" verdict instead of blowing the CI job's
        // wall-time budget. A sound run is 4-5 turns, so maxTurns:8 is ample headroom for a replan or an ask.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));

        var trajectory = await SupervisorTrajectory.RunAsync(decider, environment, maxTurns: 8, deadline.Token);

        var (ok, note) = SupervisorTrajectoryScore.Score(trajectory);
        RealModelGate.Assess(provider, ok, $"{provider} model '{model}' [{scenario}] trajectory — {note}");
    }

    private static string BaseUrlFor(string provider, string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        if (!string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase)) return b;
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b : b + "/v1";
    }

    private static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);

    private static readonly IHttpClientFactory SharedHttp = new SimpleHttpClientFactory();

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        // Generous PER-CALL timeout: this gateway is slow (~50-90s/turn with the progressive structured-output
        // double-attempt), so a tight per-call cap would abort a legitimately-slow turn and — worse — surface as an
        // OperationCanceledException that is NOT the trajectory deadline. The WHOLE-trajectory wall-clock bound is the
        // 6-min CancellationTokenSource at the call site; this per-call cap is only a backstop against a genuine hang.
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(150) };
    }

    private sealed class FixedCredentialSelector : IModelPoolSelector
    {
        private readonly string _model;
        private readonly ResolvedModelCredential _credential;
        public FixedCredentialSelector(string model, ResolvedModelCredential credential) { _model = model; _credential = credential; }

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, bool requireStructured, CancellationToken cancellationToken) =>
            Task.FromResult<ModelPoolPick?>(new ModelPoolPick { ModelId = _model, Credential = _credential });

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, bool requireStructured, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
