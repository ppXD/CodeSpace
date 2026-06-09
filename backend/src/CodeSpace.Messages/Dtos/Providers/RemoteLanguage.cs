namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// One language in a repository's composition — the Code tab's Languages bar. <see cref="Percent"/> is
/// 0–100, rounded to one decimal. The provider/normalizer orders the list descending, so the first entry
/// is the dominant language.
/// </summary>
public sealed record RemoteLanguage
{
    public required string Name { get; init; }
    public required double Percent { get; init; }
}
