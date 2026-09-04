using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Llm;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Tasks.Recipes.MapFanout;
using CodeSpace.Core.Services.Tasks.Recipes.SingleAgent;
using CodeSpace.Core.Services.Tasks.Recipes.Supervisor;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// The real-model gate for the P4 structured-LLM EFFORT classifier (measure-the-intelligence): drives the REAL
/// <see cref="LlmEffortClassifier"/> against a REAL endpoint and proves the live model turns an "Auto" task into a real
/// effort decision — an OBVIOUSLY risky, clearly-scoped task is classified risky (→ deep) with a confidence that CLEARS
/// the confirm floor, so it routes WITHOUT a human confirm (the P4 win over the always-confirm heuristic). A
/// <see cref="Theory"/> over both wires from one set of secrets; HONESTLY GATED — early-returns when the
/// <c>CODESPACE_LLM_*</c> secrets are absent (CI/forks stay green), activating only where they are bound. No cassette,
/// no Postgres — it constructs the real client directly, so each run is a fresh measurement of classification quality.
/// </summary>
[Trait("Category", "RealModel")]
public sealed class RealModelEffortClassifierFlowTests
{
    [SkippableTheory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task The_real_model_classifies_an_obviously_risky_task_as_deep_above_the_confirm_floor(string provider)
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) throw RealModelGate.ReportSkipped(provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass: NotExecuted in the trx, never a green that measured nothing

        await RealModelGate.AssessLiveAsync(provider, async () =>
        {
            var credential = RealModelLiveWire.Credential(provider, baseUrl, apiKey);
            var recipes = new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new MapFanoutRecipe(), new SupervisorRecipe() });
            var classifier = new LlmEffortClassifier(RealModelLiveWire.Registry(), RealModelLiveWire.Selector(model, credential), recipes, new HeuristicEffortClassifier());

            var request = new EffortRouteRequest
            {
                Seed = new TaskLaunchSeed { Goal = "Run the production database migration that drops the legacy users table and deploy it to prod", SurfaceKind = "test", TeamId = Guid.NewGuid() },
            };

            var decision = await classifier.ClassifyAsync(request, CancellationToken.None);

            if (decision.ClassifierKind != LlmEffortClassifier.ClassifierKind)
                return (false, $"{provider} model '{model}' fell back to the heuristic — the live structured classifier produced no usable decision");

            var ok = decision.Signals.RiskySideEffects
                     && decision.SuggestedEffort == TaskEffortModes.Deep
                     && decision.Confidence >= EffortPolicy.ConfirmConfidenceFloor;

            return (ok, $"{provider} model '{model}' classified a prod drop-table+deploy task as '{decision.SuggestedEffort}' @ confidence {decision.Confidence:0.00} (risky={decision.Signals.RiskySideEffects}). Expected risky → deep @ >= {EffortPolicy.ConfirmConfidenceFloor}.");
        });
    }

    /// <summary>
    /// B2's live measurement: the SHAPE axis, not the tier. Three unmistakable asks — a question, a written document,
    /// a code change — each with the launch context the router now supplies (a question and a report carry no
    /// repository; the code change does). Coarse on purpose: only the shape is asserted, and the two non-code shapes
    /// pass on EITHER non-code reading, because "answer vs research" is a judgement call the downstream projection
    /// treats identically (both run research-mode + a judged contract) while "code" is the reading that would put a
    /// question back on the coding lane — the exact defect this axis exists to prevent.
    /// </summary>
    [SkippableTheory]
    [InlineData("Anthropic", "Explain how our retry-with-backoff loop decides to give up", false, "answer-or-research")]
    [InlineData("OpenAI", "Explain how our retry-with-backoff loop decides to give up", false, "answer-or-research")]
    [InlineData("Anthropic", "Write a design document proposing how we should shard the events table", false, DeliverableShapes.Document)]
    [InlineData("OpenAI", "Write a design document proposing how we should shard the events table", false, DeliverableShapes.Document)]
    [InlineData("Anthropic", "Fix the off-by-one in the pagination cursor and add a regression test", true, DeliverableShapes.Code)]
    [InlineData("OpenAI", "Fix the off-by-one in the pagination cursor and add a regression test", true, DeliverableShapes.Code)]
    public async Task The_real_model_reads_the_deliverable_shape_out_of_the_ask(string provider, string goal, bool withRepository, string expectedShape)
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) throw RealModelGate.ReportSkipped(provider, "CODESPACE_LLM_* absent (fork/local — no live model)");

        await RealModelGate.AssessLiveAsync(provider, async () =>
        {
            var credential = RealModelLiveWire.Credential(provider, baseUrl, apiKey);
            var recipes = new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new MapFanoutRecipe(), new SupervisorRecipe() });
            var classifier = new LlmEffortClassifier(RealModelLiveWire.Registry(), RealModelLiveWire.Selector(model, credential), recipes, new HeuristicEffortClassifier());

            var request = new EffortRouteRequest
            {
                Seed = new TaskLaunchSeed { Goal = goal, SurfaceKind = "test", TeamId = Guid.NewGuid(), RepositoryId = withRepository ? Guid.NewGuid() : null },
            };

            var decision = await classifier.ClassifyAsync(request, CancellationToken.None);

            if (decision.ClassifierKind != LlmEffortClassifier.ClassifierKind)
                return (false, $"{provider} model '{model}' fell back to the heuristic — the live structured classifier produced no usable decision");

            var shape = decision.Signals.DeliverableShape;

            var ok = expectedShape == "answer-or-research"
                ? shape is DeliverableShapes.Answer or DeliverableShapes.Research
                : shape == expectedShape;

            return (ok, $"{provider} model '{model}' read the shape of \"{goal}\" (repository bound: {withRepository}) as '{shape}'. Expected '{expectedShape}'.");
        });
    }
}
