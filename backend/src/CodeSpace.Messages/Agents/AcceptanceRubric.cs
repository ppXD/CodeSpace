using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// The RUBRIC an <c>LlmJudge</c> acceptance is graded against (triad S7 — a data noun, Rule 18.1): weighted,
/// per-instance criteria the judge answers with BINARY met/not-met verdicts plus evidence — never a Likert score
/// (the eval canon: binary + critique beats scales for reliability). The weighted met-fraction is compared to
/// <see cref="Threshold"/>; authored WITH the task (the sprint-contract), so the evaluation layer grades against
/// the plan's own definition of done.
/// </summary>
public sealed record AcceptanceRubric
{
    /// <summary>The per-instance criteria — each a concrete, independently judgeable requirement.</summary>
    public required IReadOnlyList<AcceptanceRubricCriterion> Criteria { get; init; }

    /// <summary>The weighted met-fraction (0..1] required to PASS. Null ⇒ 1.0 — every criterion must be met (the strictest, and safest, default). <c>[JsonIgnore(WhenWritingNull)]</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Threshold { get; init; }

    /// <summary>The credentialed-model ROW the judge runs on. Null ⇒ the team's strongest structured-eligible model (the same auto-pick the critics use). <c>[JsonIgnore(WhenWritingNull)]</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? JudgeModelId { get; init; }
}

/// <summary>One rubric criterion: a stable id (the verdict joins back on it), the requirement the judge tests, and its relative weight.</summary>
public sealed record AcceptanceRubricCriterion
{
    public required string Id { get; init; }

    /// <summary>The concrete requirement — judgeable on the artifact alone, e.g. "names at least three competitors with sources".</summary>
    public required string Requirement { get; init; }

    /// <summary>Relative weight in the aggregate (≥ 0). Null ⇒ 1. <c>[JsonIgnore(WhenWritingNull)]</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Weight { get; init; }
}
