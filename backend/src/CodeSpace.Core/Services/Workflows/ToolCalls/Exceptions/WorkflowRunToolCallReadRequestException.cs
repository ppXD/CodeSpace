using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.ToolCalls.Exceptions;

public sealed class WorkflowRunToolCallReadRequestException : ArgumentException, IFailure
{
    public WorkflowRunToolCallReadRequestException(IReadOnlyList<string> errors) : base("Invalid Workflow Run tool-call read request.") { Errors = errors; }

    public IReadOnlyList<string> Errors { get; }

    FailureKind IFailure.Kind => FailureKind.Invalid;
    string IFailure.Code => FailureCodes.InvalidRequest;
    string? IFailure.ClientMessage => "Invalid Workflow Run tool-call read request.";
    IReadOnlyDictionary<string, object?> IFailure.Details => new Dictionary<string, object?> { ["errors"] = Errors };
}
