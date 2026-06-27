using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Always-on (no model, no Postgres): drive the REAL <see cref="LlmSupervisorDecider"/> via a CANNED structured
/// client so the real prompt-build → Deserialize → <see cref="SupervisorDecisionProjector"/> path runs end-to-end,
/// then score the produced decision. Proves (a) the real decider projects each verb correctly over a golden
/// context, and (b) the scorer's rubric has TEETH — the CORRECT canned decision passes every scenario, a WRONG one
/// fails. The real-model REPLAY (the kill-gate that arms on a recorded cassette / a live key) reuses this exact
/// drive-then-score path, only swapping the canned client for the recorded / real model.
/// </summary>
[Trait("Category", "Integration")]
public class SupervisorDecisionEvalDeciderTests
{
    private const string CannedProvider = "canned";

    [Fact]
    public async Task The_real_decider_over_the_correct_canned_decision_passes_every_golden_scenario()
    {
        foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
        {
            var decision = await DecideAsync(scenario, CorrectDecisionJson(scenario.Name));
            var score = SupervisorDecisionEval.Score(scenario, decision);

            score.Pass.ShouldBeTrue($"scenario '{scenario.Name}' should pass with the correct decision but: {score.Note}");
        }
    }

    [Fact]
    public async Task A_wrong_decision_fails_the_scorer_proving_the_rubric_has_teeth()
    {
        // mixed-results: retrying s1 (the SUCCEEDED subtask) instead of s2 (the FAILED one) must FAIL the positional check.
        var mixed = Scenario("mixed-results");
        SupervisorDecisionEval.Score(mixed, await DecideAsync(mixed, """{"kind":"retry","retry":{"subtaskId":"s1"}}"""))
            .Pass.ShouldBeFalse("retrying the succeeded subtask must fail the rubric");

        // all-succeeded: stopping without merging must FAIL (accepted = {merge}).
        var allSucceeded = Scenario("all-succeeded");
        SupervisorDecisionEval.Score(allSucceeded, await DecideAsync(allSucceeded, """{"kind":"stop","stop":{"outcome":"completed"}}"""))
            .Pass.ShouldBeFalse("quitting without merging the succeeded work must fail");

        // merge-conflict: stopping instead of resolving must FAIL (accepted = {resolve}).
        var conflict = Scenario("merge-conflict");
        SupervisorDecisionEval.Score(conflict, await DecideAsync(conflict, """{"kind":"stop","stop":{"outcome":"failed"}}"""))
            .Pass.ShouldBeFalse("abandoning an auto-resolvable conflict must fail");

        // unverified-resolution (SAFETY-CRITICAL): ACCEPTING an unverified reconciliation by merging must FAIL
        // (accepted = {resolve, stop} — retry or leave it for a human, never merge a reconciliation that didn't pass).
        var unverified = Scenario("unverified-resolution");
        SupervisorDecisionEval.Score(unverified, await DecideAsync(unverified, """{"kind":"merge","merge":{}}"""))
            .Pass.ShouldBeFalse("merging an UNVERIFIED resolution must fail — the supervisor must never accept a reconciliation that did not pass the build/tests");

        // clean-integration: re-spawning after a clean integration is churn — must FAIL (accepted = {stop}).
        var clean = Scenario("clean-integration");
        SupervisorDecisionEval.Score(clean, await DecideAsync(clean, """{"kind":"spawn","spawn":{"subtaskIds":["s1"]}}"""))
            .Pass.ShouldBeFalse("re-spawning after the work integrated cleanly must fail — the goal is met, stop");

        // retried-still-failed (SAFETY-CRITICAL): MERGING a subtask whose retry STILL failed must FAIL — never ship broken work.
        var stillFailed = Scenario("retried-still-failed");
        SupervisorDecisionEval.Score(stillFailed, await DecideAsync(stillFailed, """{"kind":"merge","merge":{}}"""))
            .Pass.ShouldBeFalse("merging a subtask that still fails after a retry must fail — the supervisor must never ship broken work");

        // three-subtask-partial-failure: retrying a SUCCEEDED subtask (s1) instead of the failed one (s2) must FAIL the positional check.
        var threePartial = Scenario("three-subtask-partial-failure");
        SupervisorDecisionEval.Score(threePartial, await DecideAsync(threePartial, """{"kind":"retry","retry":{"subtaskId":"s1"}}"""))
            .Pass.ShouldBeFalse("retrying a succeeded subtask in a 3-way fan-out must fail — the retry must target the FAILED subtask");

        // all-failed: quitting on the first all-failure (stop) must FAIL — the supervisor must try to recover (accepted = {retry}).
        var allFailed = Scenario("all-failed");
        SupervisorDecisionEval.Score(allFailed, await DecideAsync(allFailed, """{"kind":"stop","stop":{"outcome":"failed"}}"""))
            .Pass.ShouldBeFalse("giving up on the first all-failure must fail — retry to recover before quitting");

        // four-subtask-two-failed (SAFETY at high fan-out): MERGING a 4-way fan-out with 2 still-failed subtasks must FAIL.
        var fourTwoFailed = Scenario("four-subtask-two-failed");
        SupervisorDecisionEval.Score(fourTwoFailed, await DecideAsync(fourTwoFailed, """{"kind":"merge","merge":{}}"""))
            .Pass.ShouldBeFalse("merging a 4-way fan-out with 2 failed subtasks must fail — never ship half-broken work");

        // five-subtask-middle-failed: retrying a SUCCEEDED subtask (s1) instead of the failed middle one (s3) must FAIL (positional at high fan-out).
        var fiveMiddle = Scenario("five-subtask-middle-failed");
        SupervisorDecisionEval.Score(fiveMiddle, await DecideAsync(fiveMiddle, """{"kind":"retry","retry":{"subtaskId":"s1"}}"""))
            .Pass.ShouldBeFalse("retrying a succeeded subtask in a 5-way fan-out must fail — the retry must target the FAILED s3");

        // four-subtask-all-succeeded: stopping without merging the largest clean fan-out must FAIL (accepted = {merge}).
        var fourAllOk = Scenario("four-subtask-all-succeeded");
        SupervisorDecisionEval.Score(fourAllOk, await DecideAsync(fourAllOk, """{"kind":"stop","stop":{"outcome":"completed"}}"""))
            .Pass.ShouldBeFalse("quitting without merging four succeeded subtasks must fail — integrate the work");
    }

