using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Display.Exceptions;

public sealed class WorkflowRunCellFieldReadRequestException : ArgumentException, IFailure
{
    public WorkflowRunCellFieldReadRequestException(IReadOnlyList<string> errors) : base("Invalid Workflow Run cell field read request.") { Errors = errors; }

    public IReadOnlyList<string> Errors { get; }

    FailureKind IFailure.Kind => FailureKind.Invalid;
    string IFailure.Code => FailureCodes.InvalidRequest;
    string? IFailure.ClientMessage => "Invalid Workflow Run cell field read request.";
    IReadOnlyDictionary<string, object?> IFailure.Details => new Dictionary<string, object?> { ["errors"] = Errors };
}
