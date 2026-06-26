namespace CodeSpace.Messages.Dtos.ModelCredentials;

/// <summary>
/// One model on a credential's maintained list, as shown in the picker — the secret-free pick-from-list surface.
/// Provenance (manual vs reflected) is intentionally NOT here: the picker needs only id / label / availability,
/// not where the row came from.
/// </summary>
public sealed record CredentialedModelSummary
{
    /// <summary>The row id — the stable handle the picker selects and a delete targets.</summary>
    public required Guid Id { get; init; }

    /// <summary>The wire model id the harness/decider passes (e.g. "claude-sonnet-4-5").</summary>
    public required string ModelId { get; init; }

    /// <summary>Operator-friendly label; null → show <see cref="ModelId"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Whether the model is part of the usable pool (a disabled row is hidden from selection but kept).</summary>
    public required bool Enabled { get; init; }

    /// <summary>The operator-marked default for an "auto" run — at most one per credential. The UI shows it as the starred model.</summary>
    public bool IsDefault { get; init; }
}
