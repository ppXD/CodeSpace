using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.RoutePreview;

/// <summary>
/// Answers "where would this launch go?" without launching it. Persists a routing reference, but opens no session and stages no run.
/// </summary>
public interface ITaskRoutePreviewService
{
    /// <summary>The route the SAME seed provider + effort router would produce for this request. Every repository named is validated TEAM-SCOPED first (fail-closed).</summary>
    Task<TaskRoutePreviewResult> PreviewAsync(TaskLaunchRequest request, CancellationToken cancellationToken);
}
