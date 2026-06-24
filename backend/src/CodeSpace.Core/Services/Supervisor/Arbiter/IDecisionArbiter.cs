using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Decisions;

namespace CodeSpace.Core.Services.Supervisor.Arbiter;

/// <summary>
/// The supervisor's DECISION ARBITER (Decision substrate D4c): the brain that, for ONE pending child decision, decides
/// whether to ANSWER it itself or ESCALATE it to a human. The decision-substrate sibling of <c>ISupervisorDecider</c>
/// (which decides the main spawn/stop loop) — its OWN prompt + schema (Rule 7), not an overload of the delivery decider.
/// </summary>
public interface IDecisionArbiter
{
    /// <summary>Decide answer-or-escalate for <paramref name="decision"/>, using the operator's brain model (<paramref name="supervisorModelId"/>) in the team's credentialed pool, with the run <paramref name="goal"/> as context. The caller has ALREADY confirmed the decision is floor-arbitratable; this judges WHAT to do.</summary>
    Task<ArbiterVerdict> DecideAsync(PendingDecision decision, Guid teamId, Guid? supervisorModelId, string goal, CancellationToken cancellationToken);
}

/// <summary>
/// The real model-backed arbiter (Rule 18.3 — an <see cref="IDecisionArbiter"/> impl in the <c>Arbiter/</c> folder).
/// Mirrors <c>LlmSupervisorDecider</c>'s brain-call EXACTLY — the SAME <see cref="IModelPoolSelector"/> resolves the
/// operator's brain-model row → model id + decrypted credential, the brain model's OWN provider selects the structured
/// client, and the response is constrained to <see cref="ArbiterDecisionSchema"/> — but FAILS CLOSED TO ESCALATE, not
/// stop: a missing / unusable brain model, an empty pool, no structured provider, or a malformed verdict all mean the
/// supervisor cannot responsibly decide, so the decision goes to a HUMAN (the safe default — a wrong auto-answer is
/// costly; a human can always answer).
/// </summary>
public sealed class LlmDecisionArbiter : IDecisionArbiter, IScopedDependency
{
    private readonly ILLMClientRegistry _clientRegistry;
    private readonly IModelPoolSelector _modelSelector;

    public LlmDecisionArbiter(ILLMClientRegistry clientRegistry, IModelPoolSelector modelSelector)
    {
        _clientRegistry = clientRegistry;
        _modelSelector = modelSelector;
    }

    public async Task<ArbiterVerdict> DecideAsync(PendingDecision decision, Guid teamId, Guid? supervisorModelId, string goal, CancellationToken cancellationToken)
    {
        if (supervisorModelId is not { } brainModelId) return ArbiterVerdict.Escalate("No supervisor brain model is configured — escalated to a human.");

        // The arbiter NEVER throws — its caller (the supervisor turn) relies on ALWAYS getting a verdict. Any failure of
        // the model resolution, the brain call (a transient API error, a no-tool-block / non-2xx response), or the parse
        // (the forced-tool path doesn't hard-validate, so a type-mismatched verdict makes Deserialize throw — not return
        // null) means the supervisor can't responsibly decide → escalate to a human, the safe default. Cancellation still
        // propagates (the run is being torn down — there is no human to escalate to).
        try
        {
            var pick = await _modelSelector.ResolveByRowIdAsync(teamId, brainModelId, cancellationToken).ConfigureAwait(false);

            if (pick == null) return ArbiterVerdict.Escalate("No brain model is available in the team's pool — escalated to a human.");

            var structured = _clientRegistry.All.OfType<IStructuredLLMClient>().FirstOrDefault(c => string.Equals(c.Provider, pick.Credential.Provider, StringComparison.OrdinalIgnoreCase));

            if (structured == null) return ArbiterVerdict.Escalate("No structured-output provider for the brain model — escalated to a human.");

            var completion = await structured.CompleteStructuredAsync(BuildRequest(decision, goal, pick), cancellationToken).ConfigureAwait(false);

            var model = completion.Json.Deserialize<ArbiterModelDecision>(ArbiterDecisionSchema.Options);

            return model is null ? ArbiterVerdict.Escalate("The arbiter returned no decision — escalated to a human.") : Project(model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ArbiterVerdict.Escalate("The arbiter could not produce a valid decision — escalated to a human.");
        }
    }

    private static StructuredLLMCompletionRequest BuildRequest(PendingDecision decision, string goal, ModelPoolPick pick) => new()
    {
        Model = pick.ModelId,
        SystemPrompt = SystemPrompt,
        UserPrompt = BuildUserPrompt(decision, goal),
        JsonSchema = ArbiterDecisionSchema.ResponseSchema,
        MaxOutputTokens = 2048,
        Temperature = 0.2,
        Credential = pick.Credential,
    };

    /// <summary>Internal test accessor (InternalsVisibleTo) — pins the decision→prompt framing without a real LLM round-trip.</summary>
    internal static string BuildUserPromptForTest(PendingDecision decision, string goal) => BuildUserPrompt(decision, goal);

    private static string BuildUserPrompt(PendingDecision decision, string goal)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"Goal: {goal}");
        builder.AppendLine();
        builder.AppendLine("A worker raised a DECISION and is blocked waiting for an answer:");
        builder.AppendLine($"  Question: {decision.Question}");

