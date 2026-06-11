namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// One agent harness registered in the engine — the wire-protocol kind an <c>agent.code</c> node selects
/// (e.g. <c>codex-cli</c>, <c>claude-code</c>), its version, and the model ids it advertises. Deployment-level
/// and team-agnostic (every team sees the same set), so this carries no team scope. Feeds the editor's
/// harness picker, and <see cref="Models"/> seeds the model field's suggestions for the chosen harness.
/// </summary>
public sealed record HarnessSummary
{
    public required string Kind { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<string> Models { get; init; }

    /// <summary>
    /// The model-credential provider tags this harness can authenticate with (its
    /// <c>IModelCredentialProjector.SupportedProviders</c>), or empty when the harness implements no projector.
    /// Lets the editor's credential picker show ONLY credentials this harness can drive — e.g. claude-code
    /// (Anthropic wire format) accepts Anthropic + Custom, codex-cli (OpenAI format) accepts OpenAI/…/Custom.
    /// </summary>
    public required IReadOnlyList<string> SupportedProviders { get; init; }
}
