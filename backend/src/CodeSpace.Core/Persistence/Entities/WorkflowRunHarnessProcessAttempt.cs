namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// ONE physical harness process inside a <see cref="WorkflowRunHarnessExecution"/>. Each launch — the first pass and
/// every revise round after it — appends the next attempt, so what is currently one indivisible run becomes a
/// sequence a cost, a log stream or a native record can be attributed to individually.
///
/// <para><see cref="AttemptOrdinal"/> is one-based and CONTIGUOUS: the database accepts only the ordinal the parent
/// execution's head names next, so a gap cannot be written even by a racing worker.</para>
///
/// <para><see cref="RunnerLocatorJson"/> is the backend's OWN address for this process, read only by the runner named
/// in the execution's kind and never interpreted by shared code — a JSON object precisely so the local runner's pid
/// and spool path, a container id and log cursor, or a service-side execution reference all fit without a migration.
/// Hoisting any of them into a column is what makes a non-local backend unrepresentable.</para>
///
/// <para>Two independent fences: <see cref="WorkerFenceEpoch"/> is the immutable Agent Run fence that LAUNCHED this
/// process (a stale worker cannot append an attempt), while <see cref="ClaimFence"/> is the observer claim a
/// re-attach raises. They answer different questions and must not be conflated — which is also why the observer
/// claim, not the launch fence, is what gates recording an outcome: <see cref="WorkerFenceEpoch"/> is immutable, so
/// demanding it equal the Agent Run's CURRENT fence would leave every attempt unclosable after any fence bump.</para>
/// </summary>
public sealed class WorkflowRunHarnessProcessAttempt : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }

    /// <summary>Denormalized Agent Run scope, proved against the parent execution by a composite foreign key rather than trusted.</summary>
    public Guid AgentRunId { get; set; }

    public Guid ExecutionId { get; set; }

    /// <summary>One-based position within the execution, contiguous — the first launch is 1 and each revise round appends the next.</summary>
    public int AttemptOrdinal { get; set; }

    /// <summary>The exact current Agent Run worker fence that launched this process. Immutable.</summary>
    public long WorkerFenceEpoch { get; set; }

    /// <summary>The runner's opaque locator payload for this process. A JSON object; never interpreted by shared code.</summary>
    public string RunnerLocatorJson { get; set; } = "{}";

    /// <summary>The BACKEND's own identifier for the process, when it lives outside this system. NULL for a runner that owns the process itself.</summary>
    public string? RemoteExecutionId { get; set; }

    /// <summary>Opaque runner-interpreted resume cursor a re-attaching observer continues from. NULL ⇒ from the beginning.</summary>
    public string? CheckpointRef { get; set; }

    public HarnessProcessAttemptState State { get; set; } = HarnessProcessAttemptState.Running;

    /// <summary>The process exit code. Present exactly when the process was observed exiting, which is what separates a known outcome from a lost one.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Who currently observes this process. NULL ⇒ unheld; a terminal attempt cannot be claimed and holds no claim.</summary>
    public Guid? ClaimOwnerId { get; set; }

    /// <summary>Observer claim fence with the same rule as <see cref="WorkflowRunHarnessExecution.LeaseFence"/>: exactly
    /// one step per acquisition, no acquisition over a live claim, unchanged by a release — and no authentication, so a
    /// holder still carries <c>WHERE claim_owner_id = @me AND claim_fence = @observed AND revision = @observed</c>.</summary>
    public long ClaimFence { get; set; }

    /// <summary>When the current observer claim lapses. While it is live the claim cannot be reassigned, and the
    /// process outcome cannot be recorded by the same statement that evicts it.</summary>
    public DateTimeOffset? ClaimExpiresAt { get; set; }

    public long Revision { get; set; } = 1;

    /// <summary>When this process was launched. Immutable, and always known because the row is written at launch.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Latest observation of this process, monotonic — so silence is distinguishable from a stale reader.</summary>
    public DateTimeOffset LastObservedAt { get; set; }

    /// <summary>When the process stopped being live, whether it exited or was lost. NULL while running.</summary>
    public DateTimeOffset? ExitedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public uint Xmin { get; set; }

    public WorkflowRunHarnessExecution Execution { get; set; } = default!;
}

/// <summary>Lifecycle of one physical harness process, not the task's verdict. Both terminal states are immutable.</summary>
public enum HarnessProcessAttemptState
{
    /// <summary>The process is live, or its liveness has not yet been contradicted.</summary>
    Running,

    /// <summary>The process was observed exiting and its exit code is recorded.</summary>
    Exited,

    /// <summary>The process is gone with no exit marker. There is no exit code, and a typed reason is required — an unknown outcome must never read as a clean one.</summary>
    Lost,
}
