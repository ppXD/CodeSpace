using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Chat;

public sealed class MessageInteractionService : IMessageInteractionService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IWorkflowResumeService _resume;

    public MessageInteractionService(CodeSpaceDbContext db, IWorkflowResumeService resume)
    {
        _db = db;
        _resume = resume;
    }

    public async Task RespondAsync(Guid teamId, Guid messageId, string responseKey, Guid actorUserId, string? comment, IReadOnlyDictionary<string, JsonElement>? values, CancellationToken cancellationToken)
    {
        var message = await LoadMessageAsync(teamId, messageId, cancellationToken).ConfigureAwait(false);

        var interaction = MessageInteractionJson.Deserialize(message.InteractionJson)
            ?? throw new KeyNotFoundException($"Message {messageId} has no interaction to respond to.");

        EnsureOpen(interaction);
        EnsureValidResponse(interaction, responseKey);
        EnsureCommentIfRequired(interaction, responseKey, comment);
        EnsureRequiredFields(interaction, values);
        await EnsureAllowedResponderAsync(teamId, message.ConversationId, interaction, actorUserId, cancellationToken).ConfigureAwait(false);

        var resolved = await ResolveTargetAsync(interaction.Target, responseKey, actorUserId, comment, values, teamId, cancellationToken).ConfigureAwait(false);

        if (!resolved) throw new InvalidOperationException("This interaction was already handled.");

        await StampResolutionAsync(message, interaction, responseKey, actorUserId, comment, values, cancellationToken).ConfigureAwait(false);
    }

    // ─── Load ────────────────────────────────────────────────────────────────────

    private async Task<Message> LoadMessageAsync(Guid teamId, Guid messageId, CancellationToken cancellationToken) =>
        await _db.Message
            .SingleOrDefaultAsync(m => m.Id == messageId && m.TeamId == teamId && m.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Message {messageId} not found.");

    // ─── Validation ──────────────────────────────────────────────────────────────

    private static void EnsureOpen(MessageInteraction interaction)
    {
        if (interaction.State != InteractionState.Open) throw new InvalidOperationException("This message's interaction is no longer open.");
    }

    private static void EnsureValidResponse(MessageInteraction interaction, string responseKey)
    {
        if (!MessageInteractionPolicy.IsValidResponse(interaction, responseKey)) throw new InvalidOperationException($"'{responseKey}' is not a valid response for this interaction.");
    }

    private static void EnsureCommentIfRequired(MessageInteraction interaction, string responseKey, string? comment)
    {
        if (MessageInteractionPolicy.RequiresComment(interaction, responseKey) && string.IsNullOrWhiteSpace(comment)) throw new InvalidOperationException($"Responding with '{responseKey}' requires a comment.");
    }

    private static void EnsureRequiredFields(MessageInteraction interaction, IReadOnlyDictionary<string, JsonElement>? values)
    {
        var missing = MessageInteractionPolicy.MissingRequiredFields(interaction, values);

        if (missing.Count > 0) throw new InvalidOperationException($"Missing required field(s): {string.Join(", ", missing)}.");
    }

    private async Task EnsureAllowedResponderAsync(Guid teamId, Guid conversationId, MessageInteraction interaction, Guid actorUserId, CancellationToken cancellationToken)
    {
        var isMember = await _db.ConversationMember.AsNoTracking()
            .AnyAsync(m => m.ConversationId == conversationId && m.UserId == actorUserId && m.TeamId == teamId && m.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

        if (!MessageInteractionPolicy.IsAllowedResponder(interaction, actorUserId, isMember)) throw new InvalidOperationException("You are not allowed to respond to this message.");
    }

    // ─── Dispatch (route the response to the interaction's target) ──────────────────

    private async Task<bool> ResolveTargetAsync(InteractionTarget target, string responseKey, Guid actorUserId, string? comment, IReadOnlyDictionary<string, JsonElement>? values, Guid teamId, CancellationToken cancellationToken) =>
        target switch
        {
            WorkflowWaitTarget wait => await _resume.ResumeByActionTokenAsync(wait.Token, responseKey, actorUserId, comment, values, teamId, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported interaction target '{target.GetType().Name}'."),
        };

    // ─── Resolution mirror (the workflow wait is the authority; this reflects it for display) ───────

    private async Task StampResolutionAsync(Message message, MessageInteraction interaction, string responseKey, Guid actorUserId, string? comment, IReadOnlyDictionary<string, JsonElement>? values, CancellationToken cancellationToken)
    {
        var resolved = interaction with
        {
            State = InteractionState.Resolved,
            Resolution = new InteractionResolution
            {
                ResponseKey = responseKey,
                ByUserId = actorUserId,
                Comment = comment,
                Values = values,
                AtUtc = DateTimeOffset.UtcNow,
            },
        };

        message.InteractionJson = MessageInteractionJson.Serialize(resolved);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
