using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Sessions.Exceptions;

public sealed class SessionRunMetadataPageRequestException : ArgumentException, IFailure
{
    public SessionRunMetadataPageRequestException(IReadOnlyList<string> errors) : base("Invalid Session run metadata page request.") { Errors = errors; }
    public IReadOnlyList<string> Errors { get; }

    FailureKind IFailure.Kind => FailureKind.Invalid;
    string IFailure.Code => FailureCodes.InvalidRequest;
    string? IFailure.ClientMessage => "Invalid Session run metadata page request.";
    IReadOnlyDictionary<string, object?> IFailure.Details => new Dictionary<string, object?> { ["errors"] = Errors };
}
