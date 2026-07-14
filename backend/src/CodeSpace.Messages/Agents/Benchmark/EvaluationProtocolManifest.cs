using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>The three-tier evaluation governance (v4.2 Q contract): open development suites are visible to everyone; shadow evaluation informs; SEALED QUALIFICATION tasks/oracles/traces are inaccessible to implementers and agents, and only sealed results back a capability claim.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvaluationTier
{
    OpenDevelopment,
    ShadowEvaluation,
    SealedQualification,
}

/// <summary>
/// THE frozen identity of one evaluation protocol run (v4.2 Q contract): what was measured, with what, over what —
/// hashed to BYTES, never to references (a fixture edited under an unchanged ref must change this manifest, the
/// M1a freeze hole closed). <see cref="Digest"/> is the canonical self-describing hash consumers pin; any component
/// change (suite bytes, model, evaluator, commit, policy) is a NEW protocol whose results never mix with the old.
/// </summary>
public sealed record EvaluationProtocolManifest
{
    public required EvaluationTier Tier { get; init; }

    /// <summary>canonical digest over the suite's BYTES — every task definition + every fixture file's content (see the loader).</summary>
    public required string SuiteContentHash { get; init; }

    public required int TaskCount { get; init; }

    /// <summary>The exact model id driven (the pinned bundle's identity — never "latest").</summary>
    public required string ModelId { get; init; }

    public required string EvaluatorVersion { get; init; }

    /// <summary>The CodeSpace commit the harness ran from.</summary>
    public required string CodeSpaceCommit { get; init; }

    public required int CompletionPolicyVersion { get; init; }

    /// <summary>The manifest's own canonical identity — what a qualification report cites.</summary>
    public string Digest() => Contracts.ContractHashing.Hash(System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })).RootElement);
}
