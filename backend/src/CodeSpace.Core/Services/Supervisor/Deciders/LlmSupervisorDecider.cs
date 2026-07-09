using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
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
    private readonly IModelPoolSelector _modelSelector;
    private readonly IAgentHarnessRegistry _harnesses;
    private readonly IAgentDefinitionService _agentDefinitions;

    private readonly ISupervisorTapeSummaryStore _tapeSummaries;

    public LlmSupervisorDecider(ILLMClientRegistry clientRegistry, IModelPoolSelector modelSelector, IAgentHarnessRegistry harnesses, IAgentDefinitionService agentDefinitions, ISupervisorTapeSummaryStore tapeSummaries)
    {
        _clientRegistry = clientRegistry;
        _modelSelector = modelSelector;
        _harnesses = harnesses;
        _agentDefinitions = agentDefinitions;
        _tapeSummaries = tapeSummaries;
    }

    public async Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        // The brain model is the operator's REQUIRED explicit pick (one credentialed-model ROW, never the agent pool
        // and never a guessed default). Absent → fail closed.
        if (context.SupervisorModelId is not { } brainModelId) return NoBrainModelStop();

        // Resolve that exact row → its model id + decrypted credential (team-owned, enabled). A missing / disabled /
        // revoked row → fail closed: no env "system" key, no substitute model.
        var pick = await _modelSelector.ResolveByRowIdAsync(context.TeamId, brainModelId, cancellationToken).ConfigureAwait(false);

        if (pick == null) return NoPoolModelStop();

        // The brain model's OWN provider determines the structured client that serves it (multi-provider-ready) — not a
        // first-registered guess. No structured client for that provider → fail closed.
        var structured = _clientRegistry.All.OfType<IStructuredLLMClient>().FirstOrDefault(c => string.Equals(c.Provider, pick.Credential.Provider, StringComparison.OrdinalIgnoreCase));

        if (structured == null) return NoModelStop();

        // P1 — render the capability catalog (available harnesses + their drivable providers, and this run's
        // credentialed model pool + each model's provider) so the brain authors a provider-compatible (harness, model)
        // per agent ON PURPOSE rather than blind. The server still clamps an incompatible pair, so this guides not gates.
        var catalog = await BuildCapabilityCatalogAsync(context, cancellationToken).ConfigureAwait(false);

        // P1.2 — load the run's rolling tape digest (if any prior turn compacted): the prompt then renders
        // [digest + recent tail] instead of the whole tape, so a once-compacted run STAYS under the window.
        if (context.TapeSummary is null && await _tapeSummaries.GetAsync(context.SupervisorRunId, context.TeamId, cancellationToken).ConfigureAwait(false) is { } persisted)
            context = context with { TapeSummary = persisted };

        StructuredLLMCompletion completion;
        try
        {
            completion = await CompleteWithCompactionAsync(structured, pick, context, catalog, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmApiException ex) when (IsModelCapabilityMiss(ex.Category))
        {
            // The gateway produced NO usable decision for a MODEL-side reason — a reply that failed schema validation
            // after a re-ask / yielded no JSON (Malformed), an over-long context (ContextLengthExceeded), a
            // content-filtered or bad-request reply. Fail closed to a clean stop, the SAME way as no-model / an unknown
            // kind — NEVER crash the durable run on a model that simply could not produce a conformant decision. A genuine
            // INFRA fault (a timeout / 5xx / 429 / auth) is NOT caught here: it propagates so the engine fails the run and
            // the live-gate treats it as non-gating gateway infra (consistent with the decision-eval lane).
            return NonConformantStop();
        }

        // A completion CUT OFF mid-generation (Anthropic max_tokens / OpenAI length — see ModelCallFinish, the SAME
        // classifier the journal legibility axis uses) is not a shape the model chose: it ran out of room, most
        // often authoring a large multi-subtask plan. Buy ONE bounded retry with a RAISED output budget before this
        // falls into the bind-check flow — the repair round-trip below reuses the SAME (smaller) request budget, so
        // without this it would only reproduce the identical truncation on a genuinely big decision.
        if (ModelCallFinish.Classify(completion.Usage.FinishReason) == ModelCallFinishKind.Truncated)
            completion = await TryRetryWithRaisedBudgetAsync(structured, pick, context, catalog, cancellationToken).ConfigureAwait(false) ?? completion;

        var model = TryDeserialize(completion.Json, out var bindError);

        // A reply that passed the JSON schema but did not BIND to the decision record is a schema↔type DRIFT signal
        // (the validator and the C# shape disagree) or a near-miss the lenient converters couldn't absorb. Before
        // failing closed, buy ONE bounded REPAIR round-trip: hand the model its own reply + the exact binding error
        // and ask for the same decision corrected — the cheapest self-heal, mirroring the S6 revise philosophy at
        // the decision grain. A repair that itself fails (or still doesn't bind) falls to the clean stop, WITH the
        // precise path in the stop summary so the journal names the drift instead of a bare "did not conform".
        if (model is null || string.IsNullOrWhiteSpace(model.Kind))
        {
            completion = await TryRepairAsync(structured, pick, completion, bindError, cancellationToken).ConfigureAwait(false) ?? completion;
            model = TryDeserialize(completion.Json, out bindError);
        }

        if (model is null || string.IsNullOrWhiteSpace(model.Kind)) return NonConformantStop(bindError);

        // Capture the authoring model call (the pool-picked model + this reply's token usage) — the turn service folds it
        // into the NON-hashed outcome, never the payload, so it can't drift the idempotency key. It's how the journal shows
        // what authored the decision (e.g. the "via <model> · N tokens" line on a plan beat).
        var decision = SupervisorDecisionProjector.Project(model) with
        {
            Usage = new SupervisorModelUsage { Model = pick.ModelId, InputTokens = completion.Usage.InputTokens, OutputTokens = completion.Usage.OutputTokens },
        };

        // A STRUCTURALLY invalid plan (SupervisorPlanValidator: a dangling DependsOn reference or a cycle) would
        // otherwise force-stop the whole run at SupervisorTurnService's post-decision gate with no chance to
        // recover — buy ONE bounded re-plan with the specific error folded back before that terminal path ever
        // runs. A retry that fails too (or misses) keeps the ORIGINAL decision, so the existing gate still force-
        // stops exactly as before this existed.
        if (SupervisorPlanValidator.Validate(decision) is { } planError)
            decision = await TryRepairInvalidPlanAsync(structured, pick, context, catalog, decision, planError, cancellationToken).ConfigureAwait(false) ?? decision;

        return decision;
    }

    /// <summary>
    /// One bounded RE-PLAN round-trip after a STRUCTURALLY invalid plan: the model receives its own invalid plan
    /// payload plus the validator's reason (a dangling <c>DependsOn</c> reference or a cycle) and must re-author a
    /// valid decision — a revised plan, or a different action entirely if planning no longer fits. Returns null on
    /// a model-side miss OR a retry that is STILL invalid (fail toward the ORIGINAL decision, which the caller then
    /// keeps — <see cref="SupervisorTurnService.ApplyPostDecisionGate"/> force-stops it exactly as before this
    /// existed) — a genuine INFRA fault propagates unchanged, same as every other repair call in this file.
    /// </summary>
    private static async Task<SupervisorDecision?> TryRepairInvalidPlanAsync(IStructuredLLMClient structured, ModelPoolPick pick, SupervisorTurnContext context, string catalog, SupervisorDecision invalid, string planError, CancellationToken cancellationToken)
    {
        StructuredLLMCompletion completion;
        try
        {
            completion = await structured.CompleteStructuredAsync(new StructuredLLMCompletionRequest
            {
                Model = pick.ModelId,
                SystemPrompt = SystemPrompt,
                UserPrompt = $"{BuildUserPrompt(context, catalog)}\n\nYour previous 'plan' decision was structurally INVALID ({planError}): {invalid.PayloadJson}\n\nEvery subtask id a 'dependsOn' entry cites must be declared elsewhere in the SAME plan, and the dependency graph must contain no cycle (including a subtask depending on itself). Re-author a valid plan, or choose a different action if planning no longer fits.",
                JsonSchema = SupervisorDecisionSchema.ResponseSchema,
                MaxOutputTokens = 4096,
                Temperature = 0,
                Credential = pick.Credential,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmApiException ex) when (IsModelCapabilityMiss(ex.Category))
        {
            return null;
        }

        var model = TryDeserialize(completion.Json, out _);

        if (model is null || string.IsNullOrWhiteSpace(model.Kind)) return null;

        var retried = SupervisorDecisionProjector.Project(model) with
        {
            Usage = new SupervisorModelUsage { Model = pick.ModelId, InputTokens = completion.Usage.InputTokens, OutputTokens = completion.Usage.OutputTokens },
        };

        return SupervisorPlanValidator.Validate(retried) is null ? retried : null;
    }

    /// <summary>
    /// One bounded REPAIR round-trip after a bind failure: the model receives its OWN schema-valid-but-unbindable
    /// reply plus the exact binding error (the JSON path + why), and must re-emit the SAME decision corrected. Runs
    /// on the same pinned brain row + the same schema constraint (the structured client re-validates). Returns null
    /// when the repair itself is a model-side miss (fail toward the clean stop) — a genuine INFRA fault propagates,
    /// exactly like the primary call.
    /// </summary>
    private static async Task<StructuredLLMCompletion?> TryRepairAsync(IStructuredLLMClient structured, ModelPoolPick pick, StructuredLLMCompletion broken, string? bindError, CancellationToken cancellationToken)
    {
        try
        {
            return await structured.CompleteStructuredAsync(new StructuredLLMCompletionRequest
            {
                Model = pick.ModelId,
                SystemPrompt = "You repair one malformed supervisor decision. Reply with ONLY the corrected decision JSON object — same intent, no commentary, no new decisions.",
                UserPrompt = $"Your previous reply matched the JSON schema but could not be bound to the decision contract.\n\nBinding error: {bindError ?? "(unspecified)"}\n\nYour previous reply:\n{broken.Json.GetRawText()}\n\nRe-emit the SAME decision with that error corrected.",
                JsonSchema = SupervisorDecisionSchema.ResponseSchema,
                MaxOutputTokens = 4096,
                Temperature = 0,
                Credential = pick.Credential,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmApiException ex) when (IsModelCapabilityMiss(ex.Category))
        {
            return null;
        }
    }

    /// <summary>The raised output budget ONE truncated-completion retry gets — double the normal <see cref="MaxOutputTokens"/> (4096) so a large multi-subtask plan gets genuine room to finish. Pinned (Rule 8): shrinking it re-narrows the exact window this exists to widen.</summary>
    internal const int TruncatedRetryMaxOutputTokens = 8192;

    /// <summary>
    /// One bounded retry after a TRUNCATED completion: re-run the SAME request with <see cref="TruncatedRetryMaxOutputTokens"/>
    /// instead of the default budget. Returns null on a model-side miss (fail toward the ORIGINAL truncated completion,
    /// which the caller's normal bind-check/repair/stop flow then handles exactly as before this existed) — a genuine
    /// INFRA fault propagates unchanged, same as every other repair call in this file.
    /// </summary>
    private static async Task<StructuredLLMCompletion?> TryRetryWithRaisedBudgetAsync(IStructuredLLMClient structured, ModelPoolPick pick, SupervisorTurnContext context, string catalog, CancellationToken cancellationToken)
    {
        try
        {
            return await structured.CompleteStructuredAsync(BuildRequest(context, pick, catalog) with { MaxOutputTokens = TruncatedRetryMaxOutputTokens }, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmApiException ex) when (IsModelCapabilityMiss(ex.Category))
        {
            return null;
        }
    }

    /// <summary>How many of the NEWEST decisions stay rendered raw after a compaction — the model still sees its recent moves verbatim; only the older head folds into the digest. Pinned by a unit test (Rule 8).</summary>
    internal const int CompactTailKeep = 8;

    /// <summary>The fewest decisions worth folding — below this a compaction would not shrink the prompt meaningfully (the overflow has another cause), so the original fault propagates to the clean-stop path.</summary>
    internal const int MinCompactFold = 4;

    private static readonly JsonElement TapeSummarySchema = JsonDocument.Parse("""
        { "type": "object", "additionalProperties": false, "required": ["summary"], "properties": { "summary": { "type": "string", "description": "The compact progress digest." } } }
        """).RootElement;

    /// <summary>
    /// The primary brain call with the P1.2 AUTO-COMPACT safety net: on the FIRST ContextLengthExceeded the decider
    /// folds the tape's oldest decisions into a persisted rolling digest (one bounded summarizer call on the same
    /// pinned brain row), rebuilds the prompt as [digest + recent tail] and retries the SAME decision ONCE — a long
    /// run's growing tape compacts instead of dying. A second overflow (or a tape too small to fold) propagates to
    /// the existing clean-stop path; an infra fault during the summarizer propagates too (the node's infra park owns
    /// it). Later turns load the digest at rehydrate, so the prompt STAYS compacted without re-hitting the window.
    /// </summary>
    private async Task<StructuredLLMCompletion> CompleteWithCompactionAsync(IStructuredLLMClient structured, ModelPoolPick pick, SupervisorTurnContext context, string catalog, CancellationToken cancellationToken)
    {
        try
        {
            return await structured.CompleteStructuredAsync(BuildRequest(context, pick, catalog), cancellationToken).ConfigureAwait(false);
        }
        catch (LlmApiException ex) when (ex.Category == LlmErrorCategory.ContextLengthExceeded)
        {
            var compacted = await TryCompactTapeAsync(structured, pick, context, cancellationToken).ConfigureAwait(false);

            if (compacted == null) throw;

            return await structured.CompleteStructuredAsync(BuildRequest(compacted, pick, catalog), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fold every prior decision except the newest <see cref="CompactTailKeep"/> into a model-written digest
    /// (folding INTO any existing digest — the roll-forward), persist it (the rolling per-run row), and return the
    /// context re-stamped with it. Null when the foldable head is smaller than <see cref="MinCompactFold"/> —
    /// compaction could not meaningfully shrink the prompt, so the caller lets the original overflow propagate.
    /// </summary>
    private async Task<SupervisorTurnContext?> TryCompactTapeAsync(IStructuredLLMClient structured, ModelPoolPick pick, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var alreadyFolded = context.TapeSummary?.UpToSequence ?? long.MinValue;
        var rendered = context.PriorDecisions.Where(d => d.Sequence > alreadyFolded).ToList();
        var foldable = rendered.Take(Math.Max(0, rendered.Count - CompactTailKeep)).ToList();

        if (foldable.Count < MinCompactFold) return null;

        var digest = await SummarizeAsync(structured, pick, context, foldable, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(digest)) return null;

        var summary = new SupervisorTapeSummary { UpToSequence = foldable[^1].Sequence, Text = digest };

        await _tapeSummaries.UpsertAsync(context.SupervisorRunId, context.TeamId, summary.UpToSequence, summary.Text, cancellationToken).ConfigureAwait(false);

        return context with { TapeSummary = summary };
    }

    /// <summary>One bounded summarizer round-trip on the SAME pinned brain row: the prior digest (roll-forward) + the foldable head, out comes the new digest. A model-side miss reads as "no digest" (null → the overflow propagates); an INFRA fault propagates (the node's park owns it).</summary>
    private static async Task<string?> SummarizeAsync(IStructuredLLMClient structured, ModelPoolPick pick, SupervisorTurnContext context, IReadOnlyList<SupervisorPriorDecision> foldable, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"Goal: {context.Goal}");
        builder.AppendLine();

        if (context.TapeSummary is { } prior)
        {
            builder.AppendLine("Existing progress digest (fold the new decisions below INTO it):");
            builder.AppendLine(prior.Text.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("Decisions to fold (in order, with their recorded outcomes):");
        var latestSpawnIndex = LastIndexOf(foldable, d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind));
        for (var i = 0; i < foldable.Count; i++)
            // The fold path is ALREADY a compaction (the summarizer compresses to ~400 words), so it keeps FULL plan
            // payloads — the summarizer wants "what was planned" verbatim to distil. The superseded-plan digest is a
            // LIVE-prompt concern only; the fold rendering stays byte-identical (untouched #1004 tape-summary behavior).
            AppendPriorDecision(builder, foldable[i], isLatestSpawn: i == latestSpawnIndex, isSupersededPlan: false);

        try
        {
            var completion = await structured.CompleteStructuredAsync(new StructuredLLMCompletionRequest
            {
                Model = pick.ModelId,
                SystemPrompt = "You compact a supervisor run's oldest decisions into one rolling progress digest. Keep every fact a future decision needs: what was planned, each subtask's final state (succeeded/failed/why), branches produced, merges/conflicts, human answers, key learnings. Be dense; max ~400 words. Reply with ONLY the schema JSON.",
                UserPrompt = builder.ToString(),
                JsonSchema = TapeSummarySchema,
                MaxOutputTokens = 1024,
                Temperature = 0,
                Credential = pick.Credential,
            }, cancellationToken).ConfigureAwait(false);

            return completion.Json.ValueKind == JsonValueKind.Object && completion.Json.TryGetProperty("summary", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (LlmApiException ex) when (IsModelCapabilityMiss(ex.Category))
        {
            return null;
        }
    }

    /// <summary>Whether an LLM transport failure is a MODEL-side capability miss (the model could not produce a usable structured decision) rather than a gateway/credential INFRA fault. Capability misses fail closed to a clean stop (never crash the run); infra faults (Transient / RateLimited / AuthFailed) propagate so the engine fails the run and the live-gate treats them as non-gating infra. This is the decider's "fail closed on a model miss, surface real infra" split.</summary>
    private static bool IsModelCapabilityMiss(LlmErrorCategory category) => category is
        LlmErrorCategory.Malformed or LlmErrorCategory.ContextLengthExceeded or
        LlmErrorCategory.ContentFiltered or LlmErrorCategory.BadRequest;

    /// <summary>Deserialize the model's structured reply to a decision, returning null on ANY non-conformant shape (a wrong top-level type, malformed JSON) so the caller fails closed — the decider never crashes the durable run on a degraded gateway reply. The binding error (the JSON path + why) rides out so the repair prompt and the stop summary can NAME the miss.</summary>
    private static SupervisorModelDecision? TryDeserialize(JsonElement json, out string? bindError)
    {
        try
        {
            bindError = null;
            return json.Deserialize<SupervisorModelDecision>(SupervisorDecisionSchema.Options);
        }
        catch (JsonException ex)
        {
            bindError = ex.Path is null ? ex.Message : $"at {ex.Path}: {ex.Message}";
            return null;
        }
    }

    private async Task<string> BuildCapabilityCatalogAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var pool = await _modelSelector.ListPoolAsync(context.TeamId, context.AllowedModelIds, cancellationToken).ConfigureAwait(false);

        // P3 — the team's persona library, so the brain authors a per-agent persona by slug. Mapped to the minimal
        // render noun (slug/name/description); empty when the team has no personas (the section is then omitted).
        // CLAMPED to the operator's allowed agent pool (empty = all) so the brain only authors slugs it'll be allowed to
        // dispatch — the persona analogue of ListPoolAsync clamping the model catalog (the dispatch gate is the floor).
        var personas = (await _agentDefinitions.ListAsync(context.TeamId, cancellationToken).ConfigureAwait(false))
            .Where(p => context.AllowedAgentDefinitionIds is not { Count: > 0 } pool || pool.Contains(p.Id))
            .Select(p => new PersonaCatalogInfo(p.Slug, p.Name, p.Description)).ToList();

        return RenderCatalog(_harnesses.All, pool, personas) + RenderBoundRepositories(context);
    }

    /// <summary>
    /// Render the capability catalog the brain authors against (P1): every registered harness + the model providers it
    /// can drive, and the run's credentialed pool models + each model's provider — so the model picks a provider-compatible
    /// (harness, model) pair on purpose. Internal + static so the rendering is unit-pinned without an LLM/DB.
    ///
    /// <para>KNOWN LIMITATION (reviewed): a model NAME is unique only PER credential, so the same name can list under two
    /// different-provider rows (both shipped harnesses support "Custom"). The brain authors a name only (no provider
    /// disambiguator yet — a P2 schema add), and <c>ResolveDispatchAsync</c> tie-breaks by row id, so the dispatched
    /// provider may differ from the catalog line the brain reasoned about. Non-fatal: the authoring-time clamp +
    /// run-time reconciler bind a compatible harness regardless. Surfacing the provider per name is the P2 follow-up.</para>
    /// </summary>
    internal static string RenderCatalog(IReadOnlyList<IAgentHarness> harnesses, IReadOnlyList<PoolModelInfo> pool, IReadOnlyList<PersonaCatalogInfo>? personas = null) =>
        CapabilityCatalog.Render(harnesses, pool, personas);

    /// <summary>
    /// The run's BOUND repositories, appended to the capability catalog so an <c>agents[].repositoryId</c> proposal has
    /// an EXACT id to cite — without this the model can only guess a name (the exact miss that killed a real run:
    /// <c>"repositoryId": "backend"</c>, schema-valid, bind-dead). Rendered straight off the run profile (no DB read):
    /// the primary repo's id + each related repo's id/alias/access. A repo-less (analysis-only) run appends nothing.
    /// Internal for direct prompt pinning.
    /// </summary>
    internal static string RenderBoundRepositories(SupervisorTurnContext context)
    {
        var primary = context.AgentProfile?.RepositoryId;
        var related = ParseRelatedRepositories(context.AgentProfile?.RelatedRepositories);

        if (primary is null && related.Count == 0) return "";

        var builder = new StringBuilder();

        builder.AppendLine().AppendLine("Bound repositories (agents[].repositoryId / targetRepos[].repositoryId must cite one of these EXACT ids — never a name):");
        if (primary is { } p) builder.AppendLine($"- {p} — the run's primary repository");
        foreach (var r in related) builder.AppendLine($"- {r.Id}{(string.IsNullOrWhiteSpace(r.Alias) ? "" : $" (alias '{r.Alias}')")}{(string.IsNullOrWhiteSpace(r.Access) ? "" : $" — {r.Access}")}");

        return builder.ToString();
    }

    /// <summary>Minimal read of the profile's raw related-repos array ({repositoryId, alias?, access?}) for catalog rendering — defensive: a malformed element is skipped, never a crash (the executor's authoring path stays the real parser).</summary>
    private static IReadOnlyList<(string Id, string? Alias, string? Access)> ParseRelatedRepositories(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Array } array) return Array.Empty<(string, string?, string?)>();

        var repos = new List<(string, string?, string?)>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("repositoryId", out var id) || id.ValueKind != JsonValueKind.String) continue;

            repos.Add((id.GetString()!,
                item.TryGetProperty("alias", out var alias) && alias.ValueKind == JsonValueKind.String ? alias.GetString() : null,
                item.TryGetProperty("access", out var access) && access.ValueKind == JsonValueKind.String ? access.GetString() : null));
        }

        return repos;
    }

    private static StructuredLLMCompletionRequest BuildRequest(SupervisorTurnContext context, ModelPoolPick pick, string catalog) => new()
    {
        // The model id AND the credential both come from the one chosen pool row — nothing guessed, nothing hidden.
        Model = pick.ModelId,
        SystemPrompt = SystemPrompt,
        UserPrompt = BuildUserPrompt(context, catalog),
        JsonSchema = SupervisorDecisionSchema.ResponseSchema,
        MaxOutputTokens = 4096,
        Temperature = 0.2,
        Credential = pick.Credential,
    };

    /// <summary>Internal test accessor (InternalsVisibleTo) — pins the context→prompt framing directly, without a real LLM round-trip.</summary>
    internal static string BuildUserPromptForTest(SupervisorTurnContext context, string catalog = "") => BuildUserPrompt(context, catalog);

    private static string BuildUserPrompt(SupervisorTurnContext context, string catalog)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"Goal: {context.Goal}");
        builder.AppendLine($"Turn: {context.TurnNumber}");
        builder.AppendLine();

        // The operator's acceptance criteria — the definition of done the supervisor must target (a yardstick, NOT an
        // executable check). Null / empty ⇒ no block ⇒ byte-identical prompt.
        if (context.AcceptanceCriteria is { Count: > 0 } criteria)
        {
            builder.AppendLine("Acceptance criteria (the operator's definition of done — drive the work to meet these before declaring success):");
            foreach (var c in criteria) builder.AppendLine($"- {c}");
            builder.AppendLine();
        }

        // An independent critic reviewed the previous draft of THIS turn's decision and asked for a revision. Fold its
        // critique in so the model improves the decision. Set ONLY on the critic decorator's one bounded re-decide
        // (null on the first decide ⇒ byte-identical prompt).
        if (!string.IsNullOrWhiteSpace(context.ReviewerCritique))
        {
            builder.AppendLine("An independent reviewer critiqued your previous decision for this turn. Revise the decision to address this critique:");
            builder.AppendLine(context.ReviewerCritique.Trim());
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(catalog))
        {
            builder.AppendLine(catalog.TrimEnd());
            builder.AppendLine();
        }

        if (context.PriorDecisions.Count == 0)
        {
            builder.AppendLine("No prior decisions yet — this is the first turn. Start by planning (decompose the goal into subtasks) — UNLESS the goal context shows THIS EXACT ask was already completed and verified by prior work (the same change shipped/merged with passing tests); then do NOT re-plan it: 'stop' to recognise completion, or 'ask_human' to clarify the new ask.");
        }
        else
        {
            // P1.2 auto-compact: with a rolling digest, the folded head renders as the digest block and only the
            // decisions AFTER it render raw — the prompt stops growing with the run. Bounds/recitation still read
            // the COMPLETE tape (the filter is prompt-grain only).
            var rendered = context.TapeSummary is { } tape ? context.PriorDecisions.Where(d => d.Sequence > tape.UpToSequence).ToList() : context.PriorDecisions;

            if (context.TapeSummary is { } digest)
            {
                builder.AppendLine($"Earlier progress (auto-compacted digest of the run's older decisions, up to ledger sequence {digest.UpToSequence}):");
                builder.AppendLine(digest.Text.Trim());
                builder.AppendLine();
            }

            // The index of the MOST RECENT spawn/retry — the one whose agent results the decider should act on
            // (a later retry's results supersede the original spawn's). Marked so the model targets the freshest.
            var latestSpawnIndex = LastIndexOf(rendered, d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind));

            // P1e ladder: the index of the LATEST plan in the rendered window. Every EARLIER plan was replaced by a
            // re-plan — its full subtask payload is dead weight (the LIVE plan is recited at the tail with per-item
            // states, and its frontier is shown), so only the latest plan renders full; superseded plans collapse to a
            // one-line digest. The single biggest source of the run's monotone prompt growth (each re-plan added a full payload).
            var latestPlanIndex = LastIndexOf(rendered, d => d.DecisionKind == SupervisorDecisionKinds.Plan);

            builder.AppendLine("Prior decisions (in order, with their recorded outcomes):");
            for (var i = 0; i < rendered.Count; i++)
                AppendPriorDecision(builder, rendered[i], isLatestSpawn: i == latestSpawnIndex, isSupersededPlan: rendered[i].DecisionKind == SupervisorDecisionKinds.Plan && i != latestPlanIndex);

            AppendDependencyFrontier(builder, context);
        }

        // S8 RECITATION (the Manus lesson): restate the CURRENT plan with live per-item states at the prompt TAIL —
        // the recency-biased position — so a long run never loses the plan under a growing prior-decision log.
        // Null (no plan yet) ⇒ byte-identical prompt.
        if (SupervisorRecitation.Render(context.PriorDecisions) is { } recitation)
        {
            builder.AppendLine();
            builder.AppendLine(recitation);
        }

        // P3.5 — the BUDGET recitation, the same prompt-tail position: the model sees its own realized spend vs. the
        // cap + a per-lane breakdown, so it can self-moderate BEFORE the server ever has to force-stop it. Null when
        // no cost cap is set ⇒ byte-identical prompt for the common uncapped run.
        if (SupervisorBudgetRecitation.Render(context.MaxCostUsd, context.AgentExecutionSpendUsd, context.BrainPlaneSpendUsd, context.BrainPlaneSpendByKind) is { } budget)
        {
            builder.AppendLine();
            builder.AppendLine(budget);
        }

        builder.AppendLine();
        builder.AppendLine("Choose the single next action. After planning, spawn agents over the planned subtask ids; once their results are recorded, INSPECT each agent's status and error in the most recent spawn OR retry outcome above, RETRY any subtask that failed or did not satisfy the goal (optionally with a revised instruction), then merge the successful results, then stop. Return ONLY the schema-constrained JSON.");

        return builder.ToString();
    }

    /// <summary>
    /// Render the plan's dependency FRONTIER (loopability — the server enforces <c>DependsOn</c> ordering at spawn): the
    /// subtasks READY to spawn now (every dependency accepted) and those still BLOCKED on a dependency, so the model
    /// spawns in DAG order rather than racing. Nothing for a flat plan (no <c>DependsOn</c>) — byte-identical to before.
    /// </summary>
    private static void AppendDependencyFrontier(StringBuilder builder, SupervisorTurnContext context)
    {
        var (ready, blocked) = SupervisorDependencyGate.Frontier(context);

        if (ready.Count == 0 && blocked.Count == 0) return;

        builder.AppendLine();
        builder.AppendLine("Dependency frontier (the server spawns subtasks in DependsOn order — a blocked subtask is DEFERRED until its dependencies are accepted, so spawn only ready ones):");

        if (ready.Count > 0) builder.AppendLine($"    ready to spawn now: {string.Join(", ", ready)}");

        foreach (var b in blocked)
            builder.AppendLine($"    blocked: {b.Id} (waiting on {string.Join(", ", b.WaitingOn)})");
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
    /// <summary>
    /// Render an agent's GIT GROUND-TRUTH artifacts (the captured changed files + the pushed branch) under its result
    /// line, so the brain verifies completion + detects cross-agent file OVERLAP against the real diff — not the
    /// self-reported summary. Bounded to a capped file sample to stay token-cheap. Reads only the TOP-LEVEL fields
    /// (the single-repo diff, or a multi-repo run's PRIMARY) — the per-repo RepositoryResults are deliberately NOT
    /// rendered (kept single-repo-identical, no multi-repo bloat). No artifacts (a no-op / pure-analysis agent) → nothing.
    /// </summary>
    private static void AppendAgentArtifacts(StringBuilder builder, SupervisorAgentResult result)
    {
        const int maxFiles = 12;

        var hasFiles = result.ChangedFiles.Count > 0;
        var hasBranch = !string.IsNullOrEmpty(result.ProducedBranch);

        if (!hasFiles && !hasBranch) return;

        var parts = new List<string>();

        if (hasFiles)
        {
            var shown = string.Join(", ", result.ChangedFiles.Take(maxFiles));
            var more = result.ChangedFiles.Count > maxFiles ? $" (+{result.ChangedFiles.Count - maxFiles} more)" : "";
            parts.Add($"{result.ChangedFiles.Count} changed file(s): {shown}{more}");
        }

        if (hasBranch) parts.Add($"branch {result.ProducedBranch}");

        builder.AppendLine($"      produced — {string.Join("; ", parts)}");
    }

    private static void AppendPriorDecision(StringBuilder builder, SupervisorPriorDecision prior, bool isLatestSpawn, bool isSupersededPlan)
    {
        // P1e ladder: a plan REPLACED by a later re-plan collapses to a one-line digest — its full subtask payload is
        // dead weight (the live plan is recited at the tail; its frontier is shown). Keep the subtask ids so the model
        // still sees the re-plan history + how the shape changed, without paying for N full payloads. Pure over the
        // payload (replay re-derives the identical line). The LATEST plan still renders full below.
        if (isSupersededPlan)
        {
            var subtaskIds = SupervisorOutcome.ReadPlanSubtasks(prior.PayloadJson).Select(s => s.Id).ToList();
            var ids = subtaskIds.Count > 0 ? $" [{string.Join(", ", subtaskIds)}]" : "";

            builder.AppendLine($"- plan (superseded by a later re-plan): {subtaskIds.Count} subtask(s){ids}");
            return;
        }

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
                AppendAgentArtifacts(builder, r);
                AppendUnitAcceptanceVerdict(builder, r);
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

    /// <summary>
    /// Render a unit's per-unit OBJECTIVE acceptance verdict (loopability slice 3) under its result line so the brain
    /// ACTS on it — THREE-way, on the shared failure classification: a PASSED unit's work is server-verified; a
    /// WORK-classed failure names the precise subtask to RETRY; an INFRA-classed failure (grader error, half-authored
    /// spec, publish failure with work present — <see cref="Agents.AgentAcceptanceContract.IsInfraFailure"/>) says the
    /// CHECK could not run, NOT that the work is wrong — a retry re-bills an agent and fails identically forever, the
    /// exact loop that marched a real run into its no-progress kill. Absent verdict (no per-unit contract / a deferred
    /// multi-repo unit) → nothing, byte-identical to before.
    /// </summary>
    private static void AppendUnitAcceptanceVerdict(StringBuilder builder, SupervisorAgentResult result)
    {
        if (result.AcceptancePassed is not { } passed) return;

        if (passed)
        {
            // P4-1: the agent itself reported failure, yet the objective check passed — previously silent (this line
            // only ever read AcceptancePassed, never Status), so a passed-but-self-reported-failed unit rendered
            // identically to a clean pass with no signal that the agent disagreed with its own verified result.
            // Reads Status directly (not the newer Contradiction field) so this applies to every row, old or new.
            builder.AppendLine(result.Status == "Failed"
                ? "      acceptance PASSED — this unit's definition-of-done check ran green, even though the agent itself reported failure. The work is objectively fine; do NOT retry this subtask, merge it."
                : "      acceptance PASSED — this unit's definition-of-done check ran green against its branch; the work is objectively verified.");
            return;
        }

        builder.AppendLine(Agents.AgentAcceptanceContract.IsInfraFailure(result.AcceptanceDetail, SupervisorOutcome.ResultShowsWork(result))
            ? $"      acceptance UNVERIFIED ({result.AcceptanceDetail}) — the CHECK could not run (grader/spec/publish infrastructure), NOT a verdict on the work; the produced work is preserved on this unit. Do NOT retry the agent — another pass cannot fix the check. Re-plan this item with a check its agent can satisfy, or ask a human to rule."
            : $"      acceptance FAILED — this unit's own check did NOT pass ({result.AcceptanceDetail}); its branch is NOT mergeable. RETRY this exact subtask (do not merge it).");
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

    /// <summary>Fail-closed terminal stop when the model's response did NOT conform to the decision schema (it did not parse to a decision, or carried no kind) — a model-side miss handled the SAME way as no-model and an unknown kind (the projector already maps an unknown verb to stop): a clean one-turn no-op stop, never an unhandled crash mid-run. Keeps the decider's "fail closed, never crash" contract WHOLE — a degraded/flaky gateway reply stops the run cleanly rather than faulting the durable engine. Deterministic so a replay re-derives it. The binding detail (when known) rides the summary so the journal NAMES the miss — a schema↔type drift is diagnosable from the run page, not just the database.</summary>
    private static SupervisorDecision NonConformantStop(string? bindError = null) => new()
    {
        Kind = SupervisorDecisionKinds.Stop,
        PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = SupervisorStopPayload.NonConformantOutcome, Summary = $"The supervisor model returned a response that did not conform to the decision schema — stopping cleanly rather than crashing the run.{(string.IsNullOrWhiteSpace(bindError) ? "" : $" ({bindError})")}" }, AgentJson.Options),
    };

    /// <summary>Fail-closed terminal stop when the operator did not pick a required supervisor brain model (<c>supervisorModelId</c>). The decision is the operator's — the supervisor never guesses its own model. Deterministic so a replay re-derives it.</summary>
    private static SupervisorDecision NoBrainModelStop() => new()
    {
        Kind = SupervisorDecisionKinds.Stop,
        PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "no-model", Summary = "No supervisor brain model is selected — set 'supervisorModelId' to a credentialed, enabled model." }, AgentJson.Options),
    };

    /// <summary>Fail-closed terminal stop when no structured-LLM provider is registered — the lane is on but no model is configured. Deterministic so a replay re-derives it.</summary>
    private static SupervisorDecision NoModelStop() => new()
    {
        Kind = SupervisorDecisionKinds.Stop,
        PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "no-model", Summary = "No structured-output LLM provider is configured for the supervisor lane." }, AgentJson.Options),
    };

    /// <summary>Fail-closed terminal stop when the team's credentialed-model POOL has no model the brain can run (none configured, or none within the allowed pool / pin) — it stops cleanly rather than guessing a model or key. The pool analogue of <see cref="NoModelStop"/>.</summary>
    private static SupervisorDecision NoPoolModelStop() => new()
    {
        Kind = SupervisorDecisionKinds.Stop,
        PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "no-model", Summary = "No model is available in the team's credentialed model pool — add a credentialed, enabled model for the supervisor's provider, or widen the allowed model pool." }, AgentJson.Options),
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
        "When you spawn, you MAY optionally author a per-agent 'agents[]' override (one entry per subtask id) to give " +
        "each agent a DISTINCT role, goal, repo subset, harness, model, persona, or a LOWER autonomy — use it when the " +
        "subtasks need different specialisations (e.g. a backend implementer and a separate reviewer); omit 'agents[]' to " +
        "fan out homogeneous agents (the default). To give an agent a specialist persona, set 'agentDefinition' to a " +
        "persona SLUG from the capability catalog. The server CLAMPS every per-agent field to the operator's grant: a " +
        "repo subset must lie within the run's bound repos and autonomy is never raised above the run's ceiling. " +
        "When you 'plan', you MAY optionally group your subtasks into named 'phases' (e.g. Investigate / Implement / " +
        "Review) — each phase lists the subtask ids it covers and an OPTIONAL objective 'acceptance' check — so the run " +
        "reads as coherent stages; author phases when the work has DISTINCT stages, and omit 'phases' for a flat subtask " +
        "plan (the default). " +
        "When you 'stop', you MAY optionally author an objective 'acceptance' definition-of-done — an argv 'command' the " +
        "server RUNS against the integrated result to verify the goal is met (it is AND-ed with the operator's own " +
        "acceptance floor, never replaces it) — but author it ONLY when the goal itself names a concrete runnable check " +
        "(e.g. it explicitly says to verify with a specific test/command); otherwise OMIT 'acceptance' and rely on the " +
        "operator's floor. Never author a command you are not confident the integrated result passes. " +
        "Before planning, check whether the goal has ALREADY been delivered: if the context shows THIS EXACT ask was " +
        "already completed and verified by prior work (the SAME change shipped/merged with passing tests — not merely " +
        "related work), do NOT re-plan or redo it — 'stop' to recognise completion, or 'ask_human' to clarify what new " +
        "work is wanted. A follow-up that asks for NEW or ADDITIONAL work — even building on prior turns, or touching the " +
        "same file/endpoint/area as prior work — is NOT redundant; plan it. " +
        "If a merge reports INTEGRATION CONFLICTED, the agents' work could not be auto-combined; you may spawn ONE agent " +
        "to reconcile the preserved branches, build, and run the tests (then merge again), or stop to leave the conflict " +
        "for a human — never accept an unverified resolution. " +
        "If the context shows a PLAN-CONFIRMATION question (it asks the human to confirm a plan version) that was just " +
        "answered: an approving answer means the plan is confirmed — proceed to 'spawn' its subtasks; ANY other answer " +
        "is the operator's revision feedback — author a REVISED 'plan' that incorporates it (keep what they liked, " +
        "change what they asked), never spawn the rejected plan unchanged. " +
        "You never name node types, run ids, or graph wiring — only the action + its payload. " +
        "Return ONLY the schema-constrained JSON.";
}
