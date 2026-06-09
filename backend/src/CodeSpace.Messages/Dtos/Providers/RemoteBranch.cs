namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// One branch of a repository, as the Code browser's branch picker shows it.
/// <see cref="IsDefault"/> is computed by the provider against the repo's default branch so the
/// picker can pre-select and badge it without a second round-trip.
/// </summary>
public sealed record RemoteBranch
{
    public required string Name { get; init; }
    public string? CommitSha { get; init; }
    public bool IsDefault { get; init; }
    public bool Protected { get; init; }
}
