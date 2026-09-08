using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Review;

/// <summary>The trusted identity evidence carried from a model-backed producer into its evaluator. A configured row or alias proves routing intent; only <see cref="ObservedModel"/> is evidence about the model that answered.</summary>
public sealed record ReviewModelIdentity
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ModelCredentialModelId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfiguredModel { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservedModel { get; init; }
}

/// <summary>Whether two provider-observed identities establish that an evaluator used a different backing model.</summary>
public enum ReviewModelIndependence
{
    Unknown = 0,
    DistinctBackingModel = 1,
    SameBackingModel = 2,
}

