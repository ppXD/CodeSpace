namespace CodeSpace.Core.Services.Providers.GitHub.Events;

internal static class GitHubDelivery
{
    private const string Header = "X-GitHub-Delivery";

    /// <summary>Reads the GitHub delivery id from the webhook header; falls back to a random N-format Guid so downstream events always have a stable ProviderEventId.</summary>
    public static string IdFromHeadersOrFallback(IReadOnlyDictionary<string, string> headers)
    {
        return WebhookHeaderLookup.TryFind(headers, Header, out var id) ? id : Guid.NewGuid().ToString("N");
    }
}
