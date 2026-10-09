namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// A provider whose webhook signature covers the BODY and which stamps every delivery with an id in a header (GitHub's
/// HMAC and <c>X-GitHub-Delivery</c>). A body signed that way stays valid for as long as the secret does, so anyone who
/// captured one can post it again; ingestion therefore refuses such a delivery without its id, and refuses a body the hook
/// already accepted under a different id.
///
/// <para>Not implemented by a provider that authenticates with a static token header (GitLab's <c>X-Gitlab-Token</c>):
/// whoever holds that token can sign any body at all, so a replay check would stop nothing the token does not already
/// allow — and an older GitLab sends no delivery id to require.</para>
/// </summary>
public interface IWebhookDeliveryIdentity : IProviderCapability
{
    /// <summary>The header every authentic delivery carries its id in.</summary>
    string DeliveryIdHeader { get; }
}
