using System.Security.Cryptography;
using System.Text;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.SpecPreview;

public interface ITaskSpecEvidenceReader
{
    Task<TaskSpecEvidenceContext> CaptureAsync(TaskSpecEvidenceRequest request, CancellationToken cancellationToken);
    Task<TaskSpecEvidenceContext> ReadFilesAsync(TaskSpecEvidenceContext context, IReadOnlyList<string> paths, CancellationToken cancellationToken);
}

public sealed record TaskSpecEvidenceRequest(Guid TeamId, string Goal, Guid? RepositoryId);

public sealed record TaskSpecEvidenceContext(TaskSpecEvidenceRequest Request, TaskSpecRepositoryObservation Repository, IReadOnlyList<TaskSpecSource> Sources)
{
    public IReadOnlyList<string> ReadFailures { get; init; } = Array.Empty<string>();
    public bool Grounded => Repository.State is TaskSpecRepositoryState.Observed or TaskSpecRepositoryState.ObservedEmpty;

    public static TaskSpecEvidenceContext TaskOnly(TaskSpecEvidenceRequest request, TaskSpecRepositoryState state, string detail) => new(request, new TaskSpecRepositoryObservation { RepositoryId = request.RepositoryId, State = state, Detail = detail }, [TaskSpecSource.Create("goal", "user-goal", request.Goal)]);
}

public sealed record TaskSpecSource(string Id, string Kind, string Content, string ContentDigest, string? Path = null, string? Reference = null)
{
    public static TaskSpecSource Create(string id, string kind, string content, string? path = null, string? reference = null) => new(id, kind, content, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), path, reference);
}
