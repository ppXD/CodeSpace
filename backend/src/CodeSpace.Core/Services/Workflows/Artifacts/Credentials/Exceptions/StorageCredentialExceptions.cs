using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Credentials.Exceptions;

public sealed class StorageCredentialInvalidException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Invalid;
    public string Code => FailureCodes.StorageCredentialInvalid;
    public string? ClientMessage => Message;

    public StorageCredentialInvalidException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class StorageCredentialConflictException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Conflict;
    public string Code => FailureCodes.StorageCredentialConflict;
    public string? ClientMessage => Message;

    public StorageCredentialConflictException(string message, Exception? inner = null) : base(message, inner) { }
}
