using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Reads WHICH repository a raw delivery is about, out of the payload.
///
/// <para>A sibling of <see cref="IWebhookEventNormalizer"/> rather than a widening of it, because
/// it answers a question the normalizer is never asked: the normalizer is HANDED a repository id
/// and turns a body into an event, which is all a per-repository hook ever needs — its callback
/// URL already identifies the repository. A group hook delivers many repositories to one URL, so
/// identity has to be established BEFORE normalization can be given a repository id at all. The
/// two also fail differently: a payload we can identify but don't map is a normalizer miss, and a
/// payload we can't identify is not.</para>
///
/// <para>Returning null is the explicit "this payload names no repository" signal — a
/// group-membership event, a ping, anything that isn't about a project. Callers audit and drop.</para>
/// </summary>
public interface IWebhookRepositoryIdentifier : IProviderCapability
{
    WebhookRepositoryIdentity? Identify(string body, IReadOnlyDictionary<string, string> headers);
}