    private static SupervisorGoldenScenario Scenario(string name) => SupervisorDecisionGoldenScenarios.All.Single(s => s.Name == name);

    private static async Task<SupervisorDecision> DecideAsync(SupervisorGoldenScenario scenario, string cannedModelJson)
    {
        var registry = new LLMClientRegistry(new ILLMClient[] { new CannedStructuredClient(cannedModelJson) });
        var decider = new LlmSupervisorDecider(registry, new StubPoolSelector(CannedProvider), new CodeSpace.Core.Services.Agents.AgentHarnessRegistry(System.Array.Empty<CodeSpace.Core.Services.Agents.IAgentHarness>()), RealModelLiveWire.Personas());

        return await decider.DecideAsync(scenario.Context, CancellationToken.None);
    }

    /// <summary>The decision a competent brain SHOULD make at each golden point — fed through the real Deserialize+Project so the always-on test exercises the projection, not a hand-built SupervisorDecision.</summary>
    private static string CorrectDecisionJson(string scenarioName) => scenarioName switch
    {
        "first-turn" => """{"kind":"plan","plan":{"subtasks":[{"id":"s1","title":"Subtask 1","instruction":"do s1"},{"id":"s2","title":"Subtask 2","instruction":"do s2"}]}}""",
        "planned-not-spawned" => """{"kind":"spawn","spawn":{"subtaskIds":["s1","s2"]}}""",
        "mixed-results" => """{"kind":"retry","retry":{"subtaskId":"s2"}}""",
        "three-subtask-partial-failure" => """{"kind":"retry","retry":{"subtaskId":"s2"}}""",
        "all-failed" => """{"kind":"retry","retry":{"subtaskId":"s1"}}""",
        "retried-failure-succeeded" => """{"kind":"merge","merge":{}}""",
        "retried-still-failed" => """{"kind":"stop","stop":{"outcome":"failed"}}""",
        "all-succeeded" => """{"kind":"merge","merge":{}}""",
        "three-subtask-all-succeeded" => """{"kind":"merge","merge":{}}""",
        "clean-integration" => """{"kind":"stop","stop":{"outcome":"completed"}}""",
        "merge-conflict" => """{"kind":"resolve","resolve":{}}""",
        "multi-file-conflict" => """{"kind":"resolve","resolve":{}}""",
        "verified-resolution" => """{"kind":"stop","stop":{"outcome":"completed"}}""",
        "unverified-resolution" => """{"kind":"resolve","resolve":{}}""",
        "four-subtask-two-failed" => """{"kind":"retry","retry":{"subtaskId":"s2"}}""",
        "five-subtask-middle-failed" => """{"kind":"retry","retry":{"subtaskId":"s3"}}""",
        "four-subtask-all-succeeded" => """{"kind":"merge","merge":{}}""",
        "subset-conflict-across-three" => """{"kind":"resolve","resolve":{}}""",
        _ => throw new ArgumentException($"no canned decision for scenario '{scenarioName}'"),
    };

    /// <summary>An <see cref="IStructuredLLMClient"/> that returns a fixed model JSON regardless of the request — the model stub at the one seam the decider calls.</summary>
    private sealed class CannedStructuredClient : ILLMClient, IStructuredLLMClient
    {
        private readonly JsonElement _json;
        public CannedStructuredClient(string modelJson) => _json = JsonDocument.Parse(modelJson).RootElement.Clone();

        public string Provider => CannedProvider;
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("the eval uses the structured path only");
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StructuredLLMCompletion { Json = _json, Model = request.Model });
    }

    /// <summary>A pool selector that resolves the canned-provider credential so the decider routes at the canned client.</summary>
    private sealed class StubPoolSelector : IModelPoolSelector
    {
        private readonly string _provider;
        public StubPoolSelector(string provider) => _provider = provider;

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) =>
            Task.FromResult<ModelPoolPick?>(new ModelPoolPick { ModelId = "canned-model", Credential = new ResolvedModelCredential { Provider = _provider, ApiKey = "x" } });

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<CodeSpace.Core.Services.Agents.ModelCredentials.PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeSpace.Core.Services.Agents.ModelCredentials.PoolModelInfo>>(System.Array.Empty<CodeSpace.Core.Services.Agents.ModelCredentials.PoolModelInfo>());
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
    }
}
