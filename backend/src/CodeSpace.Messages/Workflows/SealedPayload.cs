namespace CodeSpace.Messages.Workflows;

/// <summary>
/// A payload prepared for a column other readers see: <see cref="PublicJson"/> with every embedded secret value
/// masked, and the exact copy encrypted in <see cref="Ciphertext"/>. Null ciphertext means nothing was masked, so the
/// public copy is itself exact — the shape every payload without a secret keeps.
/// </summary>
public sealed record SealedPayload(string PublicJson, string? Ciphertext);

/// <summary>
/// A sealed payload opened for execution: the exact <see cref="Json"/> and the <see cref="Secrets"/> it embeds, which
/// the consumer keeps treating as secrets wherever it persists what it derives from the payload. Empty secrets for a
/// payload that was never sealed.
/// </summary>
public sealed record OpenedPayload(string Json, IReadOnlyList<string> Secrets);
