using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Sandbox.Exceptions;

/// <summary>A launch slot cannot safely admit a new physical execution. The reason is host metadata, never the secret-bearing invocation.</summary>
public sealed class NativeLaunchException(string reason, string message) : Exception(message), IFailure
{
    public string Reason { get; } = reason;
    public FailureKind Kind => FailureKind.Unprocessable;
    public string Code => FailureCodes.NativeLaunchUnavailable;
}
