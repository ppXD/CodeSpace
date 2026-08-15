using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing.Exceptions;

public sealed class StorageRouteInvalidException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Invalid;
    public string Code => FailureCodes.StorageRouteInvalid;
    public string? ClientMessage => Message;

    public StorageRouteInvalidException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class StorageRouteConflictException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Conflict;
    public string Code => FailureCodes.StorageRouteConflict;
    public string? ClientMessage => Message;

    public StorageRouteConflictException(string message, Exception? inner = null) : base(message, inner) { }
}