        if (!string.IsNullOrWhiteSpace(decision.BlockingReason)) builder.AppendLine($"  Why blocked: {decision.BlockingReason}");
        if (!string.IsNullOrWhiteSpace(decision.ContextSummary)) builder.AppendLine($"  Context: {decision.ContextSummary}");

        builder.AppendLine($"  Risk: {decision.RiskLevel}");

        if (!string.IsNullOrWhiteSpace(decision.RecommendedOption)) builder.AppendLine($"  Recommended option: {decision.RecommendedOption}");

        if (decision.Options.Count > 0)
        {
            builder.AppendLine("  Options:");
            foreach (var o in decision.Options) builder.AppendLine($"    {o.Id}: {o.Label}{(o.IsSideEffecting ? " (irreversible)" : "")}");
        }
        else
        {
            builder.AppendLine("  (free-text answer expected)");
        }

        builder.AppendLine();
        builder.AppendLine("Decide: answer it yourself (only if low/medium risk and you are confident — choose the recommended option unless you have a clear reason), or escalate to a human. Return ONLY the schema-constrained JSON with a rationale.");

        return builder.ToString();
    }

    /// <summary>Project the schema-valid model verdict into the canonical <see cref="ArbiterVerdict"/>, FAIL-CLOSED: only a clean <c>answer</c> answers; <c>escalate</c> or any unknown / missing kind escalates to a human. A blank rationale degrades to a placeholder (never silent). Internal for direct unit testing.</summary>
    internal static ArbiterVerdict Project(ArbiterModelDecision model)
    {
        var rationale = string.IsNullOrWhiteSpace(model.Rationale) ? "(the arbiter gave no rationale)" : model.Rationale;

        if (!string.Equals(model.Kind, ArbiterVerdictKinds.Answer, StringComparison.OrdinalIgnoreCase))
            return ArbiterVerdict.Escalate(rationale);

        return ArbiterVerdict.Answer(model.Answer?.SelectedOptions ?? Array.Empty<string>(), model.Answer?.FreeText, rationale);
    }

    private const string SystemPrompt =
        "You are a software-delivery supervisor's decision arbiter. A worker (a coding agent or a workflow step) hit a " +
        "decision it cannot make alone and is BLOCKED waiting for an answer. For this ONE decision you choose: ANSWER it " +
        "yourself, or ESCALATE it to a human. Answer ONLY when the decision is low or medium risk AND you are genuinely " +
        "confident — prefer the raiser's recommended option unless you have a clear, stated reason to differ. ESCALATE " +
        "whenever you are unsure, the stakes are meaningful, the options are irreversible, or the recommendation and " +
        "context are too thin to decide responsibly. When in doubt, ESCALATE — a human can always answer, and a wrong " +
        "auto-answer is costly. ALWAYS give a rationale. Return ONLY the schema-constrained JSON.";
}

/// <summary>The arbiter brain's raw schema-valid output, before projection. Bound case-insensitively from the structured response.</summary>
internal sealed record ArbiterModelDecision
{
    public string? Kind { get; init; }

    public ArbiterModelAnswer? Answer { get; init; }

    public string? Rationale { get; init; }
}

internal sealed record ArbiterModelAnswer
{
    public IReadOnlyList<string>? SelectedOptions { get; init; }

    public string? FreeText { get; init; }
}
