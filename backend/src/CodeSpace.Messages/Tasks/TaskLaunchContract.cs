using CodeSpace.Messages.Agents;

namespace CodeSpace.Messages.Tasks;

/// <summary>
/// Launch-time intent preserved independently of the projected graph. Produced by the task launch service and
/// frozen with the definition, so run detail and replay retain requirements a projection might not yet consume.
/// This is provenance, NOT an authorization grant, enforcement receipt, or proof of verification/delivery.
/// An absent contract on an older run means unknown; readers must not reconstruct operator intent from node defaults.
/// </summary>
public sealed record TaskLaunchContract
{
    public const int CurrentVersion = 1;

    /// <summary>No default version: a present object without its version is incomplete, not implicitly v1.</summary>
    public int Version { get; init; }

    /// <summary>The task text received by the service. Null when a non-chat surface supplied the goal instead.</summary>
    public string? TaskText { get; init; }

    /// <summary>The normalized goal supplied by the launch surface, before a planner or agent can rewrite it.</summary>
    public string? Goal { get; init; }

    public string? SurfaceKind { get; init; }
    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }
    public IReadOnlyList<string>? AcceptanceChecks { get; init; }
    public DeliverySpec? DeliverySpec { get; init; }

    /// <summary>Service input values, including DTO defaults; recording a value does not prove the user explicitly chose a default or the selected lane enforced it.</summary>
    public TaskLaunchControls? RequestedControls { get; init; }

    /// <summary>Routing and autonomy resolved at launch. This does not attest runtime confinement or execution of any control.</summary>
    public RoutePlan? ResolvedRoute { get; init; }

    /// <summary>The launch-time agent envelope, before any dispatch-time defaults or credential resolution.</summary>
    public ResolvedAgentProfile? ResolvedAgentProfile { get; init; }
}
