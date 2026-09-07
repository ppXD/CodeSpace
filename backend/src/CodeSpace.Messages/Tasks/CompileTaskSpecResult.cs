using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Tasks;

/// <summary>Spec proposals, server-observed source evidence, and model call outcomes. Source review does not establish execution compatibility, a passed check, or authority to run.</summary>
public sealed record CompileTaskSpecResult
{
    /// <summary>The compiled suggestions, or null when unavailable — the caller renders nothing, never an empty scaffold.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskSpecSuggestion? Suggestion { get; init; }

    /// <summary>Legacy coarse observation flag. Use RepositoryObservation to distinguish unreadable, absent, and observed empty repositories.</summary>
    public bool Grounded { get; init; }

    /// <summary>What the server actually observed. Missing on legacy replies means Unknown, never an empty repository.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskSpecRepositoryObservation? RepositoryObservation { get; init; }

    /// <summary>Both generation and semantic-review calls, including failures and provider-reported usage. Missing usage is unknown, not free.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TaskSpecModelCall>? ModelCalls { get; init; }
}

/// <summary>One compiled suggestion set with a preview-only evidence assessment. Applying a field requires separate execution compatibility; durable launch provenance is not established by this reply.</summary>
public sealed record TaskSpecSuggestion
{
    /// <summary>Source-supported candidate argv for existing consumers. Empty for unknown or contradicted evidence. The route adapter must independently accept this input before adoption; no execution is claimed.</summary>
    public IReadOnlyList<string> AcceptanceChecks { get; init; } = Array.Empty<string>();

    /// <summary>The proposed argv, its assessed source and the evidence behind adoption. Supported is a model assessment, not an execution receipt or authority grant.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskSpecAcceptanceProposal? AcceptanceProposal { get; init; }

    /// <summary>Suggested definition-of-done bullets (the launch's <c>AcceptanceCriteria</c>). Prompt-rendered guidance, never executed.</summary>
    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = Array.Empty<string>();

    /// <summary>Suggested delivery preference (the launch's <c>DeliverySpec.OpenPullRequest</c>). Null = the goal implies no opinion.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? OpenPullRequest { get; init; }

    /// <summary>Suggested PR target branch (the launch's <c>DeliverySpec.TargetBranch</c>) — only when the goal names one explicitly. Null = the repository default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetBranch { get; init; }

    /// <summary>One short line: why these suggestions — shown on the suggestion card. Includes an honest note when a suggested check was dropped for failing authoring validation.</summary>
    public required string Rationale { get; init; }

    /// <summary>The model's confidence in the suggestion set, clamped 0..1 — the FE may de-emphasize low-confidence cards.</summary>
    public double Confidence { get; init; }
}
