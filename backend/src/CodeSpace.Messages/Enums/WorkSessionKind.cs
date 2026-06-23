namespace CodeSpace.Messages.Enums;

/// <summary>
/// The TYPE OF WORK a <c>WorkSession</c> thread solves — a PRODUCT SEMANTIC of the thread, NOT the
/// trigger of any single run inside it. One session has MANY runs of DIFFERENT source types: a
/// <see cref="Pr"/> session might hold a run triggered by a GitHub pull_request webhook, then a
/// manual follow-up run, then a replay — all the same Kind.
///
/// <para>Deliberately distinct from <c>WorkflowRun.SourceType</c> (how a run was triggered) and
/// <c>WorkflowRun.ProjectionKind</c> (the intelligence form a run executes as). Stored as the enum
/// NAME via <c>HasConversion&lt;string&gt;</c> (zero schema churn to add a kind); the literal values
/// are pinned by <c>WorkSessionEnumTests</c>. <see cref="Custom"/> is the open escape hatch for a
/// thread that fits none of the first-class shapes.</para>
/// </summary>
public enum WorkSessionKind
{
    /// <summary>A task thread — the canonical "launch a task, iterate on it" work line.</summary>
    Task,

    /// <summary>A pull-request thread — runs aggregate around one PR (open → review → follow-up fixes).</summary>
    Pr,

    /// <summary>An issue thread — runs aggregate around one tracked issue.</summary>
    Issue,

    /// <summary>A workflow-automation thread — manual + triggered runs of one authored workflow grouped over time.</summary>
    Workflow,

    /// <summary>A scheduled thread — recurring cron firings grouped as one ongoing line (surface only on failure/decision).</summary>
    Schedule,

    /// <summary>Open escape hatch — a thread whose product shape is none of the above.</summary>
    Custom
}
