using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Tasks.RoutePreview.Exceptions;

public sealed class TaskRouteSnapshotMismatchException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Conflict;
    public string Code => "task_route_snapshot_mismatch";

    public TaskRouteSnapshotMismatchException() : base("The preview no longer matches this launch input or routing policy, or has expired. Preview the current input again.") { }
}
