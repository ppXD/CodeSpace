using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Tasks;

/// <summary>
/// P5-7: the spec compiler's reply — a nullable suggestion (null = no model available / the model path degraded /
/// the model had nothing useful to suggest; the composer simply shows nothing) plus whether repo grounding was
/// available to it. Pure data nouns (Rule 18.1); the suggestion's fields mirror the launch surface 1:1 so the FE
/// pre-fills EXISTING fields — there is deliberately no field here the launch cannot carry.
/// </summary>
public sealed record CompileTaskSpecResult
{
    /// <summary>The compiled suggestions, or null when unavailable — the caller renders nothing, never an empty scaffold.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskSpecSuggestion? Suggestion { get; init; }

    /// <summary>Whether the repo's top-level grounding reached the compiler — a checks suggestion without grounding is a guess about the toolchain, and the FE may caveat it.</summary>
    public bool Grounded { get; init; }
}

/// <summary>One compiled suggestion set. Every field maps onto an existing <c>LaunchTaskCommand</c> field; the operator edits or discards freely — these are proposals, not stakes.</summary>
public sealed record TaskSpecSuggestion
{
    /// <summary>The suggested EXECUTABLE acceptance argv (the launch's <c>AcceptanceChecks</c> floor). Empty when the compiler could not name a check it believes exists — a wrong argv is worse than none (it mints Failed/InfraUnknown noise and withholds good work).</summary>
    public IReadOnlyList<string> AcceptanceChecks { get; init; } = Array.Empty<string>();

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
