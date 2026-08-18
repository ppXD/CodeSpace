namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The durable IDENTITY of one logical harness execution of an <see cref="AgentRun"/> — the row that answers "which
/// physical run produced this?", which today has no answer at all: revise rounds, re-attaches and worker
/// replacements all fold into the single mutable <see cref="AgentRun"/> row, so a re-attach can only recover the
/// tail, a per-attempt cost has nowhere to live, and an execution on a non-local runner cannot be addressed.
///
/// <para>Keyed to the AGENT RUN rather than to a Workflow Run. <see cref="AgentRun.WorkflowRunId"/> is nullable
/// because an Agent Run is deliberately standalone-capable, so a NOT NULL workflow run would make a standalone
/// execution unrepresentable; <see cref="WorkflowRunId"/> mirrors the Agent Run's own nullable soft link and is
/// enforced to agree with it. The table NAME keeps the contract-registered <c>workflow_run_</c> prefix.</para>
///
/// <para><see cref="Generation"/> is the supersession axis, one-based and contiguous: a re-LAUNCH of the same Agent
/// Run opens the next generation, and the database refuses to open one while its predecessor is still live. A
/// re-attach to a process that is still alive is NOT a new generation — it raises <see cref="LeaseFence"/> on this
/// same row, which is how a resurrected older observer is rejected instead of interleaving with the new one.</para>
///
/// <para>The flip side of that gate, and the OBLIGATION it imposes on the first writer: a generation left
/// <see cref="HarnessExecutionState.Pending"/> with no attempt — a launch that died between writing this row and
/// inserting attempt 1 — blocks every later generation of its Agent Run until something closes it, and it can only be
/// closed as <see cref="HarnessExecutionState.Abandoned"/> with an error code. It is also invisible to the
/// lease-expiry index, because it never held a lease; the age scan over
/// <c>ix_workflow_run_harness_execution_stale_live</c> is what finds it.</para>
///
/// <para>The runner is described by KIND plus an opaque locator schema version, never by transport-specific columns,
/// so a container or remote backend arrives as a new kind with no migration. The per-process locator itself lives on
/// <see cref="WorkflowRunHarnessProcessAttempt.RunnerLocatorJson"/>, where a pid actually belongs.</para>
///
/// <para><see cref="State"/> describes the PROCESS lifecycle of this execution, never the task's verdict — the Agent
/// Run's own status remains the only outcome authority.</para>
/// </summary>
public sealed class WorkflowRunHarnessExecution : IEntity<Guid>
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid AgentRunId { get; set; }

    /// <summary>The workflow run whose agent.run node spawned the owning Agent Run; NULL for a standalone run. Soft correlation (no FK), enforced to equal the Agent Run's own value.</summary>
    public Guid? WorkflowRunId { get; set; }

    /// <summary>One-based supersession counter for this Agent Run. A re-launch opens the next generation; a re-attach does not.</summary>
    public int Generation { get; set; }

    /// <summary>The adapter identity that ran, snapshotted as <c>&lt;kind&gt;/v&lt;major&gt;</c> and immutable, so a row read a year later is interpretable against the adapter that produced it rather than against whatever the harness column has since become.</summary>
    public string HarnessTypeKey { get; set; } = string.Empty;

    /// <summary>Runner kind that owns this execution (e.g. <c>local</c>) — how a reader resolves who may interpret an attempt's locator.</summary>
    public string RunnerKind { get; set; } = string.Empty;

    /// <summary>One-based version of THIS runner kind's locator shape, owned by the runner so a backend evolves its locator without touching shared schema.</summary>
    public int RunnerLocatorSchemaVersion { get; set; } = 1;

    /// <summary>The host this execution is pinned to, when it is pinned. NULL ⇒ any worker may claim it.</summary>
    public string? RunnerHostAffinity { get; set; }

    /// <summary>Absolute wall-clock cap snapshotted at launch, so the timeout survives the observer that set it. NULL ⇒ no cap.</summary>
    public DateTimeOffset? DeadlineAt { get; set; }

    public HarnessExecutionState State { get; set; } = HarnessExecutionState.Pending;

    /// <summary>Physical attempts recorded so far. Monotonic head — advanced by the database when an attempt is appended, never by a writer.</summary>
    public int AttemptCount { get; set; }

    /// <summary>The only ordinal the next appended attempt may carry, which is what makes attempt ordinals contiguous from one.</summary>
    public int NextAttemptOrdinal { get; set; } = 1;

    /// <summary>Who currently holds this execution. NULL ⇒ unheld; a terminal execution always releases it.</summary>
    public Guid? LeaseOwnerId { get; set; }

    /// <summary>
    /// Advances by exactly one on every ACQUISITION of the lease, and an acquisition is refused while the stored lease
    /// is still live — so each fence value is acquired by at most ONE owner and a stale holder's observed value can
    /// never come round again. A release leaves it where it is; only acquiring moves it.
    /// <para>What it does NOT do: authenticate anybody. A row trigger sees OLD and NEW, never which worker issued the
    /// statement, so releasing a live lease is legal from any session. A holder proves itself by carrying
    /// <c>WHERE lease_owner_id = @me AND lease_fence = @observed AND revision = @observed</c> on its own writes — that
    /// predicate, not the fence alone, is what makes a displaced holder fail instead of interleave.</para>
    /// </summary>
    public long LeaseFence { get; set; }

    /// <summary>When the current lease lapses. While it is live the lease cannot be reassigned to another owner, and
    /// the execution cannot be closed in the same statement that evicts it — it must lapse or be released first.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }

    /// <summary>When this execution reached a terminal process state. NULL while live.</summary>
    public DateTimeOffset? TerminalAt { get; set; }

    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public uint Xmin { get; set; }

    public AgentRun AgentRun { get; set; } = default!;
    public ICollection<WorkflowRunHarnessProcessAttempt> Attempts { get; set; } = new List<WorkflowRunHarnessProcessAttempt>();
}

/// <summary>Process lifecycle of a harness execution, not the Agent Run's task outcome. Every non-live state is terminal and immutable.</summary>
public enum HarnessExecutionState
{
    /// <summary>Identity exists, no process launched yet — which is what makes a launch that never happened a durable
    /// fact rather than a missing row. It is also a BLOCKING fact: while this generation stays Pending no later one may
    /// open, and with no attempt it is closable only as <see cref="Abandoned"/>, so an age-based reaper owes it.</summary>
    Pending,

    /// <summary>At least one attempt has been appended and none is unaccounted for.</summary>
    Running,

    /// <summary>Every attempt reached a terminal process state.</summary>
    Exited,

    /// <summary>No worker will reclaim this execution; its real process state is unknown and stays that way.</summary>
    Abandoned,
}
