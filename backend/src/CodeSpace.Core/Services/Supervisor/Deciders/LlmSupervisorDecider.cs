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
    private readonly ISupervisorModelSelector _modelSelector;

    public LlmSupervisorDecider(ILLMClientRegistry clientRegistry, ISupervisorModelSelector modelSelector)
    {
        _clientRegistry = clientRegistry;
        _modelSelector = modelSelector;
    }

    public async Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var structured = _clientRegistry.All.OfType<IStructuredLLMClient>().FirstOrDefault();

        if (structured == null) return NoModelStop();

        // The brain's model + key come from the POOL — exactly like the agents it spawns. The selector picks a
        // structured-capable model the operator credentialed (within the allowed pool, the supervisorModel pin if any,
        // preferring a recommended one). Nothing qualifies → fail-closed: no env "system" key, no default model.
        var pick = await _modelSelector.SelectAsync(context.TeamId, structured.Provider, context.AllowedModels, context.SupervisorModel, cancellationToken).ConfigureAwait(false);

        if (pick == null) return NoPoolModelStop();

        var completion = await structured.CompleteStructuredAsync(BuildRequest(context, pick), cancellationToken).ConfigureAwait(false);

        var model = Deserialize(completion.Json);

        return SupervisorDecisionProjector.Project(model);
    }

    private static StructuredLLMCompletionRequest BuildRequest(SupervisorTurnContext context, SupervisorModelPick pick) => new()
    {
        // The model id AND the credential both come from the one chosen pool row — nothing guessed, nothing hidden.
        Model = pick.ModelId,
        SystemPrompt = SystemPrompt,
        UserPrompt = BuildUserPrompt(context),
        JsonSchema = SupervisorDecisionSchema.ResponseSchema,
        MaxOutputTokens = 4096,
        Temperature = 0.2,
        Credential = pick.Credential,
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
            // The index of the MOST RECENT spawn/retry — the one whose agent results the decider should act on
            // (a later retry's results supersede the original spawn's). Marked so the model targets the freshest.
            var latestSpawnIndex = LastIndexOf(context.PriorDecisions, d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind));

            builder.AppendLine("Prior decisions (in order, with their recorded outcomes):");
            for (var i = 0; i < context.PriorDecisions.Count; i++)
                AppendPriorDecision(builder, context.PriorDecisions[i], isLatestSpawn: i == latestSpawnIndex);
        }

        builder.AppendLine();
        builder.AppendLine("Choose the single next action. After planning, spawn agents over the planned subtask ids; once their results are recorded, INSPECT each agent's status and error in the most recent spawn OR retry outcome above, RETRY any subtask that failed or did not satisfy the goal (optionally with a revised instruction), then merge the successful results, then stop. Return ONLY the schema-constrained JSON.");

        return builder.ToString();
    }

    /// <summary>Internal test accessor (InternalsVisibleTo) — pins the system-prompt guidance as a tested contract (the inspect-and-retry framing; no unconditional merge-then-stop rail).</summary>
    internal static string SystemPromptForTest => SystemPrompt;

    /// <summary>
    /// Render one prior decision for the decider. A spawn/retry that carries folded agent results (SOTA #2) is
    /// rendered as one LABELED line per agent — <c>status — summary/error</c>, NOT the raw outcome jsonb — so the
    /// model reads each agent's outcome legibly without the agent-run GUIDs (noise it never acts on) and the failed
    /// agents stand out as the retry signal. The most-recent spawn/retry is tagged so the model targets the freshest
    /// results. Every other decision keeps the compact payload+outcome line.
    /// </summary>
    private static void AppendPriorDecision(StringBuilder builder, SupervisorPriorDecision prior, bool isLatestSpawn)
    {
        var agentResults = SupervisorDecisionKinds.StagesAgents(prior.DecisionKind)
            ? SupervisorOutcome.ReadAgentResults(prior.OutcomeJson)
            : Array.Empty<SupervisorAgentResult>();

        if (agentResults.Count > 0)
        {
            builder.AppendLine($"- {prior.DecisionKind}{(isLatestSpawn ? " (latest — act on THESE results)" : "")}: payload={prior.PayloadJson}");
            for (var k = 0; k < agentResults.Count; k++)
            {
                var r = agentResults[k];
                var detail = !string.IsNullOrWhiteSpace(r.Error) ? $"error: {r.Error}" : !string.IsNullOrWhiteSpace(r.Summary) ? r.Summary : "(no summary)";
                builder.AppendLine($"    agent {k}: {r.Status} — {detail}");
            }

            if (prior.DecisionKind == SupervisorDecisionKinds.Resolve) AppendResolutionVerdict(builder, prior);

            return;
        }

        // A merge whose on-disk integration CONFLICTED is rendered as a legible, actionable block (resolver loop #379) —
        // the conflicted files + the PRESERVED agent branches + the resolve-or-stop choice — so the decider acts on the
        // conflict rather than parsing it out of raw outcome jsonb. A clean/skipped merge keeps the compact line below.
        if (prior.DecisionKind == SupervisorDecisionKinds.Merge && SupervisorOutcome.ReadIntegration(prior.OutcomeJson) is { IsConflicted: true } integration)
        {
            AppendConflictedMerge(builder, integration);
            return;
        }

        builder.AppendLine($"- {prior.DecisionKind}: payload={prior.PayloadJson} outcome={prior.OutcomeJson ?? "(none)"}");
    }

    /// <summary>Render the resolver's build/test VERDICT (S3) so the decider acts on it: a VERIFIED resolution may be accepted (merge again / open a PR); an UNVERIFIED one must NOT be accepted (retry within the cap, or stop and leave the conflict for a human).</summary>
    private static void AppendResolutionVerdict(StringBuilder builder, SupervisorPriorDecision prior)
    {
        var verdict = SupervisorOutcome.ReadResolutionVerdict(prior.OutcomeJson);

        builder.AppendLine(verdict switch
        {
            SupervisorResolutionVerdict.Verified => "    resolution VERIFIED — the reconciliation built and passed the tests; it is safe to accept (merge again / open a PR).",
            SupervisorResolutionVerdict.Unverified => "    resolution NOT verified — the reconciliation did not pass the build/tests; do NOT accept it. Retry the resolution or stop and leave the conflict for a human.",
            _ => "    resolution verdict unknown — the resolver has not produced a verified result.",
        });
    }

    /// <summary>Render a conflicted merge integration legibly: what conflicted, where the agents' work is preserved, and the two moves available (spawn a resolver to reconcile + verify, or stop and leave it for a human).</summary>
    private static void AppendConflictedMerge(StringBuilder builder, SupervisorIntegrationOutcome integration)
    {
        builder.AppendLine("- merge: INTEGRATION CONFLICTED — the agents' work could not be auto-combined.");
        builder.AppendLine($"    conflicted files: {(integration.ConflictedFiles.Count > 0 ? string.Join(", ", integration.ConflictedFiles) : "(unspecified)")}");

        if (!string.IsNullOrWhiteSpace(integration.Reason)) builder.AppendLine($"    reason: {integration.Reason}");
        if (integration.PreservedBranches.Count > 0) builder.AppendLine($"    the agents' work is PRESERVED on branches: {string.Join(", ", integration.PreservedBranches)}");

        builder.AppendLine("    To resolve: spawn ONE agent to reconcile these branches, build, and run the tests, then merge again. Or stop to leave the conflict for a human.");
    }

    /// <summary>The index of the LAST element matching the predicate, or -1 — used to tag the most-recent spawn/retry.</summary>
    private static int LastIndexOf(IReadOnlyList<SupervisorPriorDecision> decisions, Func<SupervisorPriorDecision, bool> predicate)
    {
        for (var i = decisions.Count - 1; i >= 0; i--)
            if (predicate(decisions[i])) return i;
        return -1;
    }

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

    /// <summary>Fail-closed terminal stop when the team's credentialed-model POOL has no structured-capable model the brain can run (none configured, or none within the allowed pool / pin) — it stops cleanly rather than guessing a model or key. The pool analogue of <see cref="NoModelStop"/>.</summary>
    private static SupervisorDecision NoPoolModelStop() => new()
    {
        Kind = SupervisorDecisionKinds.Stop,
        PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "no-model", Summary = "No structured-output model is available in the team's credentialed model pool — add a model credential with a structured-capable model, or widen the allowed model pool." }, AgentJson.Options),
    };

    private const string SystemPrompt =
        "You are a software-delivery supervisor driving a bounded loop of decisions toward a goal. " +
        "On each turn you emit ONE action from a fixed vocabulary: 'plan' (decompose the goal into subtasks), " +
        "'spawn' (fan out coding agents over planned subtask ids), 'retry' (re-run one subtask), " +
        "'merge' (synthesize the agents' results), 'ask_human' (ask a question), 'stop' (finish). " +
        "Plan first. Then drive the subtasks to completion: spawn over the planned subtask ids, inspect each agent's " +
        "recorded status, error and summary in the most recent spawn OR retry outcome, retry any subtask that FAILED or " +
        "did not satisfy the goal (optionally with a revised instruction), and merge only once the results you need have " +
        "succeeded. Stop when the goal is met or a bound forces it. " +
        "If a merge reports INTEGRATION CONFLICTED, the agents' work could not be auto-combined; you may spawn ONE agent " +
        "to reconcile the preserved branches, build, and run the tests (then merge again), or stop to leave the conflict " +
        "for a human — never accept an unverified resolution. " +
        "You never name node types, run ids, or graph wiring — only the action + its payload. " +
        "Return ONLY the schema-constrained JSON.";
}
