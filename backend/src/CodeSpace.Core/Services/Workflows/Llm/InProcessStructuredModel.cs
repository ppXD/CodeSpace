using CodeSpace.Core.Services.Agents.ModelCredentials;

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
    public static async Task<(IStructuredLLMClient Client, ModelPoolPick Pick)?> ResolveAsync(ILLMClientRegistry clients, IModelPoolSelector models, Guid teamId, CancellationToken cancellationToken)
    {
        foreach (var client in clients.All.OfType<IStructuredLLMClient>())
        {
            var pick = await models.SelectAsync(teamId, client.Provider, allowedModels: null, pinnedModel: null, cancellationToken).ConfigureAwait(false);

            if (pick != null) return (client, pick);
        }

        return null;
    }
}
