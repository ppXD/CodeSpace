using System.Text.Json.Serialization;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Messages.Tasks;

/// <summary>A persisted routing decision. No session or run is created, and its reference grants no execution permission.</summary>
public sealed record TaskRoutePreviewResult
{
    public required RoutePlan Route { get; init; }
    public required string DeploymentAutonomyCeiling { get; init; }
    /// <summary>Absent on a legacy preview means no reusable decision was recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RouteSnapshotId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Database time at issue, so clients can schedule a refresh without assuming their wall clock matches the server.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Compatibility of the resolved projection's operator-command adapter with this workspace. This is not a runtime capability probe or a passed execution receipt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskAcceptanceCompatibility? AcceptanceCompatibility { get; init; }

    /// <summary>
    /// The posture the LAUNCH will actually take for this exact input — autonomy / network / completion mode, each
    /// computed by calling the SAME production derivations <c>LaunchAsync</c> calls (no copy), so this can never
    /// state a posture the launch would not also reach. Null only on the raw, undescribed result a snapshot store
    /// constructs before <c>TaskRoutePreviewService</c> augments it (mirrors <see cref="AcceptanceCompatibility"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskRoutePosture? Posture { get; init; }
}

/// <summary>
/// The posture a launch of this exact input would run under (Rule 18.1, a pure data noun) — the answer to "network
/// on/off and why, what autonomy tier, and does completion get enforced", stated BEFORE any run exists so the
/// operator never sees a posture the launch might then contradict.
/// </summary>
public sealed record TaskRoutePosture
{
    /// <summary>The tier the run's agents WILL run at — the operator's request clamped to the route's ceiling (the same clamp <c>TaskLaunchService.BuildAgentProfile</c> stamps onto the projected run), as an open tier-name string (e.g. <c>"Standard"</c>).</summary>
    public required string Autonomy { get; init; }

    /// <summary>Whether <see cref="Autonomy"/>'s own baseline (<c>AgentAutonomyPolicy.Derive</c>) actually grants network — the one fact a reader needs before deciding whether <see cref="Network"/>'s "on" consequence applies, without re-deriving it from the tier name.</summary>
    public required bool NetworkOn { get; init; }

    /// <summary>The one-line network posture for a reader — <c>AgentAutonomyPolicy.DescribeNetwork</c> fed by the SAME resolved tier and ceilings. No run exists yet, so it carries no confinement record (the pre-launch caveat wording), exactly like the run journal's own sentence before a run has one.</summary>
    public required string Network { get; init; }

    /// <summary>The completion-enforcement mode a run launched with these exact inputs would be stamped with — <c>CompletionPolicy.StampModeFor</c> fed by the SAME <c>RunModeClassifier</c> reading the launch's own run-staging uses.</summary>
    public required CompletionEnforcementMode CompletionMode { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TaskAcceptanceCompatibilityState>))]
public enum TaskAcceptanceCompatibilityState { Unknown, Compatible, Incompatible }

public sealed record TaskAcceptanceCompatibility
{
    public TaskAcceptanceCompatibilityState State { get; init; }
    public required string ProjectionKind { get; init; }
    public string? GradingKind { get; init; }
    public bool? RequiresRepository { get; init; }
    public required string Detail { get; init; }
}
