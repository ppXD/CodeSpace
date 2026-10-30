using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

public sealed class ExternalTriggerIdempotencyIntegrityException(string detail) : Exception(detail), IFailure
{
    public FailureKind Kind => FailureKind.Internal;
    public string Code => FailureCodes.Internal;
}
