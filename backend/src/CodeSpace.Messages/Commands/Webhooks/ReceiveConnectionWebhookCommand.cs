using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Webhooks;

/// <summary>
/// A delivery from a group / organization hook. Separate from <see cref="ReceiveWebhookCommand"/>
/// because the id means a different thing: there it identifies the repository, here it identifies
/// only the hook, and the repository is still to be found in the body.
/// </summary>
public sealed record ReceiveConnectionWebhookCommand : ICommand<Unit>
{
    public required Guid ConnectionWebhookId { get; init; }
    public required string Body { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
}
