using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Defaults.Exceptions;

public sealed class StorageDefaultInvalidException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Invalid;
    public string Code => FailureCodes.StorageDefaultInvalid;
    public string? ClientMessage => Message;

    public StorageDefaultInvalidException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class StorageDefaultConflictException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Conflict;
    public string Code => FailureCodes.StorageDefaultConflict;
    public string? ClientMessage => Message;

    public StorageDefaultConflictException(string message, Exception? inner = null) : base(message, inner) { }
}
