using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Plans;

/// <summary>
/// A planner-authored OPERATOR QUESTION riding a plan version (Rule 18.1 data noun) — the "choose a
/// direction" form fodder. The plan-confirmation gate renders these as a form (each question = one choice
/// group); an unanswered question falls back to <see cref="RecommendedOptionId"/> and the chosen default is
/// recorded as an assumption. Pure data here — nothing executes or parks on it in S1/S2; the confirm gate
/// (S3) is the consumer.
/// </summary>
public sealed record WorkPlanQuestion
{
    /// <summary>Stable, plan-local id (answers reference it).</summary>
    public required string Id { get; init; }

    /// <summary>The question the operator is asked.</summary>
    public required string Question { get; init; }

    /// <summary>2-4 mutually exclusive options.</summary>
    public IReadOnlyList<WorkPlanQuestionOption> Options { get; init; } = Array.Empty<WorkPlanQuestionOption>();

    /// <summary>The option the planner recommends — the default an unattended run proceeds with.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecommendedOptionId { get; init; }

    /// <summary>Whether a free-text answer is also acceptable (rendered as an "other" input).</summary>
    public bool AllowFreeText { get; init; }
}

/// <summary>One selectable option of a <see cref="WorkPlanQuestion"/>.</summary>
public sealed record WorkPlanQuestionOption
{
    /// <summary>Stable, question-local id.</summary>
    public required string Id { get; init; }

    /// <summary>The operator-facing label.</summary>
    public required string Label { get; init; }
}
