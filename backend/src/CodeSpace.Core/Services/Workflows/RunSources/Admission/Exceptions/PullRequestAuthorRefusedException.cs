using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.RunSources.Admission.Exceptions;

/// <summary>An activation that admits only members saw a pull request whose author — or whoever pushed its new commits — is not one. Costs that activation's run only.</summary>
public sealed class PullRequestAuthorRefusedException : Exception, IFailure
{
    public PullRequestAuthorRefusedException(string detail, string auditKey) : base(detail)
    {
        AuditKey = auditKey;
    }

    /// <summary>What the refusal row is deduplicated on — one per (activation, PR) per day, however many deliveries the author sends.</summary>
    public string AuditKey { get; }

    /// <summary>The user is known and their standing is not enough: the same user's next pull request or push is refused the same way.</summary>
    public FailureKind Kind => FailureKind.Forbidden;

    public string Code => FailureCodes.Forbidden;
}
