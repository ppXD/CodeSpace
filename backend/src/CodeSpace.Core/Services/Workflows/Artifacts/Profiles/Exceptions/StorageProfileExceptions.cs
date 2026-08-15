using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Profiles.Exceptions;

public sealed class StorageProfileInvalidException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Invalid;
    public string Code => FailureCodes.StorageProfileInvalid;
    public string? ClientMessage => Message;

    public StorageProfileInvalidException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class StorageProfileConflictException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Conflict;
    public string Code => FailureCodes.StorageProfileConflict;
    public string? ClientMessage => Message;

    public StorageProfileConflictException(string message, Exception? inner = null) : base(message, inner) { }
}
