using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.RunSources.Admission.Exceptions;

/// <summary>The same activation started a run for this pull request at this head commit inside the debounce window. Costs that activation's run only.</summary>
public sealed class PullRequestTriggerDebouncedException : Exception, IFailure
{
    public PullRequestTriggerDebouncedException(int number, string? headSha, TimeSpan window, string auditKey)
        : base($"pull request #{number} at {headSha ?? "an unreported head"} already started a run from this trigger in the last {window.TotalSeconds:0} seconds")
    {
        AuditKey = auditKey;
    }

    /// <summary>What the refusal row is deduplicated on — one per (activation, PR, head) per day, however many deliveries arrive.</summary>
    public string AuditKey { get; }

    /// <summary>A window that is spent, not a rule that refuses: the same head starts a run again once it passes, and a new head at once.</summary>
    public FailureKind Kind => FailureKind.Exhausted;

    public string Code => FailureCodes.RateLimited;
}
