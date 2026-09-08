using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Context.Exceptions;

/// <summary>A malformed or rebound source cursor. Kept distinct from database faults so the tool can return a correctable caller error without hiding operational failures.</summary>
public sealed class AgentContextCursorException : ArgumentException, IFailure
{
    public AgentContextCursorException(string message) : base(message) { }

    FailureKind IFailure.Kind => FailureKind.Invalid;
    string IFailure.Code => FailureCodes.InvalidRequest;
    string? IFailure.ClientMessage => Message;
}
