using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// The real model-backed supervisor decider (PR-E E3, Rule 18.3 — an <see cref="ISupervisorDecider"/> impl in
/// the <c>Deciders/</c> variant folder). It folds the turn context (goal + the prior-decision tape) into a
/// system+user prompt, calls a structured-LLM client constrained by <see cref="SupervisorDecisionSchema"/>,
/// and PROJECTS the schema-valid <see cref="SupervisorModelDecision"/> into the canonical
/// <see cref="SupervisorDecision"/> (verb + canonical payload JSON) the turn loop hashes + records.
///
/// <para>The model NEVER addresses graph topology — the schema carries no node id / type key / run id. The
/// server (turn service + executor) turns the verb + bounded payload into a side effect; the ledger key + the
/// agent-run waits + the node id are all server-derived. Resolves the SAME <see cref="ILLMClientRegistry"/>
/// the planner uses (the first structured-capable provider). When no structured provider is registered it
/// FAILS CLOSED with a clean terminal <c>stop</c> rather than crashing — so a deployment with the lane on but
/// no model degrades to a one-turn no-op, never an unhandled exception mid-run.</para>
/// </summary>
public sealed class LlmSupervisorDecider : ISupervisorDecider, IScopedDependency
{
    private readonly ILLMClientRegistry _clientRegistry;

    public LlmSupervisorDecider(ILLMClientRegistry clientRegistry) { _clientRegistry = clientRegistry; }

    public async Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var structured = _clientRegistry.All.OfType<IStructuredLLMClient>().FirstOrDefault();

        if (structured == null) return NoModelStop();

        var completion = await structured.CompleteStructuredAsync(BuildRequest(context), cancellationToken).ConfigureAwait(false);

        var model = Deserialize(completion.Json);

        return SupervisorDecisionProjector.Project(model);
    }

    private static StructuredLLMCompletionRequest BuildRequest(SupervisorTurnContext context) => new()
    {
        Model = DefaultModel,
        SystemPrompt = SystemPrompt,
        UserPrompt = BuildUserPrompt(context),
        JsonSchema = SupervisorDecisionSchema.ResponseSchema,
        MaxOutputTokens = 4096,
        Temperature = 0.2,
    };

    /// <summary>Internal test accessor (InternalsVisibleTo) — pins the context→prompt framing directly, without a real LLM round-trip.</summary>
    internal static string BuildUserPromptForTest(SupervisorTurnContext context) => BuildUserPrompt(context);

    private static string BuildUserPrompt(SupervisorTurnContext context)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"Goal: {context.Goal}");
        builder.AppendLine($"Turn: {context.TurnNumber}");
        builder.AppendLine();

        if (context.PriorDecisions.Count == 0)
        {
            builder.AppendLine("No prior decisions yet — this is the first turn. Start by planning (decompose the goal into subtasks).");
        }
        else
        {
            builder.AppendLine("Prior decisions (in order, with their recorded outcomes):");
            foreach (var prior in context.PriorDecisions)
                builder.AppendLine($"- {prior.DecisionKind}: payload={prior.PayloadJson} outcome={prior.OutcomeJson ?? "(none)"}");
        }

        builder.AppendLine();
        builder.AppendLine("Choose the single next action. After planning, spawn agents over the planned subtask ids; once their results are recorded, INSPECT each agent's status and error in the most recent spawn OR retry outcome above, RETRY any subtask that failed or did not satisfy the goal (optionally with a revised instruction), then merge the successful results, then stop. Return ONLY the schema-constrained JSON.");

        return builder.ToString();
    }

    /// <summary>Internal test accessor (InternalsVisibleTo) — pins the system-prompt guidance as a tested contract (the inspect-and-retry framing; no unconditional merge-then-stop rail).</summary>
    internal static string SystemPromptForTest => SystemPrompt;

    private static SupervisorModelDecision Deserialize(JsonElement json)
    {
        var model = json.Deserialize<SupervisorModelDecision>(SupervisorDecisionSchema.Options);

        if (model == null || string.IsNullOrWhiteSpace(model.Kind))
            throw new InvalidOperationException("The supervisor decider returned no decision (no kind). The response did not conform to the decision schema.");

        return model;
    }

    /// <summary>Fail-closed terminal stop when no structured-LLM provider is registered — the lane is on but no model is configured. Deterministic so a replay re-derives it.</summary>
    private static SupervisorDecision NoModelStop() => new()
    {
        Kind = SupervisorDecisionKinds.Stop,
        PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "no-model", Summary = "No structured-output LLM provider is configured for the supervisor lane." }, AgentJson.Options),
    };

    /// <summary>Mirrors the planner's default structured-capable model. A per-team override is a later concern.</summary>
    private const string DefaultModel = "claude-sonnet-4-5";

    private const string SystemPrompt =
        "You are a software-delivery supervisor driving a bounded loop of decisions toward a goal. " +
        "On each turn you emit ONE action from a fixed vocabulary: 'plan' (decompose the goal into subtasks), " +
        "'spawn' (fan out coding agents over planned subtask ids), 'retry' (re-run one subtask), " +
        "'merge' (synthesize the agents' results), 'ask_human' (ask a question), 'stop' (finish). " +
        "Plan first. Then drive the subtasks to completion: spawn over the planned subtask ids, inspect each agent's " +
        "recorded status, error and summary in the most recent spawn OR retry outcome, retry any subtask that FAILED or " +
        "did not satisfy the goal (optionally with a revised instruction), and merge only once the results you need have " +
        "succeeded. Stop when the goal is met or a bound forces it. " +
        "You never name node types, run ids, or graph wiring — only the action + its payload. " +
        "Return ONLY the schema-constrained JSON.";
}
