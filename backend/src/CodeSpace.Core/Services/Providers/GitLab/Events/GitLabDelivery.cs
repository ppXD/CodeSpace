namespace CodeSpace.Core.Services.Providers.GitLab.Events;

internal static class GitLabDelivery
{
    private const string Header = "X-Gitlab-Event-UUID";

    public static string IdFromHeadersOrFallback(IReadOnlyDictionary<string, string> headers)
    {
        return WebhookHeaderLookup.TryFind(headers, Header, out var id) ? id : Guid.NewGuid().ToString("N");
    }
}
