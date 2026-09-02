namespace CodeSpace.Messages.Dtos.Storage;

/// <summary>
/// Secret-free qualification result for a destination NOBODY HAS SAVED.
///
/// <para>It carries no profile identity because there is no profile: the whole point of this answer is that it is
/// available BEFORE anything is written down. It reuses <see cref="StorageProfileProbeStatusValue"/> and
/// <see cref="StorageProfileProbeFailure"/> deliberately - an operator reading "the key is not authorized for this
/// bucket" must not be told it in two different vocabularies depending on whether they had already pressed Save.</para>
/// </summary>
public sealed record StorageConfigurationProbeResult
{
    public required string ProviderTypeKey { get; init; }
    public required StorageProfileProbeStatusValue Status { get; init; }
    public required long LatencyMilliseconds { get; init; }
    public StorageProfileProbeFailure? Failure { get; init; }
}
