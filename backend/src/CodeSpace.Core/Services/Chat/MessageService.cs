using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using CodeSpace.Messages.Queries.Chat;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Chat;

public sealed class MessageService : IMessageService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public MessageService(CodeSpaceDbContext db) { _db = db; }

    /// <summary>
    /// Hard cap on a message body, in characters. Generous for a code-friendly chat (long
    /// snippets / stack traces) while bounding one row + the FTS tsvector it generates, so a
    /// single giant paste can't bloat the hot table or a page payload. Pinned by a test.
    /// </summary>
    public const int MaxBodyLength = 16_000;

    // ─── Post ────────────────────────────────────────────────────────────────────

    public Task<MessageView> PostAsync(Guid teamId, Guid authorUserId, Guid conversationId, string body, Guid? replyToMessageId, CancellationToken cancellationToken) =>
        PostCoreAsync(teamId, authorUserId, conversationId, body, replyToMessageId, interaction: null, cancellationToken);

    public Task<MessageView> PostInteractiveAsync(Guid teamId, Guid authorUserId, Guid conversationId, string body, MessageInteraction interaction, CancellationToken cancellationToken) =>
        PostCoreAsync(teamId, authorUserId, conversationId, body, replyToMessageId: null, interaction, cancellationToken);

    private async Task<MessageView> PostCoreAsync(Guid teamId, Guid authorUserId, Guid conversationId, string body, Guid? replyToMessageId, MessageInteraction? interaction, CancellationToken cancellationToken)
    {
        EnsureValidBody(body);

        await EnsureActiveMemberAsync(teamId, conversationId, authorUserId, cancellationToken).ConfigureAwait(false);

        if (replyToMessageId.HasValue)
            await EnsureReplyTargetInConversationAsync(teamId, conversationId, replyToMessageId.Value, cancellationToken).ConfigureAwait(false);

        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            TeamId = teamId,
            AuthorUserId = authorUserId,
            Body = body,
            ReplyToMessageId = replyToMessageId,
            CreatedDate = DateTimeOffset.UtcNow,
            InteractionJson = MessageInteractionJson.Serialize(interaction),
        };

        var references = BuildReferences(message.Id, teamId, body);

        _db.Message.Add(message);
        _db.MessageReference.AddRange(references);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return MapView(message, references);
    }

    // ─── List (keyset pagination, newest-first) ────────────────────────────────────

    public async Task<MessagePage> ListAsync(Guid teamId, Guid userId, Guid conversationId, Guid? beforeId, int limit, CancellationToken cancellationToken)
    {
        await EnsureActiveMemberAsync(teamId, conversationId, userId, cancellationToken).ConfigureAwait(false);

        var pageSize = Math.Clamp(limit, 1, ListMessagesQuery.MaxPageSize);

        var rows = await QueryPageAsync(teamId, conversationId, beforeId, pageSize + 1, cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > pageSize;
        var page = hasMore ? rows.Take(pageSize).ToList() : rows;

        var views = await MapPageViewsAsync(page, cancellationToken).ConfigureAwait(false);

        return new MessagePage { Messages = views, HasMore = hasMore, NextBeforeId = hasMore ? page[^1].Id : null };
    }

    // ─── Edit ──────────────────────────────────────────────────────────────────────

    public async Task<MessageView> EditAsync(Guid teamId, Guid editorUserId, Guid messageId, string newBody, CancellationToken cancellationToken)
    {
        EnsureValidBody(newBody);

        var message = await LoadActiveMessageAsync(teamId, messageId, cancellationToken).ConfigureAwait(false);

        if (message.AuthorUserId != editorUserId) throw new InvalidOperationException("Only the author can edit a message.");

        message.Body = newBody;
        message.EditedDate = DateTimeOffset.UtcNow;

        await ClearReferencesAsync(messageId, cancellationToken).ConfigureAwait(false);

        var newReferences = BuildReferences(messageId, teamId, newBody);
        _db.MessageReference.AddRange(newReferences);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return MapView(message, newReferences);
    }

    // ─── Delete (soft) ───────────────────────────────────────────────────────────--

    public async Task DeleteAsync(Guid teamId, Guid actorUserId, Guid messageId, CancellationToken cancellationToken)
    {
        var message = await LoadActiveMessageAsync(teamId, messageId, cancellationToken).ConfigureAwait(false);

        if (message.AuthorUserId != actorUserId) throw new InvalidOperationException("Only the author can delete a message.");

        message.DeletedDate = DateTimeOffset.UtcNow;

        await ClearReferencesAsync(messageId, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─── Read cursor (forward-only) ─────────────────────────────────────────────────

    public async Task MarkReadAsync(Guid teamId, Guid userId, Guid conversationId, Guid lastReadMessageId, CancellationToken cancellationToken)
    {
        var member = await LoadMemberAsync(teamId, conversationId, userId, cancellationToken).ConfigureAwait(false);

        var targetInstant = await MessageInstantAsync(teamId, conversationId, lastReadMessageId, cancellationToken).ConfigureAwait(false);
        if (targetInstant == null) throw new InvalidOperationException("Cannot mark read at a message that is not in this conversation.");

        if (!await IsForwardAsync(teamId, conversationId, member.LastReadMessageId, targetInstant.Value, cancellationToken).ConfigureAwait(false)) return;

        member.LastReadMessageId = lastReadMessageId;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─── Validation ──────────────────────────────────────────────────────────────--

    private static void EnsureValidBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("Message body cannot be empty.", nameof(body));
        if (body.Length > MaxBodyLength) throw new ArgumentException($"Message body exceeds the {MaxBodyLength}-character limit.", nameof(body));
    }

    // ─── Membership / tenancy gates ─────────────────────────────────────────────────

    private async Task EnsureActiveMemberAsync(Guid teamId, Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var isMember = await _db.ConversationMember.AsNoTracking()
            .AnyAsync(m => m.ConversationId == conversationId && m.UserId == userId && m.TeamId == teamId && m.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

        if (!isMember) throw new InvalidOperationException("Caller is not an active member of this conversation.");
    }

    private async Task EnsureReplyTargetInConversationAsync(Guid teamId, Guid conversationId, Guid replyToMessageId, CancellationToken cancellationToken)
    {
        var exists = await _db.Message.AsNoTracking()
            .AnyAsync(m => m.Id == replyToMessageId && m.ConversationId == conversationId && m.TeamId == teamId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists) throw new InvalidOperationException("Reply target must be a message in the same conversation.");
    }

    private async Task<ConversationMember> LoadMemberAsync(Guid teamId, Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        return await _db.ConversationMember
            .SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == userId && m.TeamId == teamId && m.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Caller is not an active member of this conversation.");
    }

    private async Task<Message> LoadActiveMessageAsync(Guid teamId, Guid messageId, CancellationToken cancellationToken)
    {
        return await _db.Message
            .SingleOrDefaultAsync(m => m.Id == messageId && m.TeamId == teamId && m.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Message {messageId} not found.");
    }

    // ─── Keyset page query ───────────────────────────────────────────────────────--

    // Mapped columns only (the generated search_tsv is DB-only and intentionally omitted). The
    // ORDER BY id DESC rides the (conversation_id, id DESC) index — UUID v7 ids sort by creation
    // time, so this IS chronological order. EF can't express a uuid '<' cursor in LINQ, so the
    // keyset predicate lives here as parameterised SQL (column list is a constant — no injection).
    private const string MessageColumns =
        "id, conversation_id, team_id, author_user_id, body, reply_to_message_id, created_date, edited_date, deleted_date, interaction_json";

    private async Task<List<Message>> QueryPageAsync(Guid teamId, Guid conversationId, Guid? beforeId, int take, CancellationToken cancellationToken)
    {
        var parameters = new List<NpgsqlParameter>
        {
            new("conv", conversationId),
            new("team", teamId),
            new("take", take),
        };

        var cursorClause = string.Empty;
        if (beforeId.HasValue)
        {
            cursorClause = " AND id < @before";
            parameters.Add(new NpgsqlParameter("before", beforeId.Value));
        }

        var sql = "SELECT " + MessageColumns + " FROM message WHERE conversation_id = @conv AND team_id = @team"
            + cursorClause + " ORDER BY id DESC LIMIT @take";

        return await _db.Message.FromSqlRaw(sql, parameters.ToArray()).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─── Reference extraction + persistence ─────────────────────────────────────────

    private async Task ClearReferencesAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var existing = await _db.MessageReference.Where(r => r.MessageId == messageId).ToListAsync(cancellationToken).ConfigureAwait(false);
        _db.MessageReference.RemoveRange(existing);
    }

    private static List<MessageReference> BuildReferences(Guid messageId, Guid teamId, string body)
    {
        var now = DateTimeOffset.UtcNow;

        return MessageReferenceParser.Parse(body)
            .Select(r => new MessageReference
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                TeamId = teamId,
                RefType = r.RefType,
                RefId = r.RefId,
                RefMetadataJson = SerializeMetadata(r.Label),
                CreatedDate = now,
            })
            .ToList();
    }

    // ─── Read-cursor forward check ──────────────────────────────────────────────────

    private async Task<bool> IsForwardAsync(Guid teamId, Guid conversationId, Guid? currentCursor, DateTimeOffset targetInstant, CancellationToken cancellationToken)
    {
        if (currentCursor == null) return true;

        var currentInstant = await MessageInstantAsync(teamId, conversationId, currentCursor.Value, cancellationToken).ConfigureAwait(false);
        return currentInstant == null || targetInstant >= currentInstant.Value;
    }

    private async Task<DateTimeOffset?> MessageInstantAsync(Guid teamId, Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        return await _db.Message.AsNoTracking()
            .Where(m => m.Id == messageId && m.ConversationId == conversationId && m.TeamId == teamId)
            .Select(m => (DateTimeOffset?)m.CreatedDate)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─── View mapping ────────────────────────────────────────────────────────────--

    private async Task<IReadOnlyList<MessageView>> MapPageViewsAsync(List<Message> messages, CancellationToken cancellationToken)
    {
        if (messages.Count == 0) return Array.Empty<MessageView>();

        var ids = messages.Select(m => m.Id).ToList();

        var references = await _db.MessageReference.AsNoTracking()
            .Where(r => ids.Contains(r.MessageId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var byMessage = references.ToLookup(r => r.MessageId);

        return messages.Select(m => MapView(m, byMessage[m.Id].ToList())).ToList();
    }

    private static MessageView MapView(Message message, IReadOnlyList<MessageReference> references)
    {
        var deleted = message.DeletedDate != null;

        return new MessageView
        {
            Id = message.Id,
            ConversationId = message.ConversationId,
            AuthorUserId = message.AuthorUserId,
            Body = deleted ? string.Empty : message.Body,
            ReplyToMessageId = message.ReplyToMessageId,
            CreatedDate = message.CreatedDate,
            EditedDate = message.EditedDate,
            IsDeleted = deleted,
            References = deleted ? Array.Empty<MessageReferenceView>() : references.Select(MapReferenceView).ToList(),
            Interaction = deleted ? null : MapInteractionView(message.InteractionJson),
        };
    }

    // Project the stored interaction to its client view — deliberately DROPPING the response target,
    // so the wait token never leaves the server. A client renders from the component and responds by
    // message id; the backend re-derives the target.
    private static MessageInteractionView? MapInteractionView(string? interactionJson)
    {
        // Tolerant on the read path: an unrenderable card (unknown/newer kind, malformed jsonb) degrades
        // to "no interaction" rather than throwing and bricking the whole conversation's message list.
        var interaction = MessageInteractionJson.TryDeserialize(interactionJson);
        if (interaction == null) return null;

        return new MessageInteractionView
        {
            Version = interaction.Version,
            Component = interaction.Component,
            AllowedResponderUserIds = interaction.AllowedResponderUserIds,
            Resolve = interaction.Resolve,
            Responses = interaction.Responses,
            State = interaction.State,
            Resolution = interaction.Resolution,
        };
    }

    private static MessageReferenceView MapReferenceView(MessageReference reference) => new()
    {
        RefType = reference.RefType,
        RefId = reference.RefId,
        Label = DeserializeLabel(reference.RefMetadataJson),
    };

    // ─── Reference metadata (label cache) ───────────────────────────────────────────

    private static string? SerializeMetadata(string? label) =>
        label is null ? null : JsonSerializer.Serialize(new RefMetadata(label));

    private static string? DeserializeLabel(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<RefMetadata>(json)?.Label;

    private sealed record RefMetadata([property: JsonPropertyName("label")] string Label);
}
