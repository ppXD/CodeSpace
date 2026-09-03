using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Resolves a (structured-LLM client, pool model) pair that MATCH for a team — the shared in-process-plane resolution
/// the planner and the effort classifier both use. It iterates the registered structured clients and returns the first
/// whose provider the team has a credentialed, enabled model for, so a team whose pool is ALL one provider (e.g. all
/// Custom-gateway models, or all OpenAI) resolves the RIGHT client + model rather than a provider-blind first pick that
/// would then find no model and fail.
///
/// <para>Null when no registered structured provider has a team model — the caller decides (the planner throws a clean
/// "no model" error; the effort classifier degrades to the heuristic baseline). The supervisor decider does NOT use this
/// — its brain model is chosen by row id and its client matched by THAT model's provider — so a Custom brain already
/// flows through once <c>"Custom"</c> is a registered structured provider.</para>
/// </summary>
public static class InProcessStructuredModel
{
    /// <summary>
    /// D2 — the ONE cost ceiling every CHEAP in-process caller passes to <see cref="ResolveAsync"/>: the launch effort
    /// classifier, model capability tiering, the nightly lesson distiller, the spec-preview compiler. Each asks ONE
    /// short, schema-bounded question whose answer a human reviews or which is merely advisory, so none of them needs
    /// the team's <see cref="ModelCapabilityTier.Frontier"/> model — before this ceiling every one of them got it, because
    /// the pool's unpinned ladder ranks the EFFECTIVE tier DESCENDING ("auto = the strongest available brain").
    /// <see cref="ModelCapabilityTier.Strong"/> is the ceiling rather than <see cref="ModelCapabilityTier.Basic"/> because
    /// these calls still need reliable schema adherence — the goal is to stop reaching for the priciest tier, not to
    /// route the plane's judgment onto its weakest model.
    ///
    /// <para>The DELIBERATE non-callers: the supervisor brain, the workflow planner, the critics, the rubric judges and
    /// the agents themselves. Their output is the product, is not human-reviewed before it acts, or is the very thing
    /// capability buys — so they keep the unceilinged "strongest available" ladder.</para>
    /// </summary>
    public const ModelCapabilityTier CheapBrainCeiling = ModelCapabilityTier.Strong;

    /// <summary>
    /// L4 pool failover: EVERY registered structured provider the team has a model for becomes a candidate, in registry
    /// order. One candidate ⇒ returned directly (byte-identical to before). Several ⇒ the returned client is a
    /// <see cref="FailoverStructuredClient"/> over all of them and the returned pick is the FIRST candidate's — so a
    /// transient / rate-limit fault on the first provider hops to the next with its own credential, and the answering
    /// model rides <see cref="StructuredLLMCompletion.Model"/> (callers stamp provenance from THAT, never the pick).
    /// The operator-pinned row path (<see cref="ResolveByRowIdAsync"/>) never fails over: an explicit pin resolves verbatim.
    ///
    /// <para>D2: <paramref name="tierCeiling"/> (null = the unceilinged "strongest available" ladder, so every existing
    /// caller is byte-identical) is applied PER CANDIDATE PROVIDER, which is how the ceiling composes with pool failover
    /// — each provider contributes its own cheapest-satisfying row, and a provider whose pool has nothing under the
    /// ceiling still contributes its unceilinged pick rather than dropping out of the failover chain. So a ceiling can
    /// never shorten the chain, and a hop lands on a ceilinged row wherever one exists.</para>
    /// </summary>
    public static async Task<(IStructuredLLMClient Client, ModelPoolPick Pick)?> ResolveAsync(ILLMClientRegistry clients, IModelPoolSelector models, Guid teamId, CancellationToken cancellationToken, ModelCapabilityTier? tierCeiling = null)
    {
        var candidates = new List<(IStructuredLLMClient Client, ModelPoolPick Pick)>();

        foreach (var client in clients.All.OfType<IStructuredLLMClient>())
        {
            var pick = await models.SelectAsync(teamId, client.Provider, allowedModels: null, pinnedModel: null, tierCeiling, cancellationToken).ConfigureAwait(false);

            if (pick != null) candidates.Add((client, pick));
        }

        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0],
            _ => (new FailoverStructuredClient(candidates), candidates[0].Pick),
        };
    }

    /// <summary>
    /// Resolve an OPERATOR-PINNED brain model by its <c>ModelCredentialModel</c> row id → its (structured client, pick) —
    /// the same row-id path the supervisor decider uses, so a pinned planner brain and a pinned supervisor brain resolve
    /// identically. The client is the structured one whose provider matches the PINNED model's own provider. <c>null</c>
    /// when the row is missing / disabled / revoked / cross-team OR no registered structured client serves its provider
    /// (the caller fails closed — an explicit pin must resolve verbatim, never silently fall back to another model).
    /// </summary>
    public static async Task<(IStructuredLLMClient Client, ModelPoolPick Pick)?> ResolveByRowIdAsync(ILLMClientRegistry clients, IModelPoolSelector models, Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken)
    {
        if (await models.ResolveByRowIdAsync(teamId, modelCredentialModelId, cancellationToken).ConfigureAwait(false) is not { } pick)
            return null;

        var client = clients.All.OfType<IStructuredLLMClient>()
            .FirstOrDefault(c => string.Equals(c.Provider, pick.Credential.Provider, StringComparison.OrdinalIgnoreCase));

        return client == null ? null : (client, pick);
    }
}
