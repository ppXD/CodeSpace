using System.Text.Json;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.Push;

namespace CodeSpace.Core.Services.Providers.GitLab.Events;

public sealed class GitLabPushEventSubscription : IProviderEventSubscription
{
    public ProviderKind Kind => ProviderKind.GitLab;
    public string RawEventName => "Push Hook";

    public NormalizedEvent? Normalize(Guid repositoryId, JsonElement root, IReadOnlyDictionary<string, string> headers)
    {
        var deliveryId = GitLabDelivery.IdFromHeadersOrFallback(headers);
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

        return new PushReceivedEvent
        {
            RepositoryId = repositoryId,
            ProviderEventId = deliveryId,
            OccurredAt = now,
            Ref = root.GetProperty("ref").GetString()!,
            BeforeSha = root.GetProperty("before").GetString()!,
            AfterSha = root.GetProperty("after").GetString()!,
            PusherExternalId = root.GetProperty("user_id").GetRawText(),
            PusherName = root.GetProperty("user_name").GetString()!,
            Commits = commits
        };
    }
}
