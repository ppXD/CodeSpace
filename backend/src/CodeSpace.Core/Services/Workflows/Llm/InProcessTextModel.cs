using CodeSpace.Core.Services.Agents.ModelCredentials;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Resolves a provider-neutral text client and a model from the same team's configured pool. The registry is a
/// capability catalog, not a provider preference: selecting its first entry before consulting the pool makes a team
/// whose models all belong to a later provider appear to have no model at all. Iterate providers and return the first
/// pair that actually resolves, mirroring <see cref="InProcessStructuredModel"/> without requiring structured output.
/// Text-only clients retain their existing preference over structured-capable fallbacks; ordering is otherwise the
/// registry's stable order.
/// </summary>
public static class InProcessTextModel
{
    public static async Task<(ILLMClient Client, ModelPoolPick Pick)?> ResolveAsync(ILLMClientRegistry clients, IModelPoolSelector models, Guid teamId, string? pinnedModel, CancellationToken cancellationToken)
    {
        foreach (var client in clients.All.OrderBy(client => client is IStructuredLLMClient ? 1 : 0))
        {
            var pick = await models.SelectAsync(teamId, client.Provider, allowedModels: null, pinnedModel, cancellationToken).ConfigureAwait(false);
            if (pick is not null) return (client, pick);
        }

        return null;
    }
}
