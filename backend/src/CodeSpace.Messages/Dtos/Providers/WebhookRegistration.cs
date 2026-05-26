namespace CodeSpace.Messages.Dtos.Providers;

public sealed record WebhookRegistration
{
    public required string CallbackUrl { get; init; }
    public required string Secret { get; init; }
    public required IReadOnlyList<string> SubscribedEvents { get; init; }
}
