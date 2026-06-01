using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Commands.Chat;

/// <summary>
/// Respond to an interactive message — click a card button. Team-scoped (the pipeline vets team
/// membership); the service additionally vets conversation membership + the allowed-responder set,
/// resolves the interaction's target (the parked workflow wait), and stamps the resolution. The wait
/// token stays SERVER-SIDE — the message id identifies the interaction, so the token is never exposed
/// to the client.
/// </summary>
public sealed record RespondToMessageCommand : IRequest, IRequireTeamMembership
{
    public Guid MessageId { get; init; }

    /// <summary>The chosen option — a button key from the card (e.g. "approve").</summary>
    public required string ResponseKey { get; init; }

    public string? Comment { get; init; }
}
