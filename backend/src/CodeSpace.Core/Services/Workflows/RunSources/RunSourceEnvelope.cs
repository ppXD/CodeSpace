namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>
/// Uniform input shape for starting a workflow run. Captures every variation (manual / replay
/// / webhook-driven) in one place; the <see cref="IRunStarter"/> consumes it.
///
/// <para>Field philosophy:
/// <list type="bullet">
///   <item><b>Always set</b>: TeamId, WorkflowId, WorkflowVersion, SourceType, ActorType,
///         NormalizedPayloadJson, CreatedBy / LastModifiedBy.</item>
///   <item><b>Provider-event only</b>: ActivationId + ActivationSnapshotJson — link the run
///         back to the activation row that matched.</item>
///   <item><b>Replay only</b>: CausationRequestId + ParentRunId + ReleaseHashAtRun — preserve
///         the lineage and freeze the release the original ran against.</item>
///   <item><b>Optional</b>: ActorId — null when the actor is anonymous (Webhook).</item>
/// </list>
/// </para>
///
/// <para>Validation lives on the starter: any combination of fields that the schema
/// constraints would reject (e.g. ActorType=User without ActorId) throws before any DB
/// write happens, so a malformed envelope never leaves a partial state.</para>
/// </summary>
public sealed record RunSourceEnvelope
{
    public required Guid TeamId { get; init; }
    public required Guid WorkflowId { get; init; }

    /// <summary>Frozen workflow version this run targets. Manual = workflow.LatestVersion; replay = parent run's WorkflowVersion.</summary>
    public required int WorkflowVersion { get; init; }

    /// <summary>String from <see cref="CodeSpace.Messages.Constants.WorkflowRunSourceTypes"/> OR a matcher's TypeKey.</summary>
    public required string SourceType { get; init; }

    /// <summary>String from <see cref="CodeSpace.Messages.Constants.WorkflowRunActorTypes"/>.</summary>
    public required string ActorType { get; init; }

    /// <summary>Specific actor identity. Null for <c>Webhook</c> (no CodeSpace identity); otherwise required.</summary>
    public Guid? ActorId { get; init; }

    /// <summary>The normalised payload the engine sees as <c>{{trigger.*}}</c>. Must be valid JSON.</summary>
    public required string NormalizedPayloadJson { get; init; }

    /// <summary>Audit/audit-write user id stamped on the run row. Same as ActorId for user-driven runs; <c>SystemUsers.SeederId</c> for system-driven.</summary>
    public required Guid CreatedBy { get; init; }

    // Provider-event optional fields.
    public Guid? ActivationId { get; init; }
    public string? ActivationSnapshotJson { get; init; }

    // Replay-only optional fields.
    public Guid? CausationRequestId { get; init; }
    public Guid? ParentRunId { get; init; }
    public string? ReleaseHashAtRun { get; init; }
}
