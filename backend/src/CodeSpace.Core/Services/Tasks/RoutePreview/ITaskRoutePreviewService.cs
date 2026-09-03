using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.RoutePreview;

/// <summary>
/// Answers "where would this launch go?" without launching it. Read-only by contract: it opens no session,
/// stages no run and persists nothing.
/// </summary>
public interface ITaskRoutePreviewService
{
    /// <summary>The route the SAME seed provider + effort router would produce for this request. Every repository named is validated TEAM-SCOPED first (fail-closed).</summary>
    Task<TaskRoutePreviewResult> PreviewAsync(TaskLaunchRequest request, CancellationToken cancellationToken);
}
