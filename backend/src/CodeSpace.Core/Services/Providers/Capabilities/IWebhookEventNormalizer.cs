using CodeSpace.Messages.Events;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Maps a raw provider-specific webhook payload into a NormalizedEvent the rest of the
/// system understands. Returning null is the explicit "this payload is uninteresting / not
/// yet supported" signal — the dispatcher logs and skips.
/// </summary>
public interface IWebhookEventNormalizer : IProviderCapability
{
    NormalizedEvent? Normalize(Guid repositoryId, string body, IReadOnlyDictionary<string, string> headers);
}
