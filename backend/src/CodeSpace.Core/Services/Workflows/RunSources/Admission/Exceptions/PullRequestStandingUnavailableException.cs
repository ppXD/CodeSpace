using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.RunSources.Admission.Exceptions;

/// <summary>
/// An activation that admits only members could not learn whether a pull request's author — or whoever pushed its new
/// commits — is one: the provider's lookup failed, and a failure says nothing about the person. Distinct from
/// <see cref="PullRequestAuthorRefusedException"/>, which is a refusal of someone whose standing is known. Costs that
/// activation's run only, and is recorded under the reason the failure names so the audit row says what to fix: the
/// credential (<see cref="WorkflowRunRequestRejectionReasons.CredentialRejected"/>) or the lookup
/// (<see cref="WorkflowRunRequestRejectionReasons.AuthorStandingUnknown"/>).
/// </summary>
public sealed class PullRequestStandingUnavailableException : Exception, IFailure
{
    public PullRequestStandingUnavailableException(string reason, string detail, string auditKey) : base(detail)
    {
        Reason = reason;
        AuditKey = auditKey;
    }

    /// <summary>The <see cref="WorkflowRunRequestRejectionReasons"/> value the refusal is recorded under.</summary>
    public string Reason { get; } = WorkflowRunRequestRejectionReasons.AuthorStandingUnknown;

    /// <summary>What the refusal row is deduplicated on — one per (activation, PR, kind) per day, however many deliveries arrive.</summary>
    public string AuditKey { get; }

    /// <summary>A rejected credential is put right by a person; a lookup that did not work is the provider's and may work next time.</summary>
    public FailureKind Kind => Reason == WorkflowRunRequestRejectionReasons.CredentialRejected ? FailureKind.PreconditionRequired : FailureKind.Unavailable;

    public string Code => Reason == WorkflowRunRequestRejectionReasons.CredentialRejected ? FailureCodes.CredentialNeedsReconnect : FailureCodes.ProviderError;
}
