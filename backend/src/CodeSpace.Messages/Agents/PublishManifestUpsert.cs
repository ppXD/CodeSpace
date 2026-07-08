namespace CodeSpace.Messages.Agents;

/// <summary>
/// The fields <c>IPublishManifestStore</c> writes for one artifact. A noun carried across the service boundary
/// (Rule 18.1) — no behavior of its own. <see cref="RepositoryAlias"/> defaults to "primary" (the single-repo /
/// top-level case); a multi-repo caller sets it to the writable repo's own alias.
/// </summary>
public sealed record PublishManifestUpsert
{
    public required Guid TeamId { get; init; }
    public Guid? WorkflowRunId { get; init; }
    public string RepositoryAlias { get; init; } = "primary";
    public Guid? RepositoryId { get; init; }
    public string? BaseSha { get; init; }
    public string? Branch { get; init; }
    public string? CommitSha { get; init; }
    public Guid? PatchArtifactId { get; init; }
    public int ChangedFileCount { get; init; }
    public string? ChangedFilesJson { get; init; }
    public PublishAcceptanceState AcceptanceState { get; init; } = PublishAcceptanceState.NotApplicable;
    public required PublishState PublishStateValue { get; init; }
    public string? PublishError { get; init; }
    public string? Summary { get; init; }
    public int? PullRequestNumber { get; init; }
    public string? PullRequestUrl { get; init; }
}
