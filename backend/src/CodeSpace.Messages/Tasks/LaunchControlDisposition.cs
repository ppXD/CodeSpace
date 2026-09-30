using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Tasks;

/// <summary>What the resolved route did with one operator launch control. Every control a launch carries lands in exactly one of these — never silently dropped.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LaunchControlOutcome>))]
public enum LaunchControlOutcome { Applied, Clamped, NotApplicable, Refused }

/// <summary>
/// One operator launch control's disposition on the resolved route (Rule 18.1, a pure data noun). The launch and its
/// route preview compute it through the same service, so the preview states what the launch will do; a
/// <see cref="LaunchControlOutcome.Refused"/> control stops the launch before any session or run exists.
/// </summary>
public sealed record LaunchControlDisposition
{
    /// <summary>The control's wire name — a <see cref="LaunchControls"/> value.</summary>
    public required string Control { get; init; }

    public required LaunchControlOutcome Outcome { get; init; }

    /// <summary>Why the route clamped, set aside or refused the control; for an applied one, how it applies when that is not obvious. Null when there is nothing to add.</summary>
    public string? Reason { get; init; }
}

/// <summary>The launch controls whose effect depends on the route, by their wire names on the launch input. A control every route consumes the same way has no disposition to report.</summary>
public static class LaunchControls
{
    public const string AllowedModelIds = "allowedModelIds";
    public const string AllowedAgentDefinitionIds = "allowedAgentDefinitionIds";
    public const string AcceptanceChecks = "acceptanceChecks";
    public const string DecisionReviewMode = "decisionReviewMode";
    public const string DeliverySpec = "deliverySpec";
    public const string RequirePlanConfirmation = "requirePlanConfirmation";
    public const string PlannerReviewMode = "plannerReviewMode";
}
