namespace CodeSpace.Messages.Constants;

/// <summary>
/// Canonical <c>run_kind</c> tokens — the COARSE semantic origin of a run, a generated function of <c>source_type</c>
/// (see migration 0067's CASE). An OPEN string set: new origins map in by extending the CASE, callers speaking in
/// well-known kinds avoid magic strings via these constants. Pinned by <c>RunKindsTests</c> so the C# tokens and the
/// SQL CASE stay in lockstep. Distinct from <c>TaskProjectionKinds</c> (the projection/coordination MODE of a task run,
/// e.g. supervisor) — a run has both a run_kind and, for task runs, a projection_kind.
/// </summary>
public static class RunKinds
{
    /// <summary>An authored workflow run (source_type = manual).</summary>
    public const string Workflow = "workflow";

    /// <summary>A task launch — a one-shot snapshot run (source_type = snapshot).</summary>
    public const string Task = "task";

    /// <summary>A provider-event-triggered run (source_type starts provider. / trigger.).</summary>
    public const string Event = "event";

    /// <summary>A replay / rerun fork (source_type = replay or rerun).</summary>
    public const string Replay = "replay";

    /// <summary>A scheduled (cron) fire (source_type = schedule.cron).</summary>
    public const string Schedule = "schedule";

    /// <summary>A sub-workflow child (source_type = workflow.child) — excluded from the team index, but the token exists for completeness.</summary>
    public const string Child = "child";

    /// <summary>A generic third-party API caller (source_type = api).</summary>
    public const string Api = "api";

    /// <summary>Any source_type not mapped above.</summary>
    public const string Other = "other";
}
