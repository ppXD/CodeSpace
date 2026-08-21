using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Exceptions;

public sealed class ToolCallPageRequestException : ArgumentException, IFailure
{
    public ToolCallPageRequestException(IReadOnlyList<string> errors) : base("Invalid governed ToolCall page request.") { Errors = errors; }

    public IReadOnlyList<string> Errors { get; }

    FailureKind IFailure.Kind => FailureKind.Invalid;
    string IFailure.Code => FailureCodes.InvalidRequest;
    string? IFailure.ClientMessage => "Invalid governed ToolCall page request.";
    IReadOnlyDictionary<string, object?> IFailure.Details => new Dictionary<string, object?> { ["errors"] = Errors };
}
