namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// What we sent to a provider, as it is safe to persist: every secret has ALREADY been replaced
/// with a mask by the time this record exists. Nothing downstream re-masks, because a value that
/// reaches a database row unmasked is already leaked — masking at display time would only hide it
/// from the screen, not from the dump.
///
/// <para>Build it with <c>ProviderCallCapture.CaptureRedacted</c>; constructing one by hand is
/// how a secret gets in.</para>
/// </summary>
public sealed record CapturedProviderRequest
{
    public required string Method { get; init; }
    public required string Url { get; init; }
    public string? Body { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}
