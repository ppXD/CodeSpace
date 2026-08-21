using System.Globalization;
using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Sessions.Journal;

public static class JournalObservationCoverageLimits
{
    public const int MaximumEntriesPerStep = 3;
    public const int MaximumSourceKindChars = 100;
}

public static class JournalObservationCoverageSourceKinds
{
    public const string SupervisorPlanPage = "supervisor-plan-page/v1";
    public const string SupervisorPlanSubtasks = "supervisor-plan-subtasks/v1";
    public const string SupervisorPlanModelUsage = "supervisor-plan-model-usage/v1";
    public const string SupervisorPlanMetadata = "supervisor-plan-metadata/v1";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JournalObservationCoverageReason
{
    OlderItemsOmitted,
    InvalidLeaf,
    TruncatedLeaf,
    CorruptLeaf,
    CorruptDecisionStatus,
}

/// <summary>
/// Typed warning that one bounded journal observation did not establish complete truth. It contains only provenance,
/// counts and the durable decision identity; no payload/outcome prefix is promoted to a normal journal fact. StoryOrder
/// is a decimal string so JavaScript clients cannot round a 64-bit identity.
/// </summary>
public sealed record JournalObservationCoverage
{
    public required string SourceKind { get; init; }
    public required JournalObservationCoverageReason Reason { get; init; }
    public required int ObservedCount { get; init; }
    public required int OmittedCount { get; init; }
    public required bool OmittedCountIsLowerBound { get; init; }
    public required Guid DecisionId { get; init; }
    public required string StoryOrder { get; init; }

    public IReadOnlyList<string> ValidateShape()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(SourceKind) || SourceKind.Length > JournalObservationCoverageLimits.MaximumSourceKindChars)
            errors.Add($"SourceKind must be between 1 and {JournalObservationCoverageLimits.MaximumSourceKindChars} characters.");
        if (!Enum.IsDefined(Reason)) errors.Add("Reason must be a known closed coverage reason.");
        if (ObservedCount is < 0 or > 500) errors.Add("ObservedCount must be between 0 and 500.");
        if (OmittedCount < 0) errors.Add("OmittedCount must be non-negative.");
        if (DecisionId == Guid.Empty) errors.Add("DecisionId must be non-empty.");
        if (!long.TryParse(StoryOrder, NumberStyles.None, CultureInfo.InvariantCulture, out var storyOrder) || storyOrder <= 0)
            errors.Add("StoryOrder must be a positive Int64 decimal string.");
        if (Reason == JournalObservationCoverageReason.OlderItemsOmitted && (!OmittedCountIsLowerBound || OmittedCount < 1))
            errors.Add("OlderItemsOmitted requires a positive lower-bound omission count.");
        if (Reason != JournalObservationCoverageReason.OlderItemsOmitted && OmittedCountIsLowerBound)
            errors.Add("Only OlderItemsOmitted may carry a lower-bound omission count.");
        return errors;
    }
}
