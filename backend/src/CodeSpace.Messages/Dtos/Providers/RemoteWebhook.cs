namespace CodeSpace.Messages.Dtos.Providers;

public sealed record RemoteWebhook
{
    public required string ExternalId { get; init; }
    public required string CallbackUrl { get; init; }
    public required IReadOnlyList<string> SubscribedEvents { get; init; }
    public required bool Active { get; init; }
}
