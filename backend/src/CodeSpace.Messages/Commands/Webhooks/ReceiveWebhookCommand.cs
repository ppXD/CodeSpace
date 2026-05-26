using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Webhooks;

public sealed record ReceiveWebhookCommand : ICommand<Unit>
{
    public required Guid WebhookId { get; init; }
    public required string Body { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
}
