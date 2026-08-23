using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Supervisor.Observation.Exceptions;

public sealed class SupervisorDecisionObservationReadRequestException : ArgumentException, IFailure
{
    public IReadOnlyList<string> Errors { get; }

    public SupervisorDecisionObservationReadRequestException(IReadOnlyList<string> errors) : base(string.Join(" ", errors))
    {
        Errors = errors;
    }

    FailureKind IFailure.Kind => FailureKind.Invalid;
    string IFailure.Code => FailureCodes.InvalidRequest;
    string? IFailure.ClientMessage => "Invalid Supervisor decision observation read request.";
    IReadOnlyDictionary<string, object?> IFailure.Details => new Dictionary<string, object?> { ["errors"] = Errors };
}
