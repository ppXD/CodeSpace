using System.Text.Json;

namespace CodeSpace.Core.Services.Chat;

/// <summary>
/// Resolves an interactive message: a person clicked a card button or submitted a form. Validates the
/// responder + the chosen option (and, for a form, its required fields), routes the response to the
/// interaction's target (today: resolve the workflow wait it parked, via <c>ResumeByActionTokenAsync</c>),
/// injecting any submitted <paramref name="values"/>, and stamps the message's resolution. The wait
/// token never leaves the server — the caller identifies the interaction by message id, and this
/// service re-derives the target. Team-scoped; also gates on conversation membership + allowed-responder.
/// </summary>
public interface IMessageInteractionService
{
    /// <summary>
    /// Respond to <paramref name="messageId"/>'s interactive component with option <paramref name="responseKey"/>
    /// as <paramref name="actorUserId"/>, carrying optional form <paramref name="values"/> (injected into the
    /// parked run). Throws <see cref="KeyNotFoundException"/> (404) when the message / interaction is absent,
    /// <see cref="InvalidOperationException"/> (400) when the interaction is closed, the key isn't a valid
    /// option, a required form field is missing, the caller may not respond, or the target was already resolved.
    /// </summary>
    Task RespondAsync(Guid teamId, Guid messageId, string responseKey, Guid actorUserId, string? comment, IReadOnlyDictionary<string, JsonElement>? values, CancellationToken cancellationToken);
}
