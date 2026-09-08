using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>The operator-owned configuration for one manually added model row.</summary>
public sealed record CredentialedModelConfiguration
{
    public required string ModelId { get; init; }
    public string? DisplayName { get; init; }
    public ModelPrice? Price { get; init; }
    public int? ContextWindowTokens { get; init; }
}
