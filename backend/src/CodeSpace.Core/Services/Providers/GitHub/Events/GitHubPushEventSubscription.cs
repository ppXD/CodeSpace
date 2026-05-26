using System.Text.Json;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.Push;

namespace CodeSpace.Core.Services.Providers.GitHub.Events;

public sealed class GitHubPushEventSubscription : IProviderEventSubscription
{
    public ProviderKind Kind => ProviderKind.GitHub;
    public string RawEventName => "push";

    public NormalizedEvent? Normalize(Guid repositoryId, JsonElement root, IReadOnlyDictionary<string, string> headers)
    {
        var deliveryId = GitHubDelivery.IdFromHeadersOrFallback(headers);
        var now = DateTimeOffset.UtcNow;

        var commits = root.GetProperty("commits").EnumerateArray()
            .Select(c => new CommitSummary
            {
                Sha = c.GetProperty("id").GetString()!,
                Message = c.GetProperty("message").GetString()!,
                AuthorEmail = c.GetProperty("author").GetProperty("email").GetString()!,
                AuthorName = c.GetProperty("author").GetProperty("name").GetString()!
            })
            .ToList();

        var pusher = root.GetProperty("pusher");
        var sender = root.GetProperty("sender");

        return new PushReceivedEvent
        {
            RepositoryId = repositoryId,
            ProviderEventId = deliveryId,
            OccurredAt = now,
            Ref = root.GetProperty("ref").GetString()!,
            BeforeSha = root.GetProperty("before").GetString()!,
            AfterSha = root.GetProperty("after").GetString()!,
            PusherExternalId = sender.GetProperty("id").GetRawText(),
            PusherName = pusher.GetProperty("name").GetString()!,
            Commits = commits
        };
    }

}
