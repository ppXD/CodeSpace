namespace CodeSpace.Messages.Constants;

/// <summary>
/// Canonical <c>source_type</c> values for <c>workflow_run_request</c>. The <c>source_type</c>
/// column is an open string — new run sources (Slack mentions, MQ pulls, CLI invocations,
/// custom HTTP, child workflows) plug in without enum churn. These constants exist so
/// call sites speaking in terms of well-known sources avoid repeating magic strings;
/// pinned by <c>WorkflowRunSourceTypesTests</c>.
///
/// <para>Convention: dotted namespaces, source-family first. Engine-internal sources stay
/// flat (<c>manual</c>, <c>replay</c>); cron, API, child-workflow sources have a single
/// dotted segment. Provider-event sources are a special case — see <see cref="ProviderPrefix"/>.</para>
/// </summary>
public static class WorkflowRunSourceTypes
{
    /// <summary>User clicked Run / hit POST /workflows/{id}/run.</summary>
    public const string Manual = "manual";

    /// <summary>Operator-initiated replay of an existing run.</summary>
    public const string Replay = "replay";

    /// <summary>Operator-initiated re-run of an existing run STARTING FROM a chosen node — upstream cells are
    /// reused (pre-seeded from the original), the chosen node + its downstream re-run. A replay with a pruned
    /// frontier (D7). Lineage rides on <c>ParentRunId</c> + the request causation, same as a replay.</summary>
    public const string Rerun = "rerun";

    /// <summary>
    /// A one-shot run whose definition is an inline frozen snapshot carried by the run itself
    /// (dynamic-workflows substrate) — there is NO backing Workflow row. Staged via
    /// <c>IRunFromSnapshotStarter</c>.
    /// </summary>
    public const string Snapshot = "snapshot";

    /// <summary>Cron / scheduled fire. Used by the scheduler producer.</summary>
    public const string ScheduleCron = "schedule.cron";

    /// <summary>Generic third-party API caller (no provider plug-in). Reserved for a future
    /// public API ingestion endpoint that doesn't go through a webhook normalizer.</summary>
    public const string Api = "api";

    /// <summary>A node inside another workflow invoked this one. Reserved for the future
    /// <c>flow.invoke</c> sub-workflow node.</summary>
    public const string ChildWorkflow = "workflow.child";

    /// <summary>
    /// Provider-event source prefix marker. The <c>RunSourceDispatcher</c> writes the
    /// matcher's <c>TypeKey</c> directly (e.g. <c>"trigger.pr.opened"</c>) — this constant
    /// is kept for two reasons: (a) future analytics tooling that wants to filter "any
    /// provider event" can do <c>WHERE source_type LIKE 'trigger.%'</c> + <c>'provider.%'</c>,
    /// (b) a future migration to <c>provider.&lt;vendor&gt;.&lt;event&gt;</c> naming has the
    /// constant ready. The current wire value <c>"provider."</c> is NOT written by any
    /// producer today.
    /// </summary>
    public const string ProviderPrefix = "provider.";
}
