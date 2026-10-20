using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

public sealed class ExternalTriggerSlugConflictException(string slug, Exception? innerException = null)
    : Exception($"External trigger slug '{slug}' is already in use. Choose another slug.", innerException), IFailure
{
    public FailureKind Kind => FailureKind.Conflict;
    public string Code => FailureCodes.DuplicateResource;
}
