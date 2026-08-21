namespace CodeSpace.Core.Services.Supervisor.Observation.Exceptions;

public sealed class SupervisorDecisionObservationReadRequestException : ArgumentException
{
    public IReadOnlyList<string> Errors { get; }

    public SupervisorDecisionObservationReadRequestException(IReadOnlyList<string> errors) : base(string.Join(" ", errors))
    {
        Errors = errors;
    }
}
