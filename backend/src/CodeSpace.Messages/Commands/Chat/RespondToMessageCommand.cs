using System.Text.Json;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Chat;

/// <summary>
/// Respond to an interactive message — click a card button. Team-scoped (the pipeline vets team
/// membership); the service additionally vets conversation membership + the allowed-responder set,
/// resolves the interaction's target (the parked workflow wait), and stamps the resolution. The wait
/// token stays SERVER-SIDE — the message id identifies the interaction, so the token is never exposed
/// to the client.
///
/// An <see cref="ICommand{TResponse}"/> (not a bare IRequest) so the TransactionalBehavior wraps the whole
/// respond in one transaction: the message row is locked + re-read, the parked wait is resolved, and the
/// card resolution is stamped atomically — so concurrent responders can't diverge the card from the
/// workflow decision, and a crash can't resume the run while leaving the card open.
/// </summary>
public sealed record RespondToMessageCommand : ICommand<Unit>, IRequireTeamMembership
{
    public Guid MessageId { get; init; }

    /// <summary>The chosen option — a button key from the card, or "submit" for a form (e.g. "approve").</summary>
    public required string ResponseKey { get; init; }

    public string? Comment { get; init; }

    /// <summary>For a form card — the submitted field values, injected into the parked run. Null for a button response.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Values { get; init; }
}
