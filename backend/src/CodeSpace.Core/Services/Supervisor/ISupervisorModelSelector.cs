using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Picks the supervisor BRAIN's model + its backing credential from the team's credentialed-model POOL — the model
/// analogue of how the agents it spawns pick theirs. EVERYTHING flows from the pool: a structured-output-capable,
/// ENABLED model under an ACTIVE credential, within the operator's allowed pool (empty = ALL the team's models),
/// PINNED to a specific model when the operator set one, preferring a supervisor-recommended model. There is no env
/// "system credential" and no default model — the model id AND the key always come from ONE pool row. <c>null</c> =
/// nothing in the pool qualifies → the brain fails closed (it never runs a guessed model on a hidden key).
/// </summary>
public interface ISupervisorModelSelector
{
    /// <summary>
    /// Select the brain's model for <paramref name="provider"/> (the structured client it will call): the team's
    /// enabled, structured-capable credentialed models under an active credential of that provider, bounded by
    /// <paramref name="allowedModels"/> (null/empty = all), pinned to <paramref name="pinnedModel"/> if set, preferring
    /// a supervisor-recommended one. Null when nothing qualifies.
    /// </summary>
    Task<SupervisorModelPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken);
}

/// <summary>The brain's resolved pick: a model id from the pool + the decrypted credential of the row that backs it. Transient — carries a secret, never persisted.</summary>
public sealed record SupervisorModelPick
{
    public required string ModelId { get; init; }

    public required ResolvedModelCredential Credential { get; init; }
}
