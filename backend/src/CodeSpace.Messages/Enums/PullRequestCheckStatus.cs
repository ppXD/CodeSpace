namespace CodeSpace.Messages.Enums;

/// <summary>
/// Normalised state of one CI check (GitHub check_run or GitLab pipeline job).
/// Reduces the providers' richer state machines onto a single 6-bucket model
/// the UI can render with one icon per bucket.
/// </summary>
public enum PullRequestCheckStatus
{
    Pending,    // queued / running / in_progress / created / waiting
    Success,    // success / passed
    Failure,    // failure / failed / timed_out
    Cancelled,  // cancelled / canceled
    Skipped,    // skipped / manual / scheduled (user-action-required without running)
    Neutral,    // neutral / action_required (GitHub-only) — completed but neither pass nor fail
}
