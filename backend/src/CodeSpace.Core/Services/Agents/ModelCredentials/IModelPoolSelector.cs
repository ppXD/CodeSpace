using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// Picks an in-process LLM call's model + its backing credential ENTIRELY from the team's credentialed-model POOL (the
/// <c>ModelCredentialModel</c> rows S1–S3 build) — the one mechanism every in-process caller shares: the supervisor
/// decider, the workflow planner, the <c>llm.complete</c> node, the supervisor synthesis. A qualifying row is an
/// ENABLED model under an ACTIVE credential of the requested provider, optionally narrowed to structured-output-capable
/// (the decider/planner need it; a free-text reduce does not), bounded by the operator's allowed-models pool
/// (null/empty = all the team's models), pinned to one model when the caller has one, preferring a supervisor-
/// recommended model. The chosen row's backing credential is decrypted just-in-time. <c>null</c> = nothing qualifies →
/// the caller fails closed. There is NO env "system credential" fallback and NO default model anywhere on this path.
/// </summary>
public interface IModelPoolSelector
{
    /// <summary>
    /// Select a model + credential for <paramref name="provider"/> (the client the caller will invoke), narrowed to
    /// structured-capable when <paramref name="requireStructured"/>, bounded by <paramref name="allowedModels"/>
    /// (null/empty = all), pinned to <paramref name="pinnedModel"/> if set, preferring a recommended one. Null when
    /// nothing qualifies. Provider + model-id matching is case-insensitive (parity with the agent-side resolver + the
    /// spawn clamp).
    /// </summary>
    Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, bool requireStructured, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken);
}

/// <summary>The resolved pick: a model id from the pool + the decrypted credential of the row that backs it. Transient — carries a secret, never persisted.</summary>
public sealed record ModelPoolPick
{
    public required string ModelId { get; init; }

    public required ResolvedModelCredential Credential { get; init; }
}
