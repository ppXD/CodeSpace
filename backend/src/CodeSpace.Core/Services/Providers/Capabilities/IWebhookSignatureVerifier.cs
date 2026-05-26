namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Verifies inbound webhook payload authenticity using the per-provider scheme
/// (GitHub uses HMAC-SHA256 over the body; GitLab compares a header token literal).
/// Separate from registration because some providers only verify (e.g. forks consuming
/// upstream webhooks) and some only register (a future read-only sink).
/// </summary>
public interface IWebhookSignatureVerifier : IProviderCapability
{
    bool VerifySignature(string body, IReadOnlyDictionary<string, string> headers, string secret);
}
