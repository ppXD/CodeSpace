using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Exceptions;

/// <summary>A page request whose direction, cursor, or hard limit is not one of the closed wire shapes.</summary>
public sealed class AgentRunEventPageRequestException : ArgumentException, IFailure
{
    public AgentRunEventPageRequestException(IReadOnlyList<string> errors) : base("Invalid Agent Run event page request.") { Errors = errors; }

    public IReadOnlyList<string> Errors { get; }

    FailureKind IFailure.Kind => FailureKind.Invalid;
    string IFailure.Code => FailureCodes.InvalidRequest;
    string? IFailure.ClientMessage => "Invalid Agent Run event page request.";
    IReadOnlyDictionary<string, object?> IFailure.Details => new Dictionary<string, object?> { ["errors"] = Errors };
}
