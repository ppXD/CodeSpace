namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// One downloadable asset on a release — GitHub release asset / source archive, or GitLab release link /
/// source. <see cref="SizeBytes"/> is null for assets the provider doesn't size (GitLab links, source
/// archives) so the UI omits the size rather than printing "0 B".
/// </summary>
public sealed record RemoteReleaseAsset
{
    public required string Name { get; init; }
    public required string DownloadUrl { get; init; }
    public long? SizeBytes { get; init; }
}
