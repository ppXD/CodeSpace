using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Chat;

public sealed class ChatBotService : IChatBotService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IMessageService _messages;

    public ChatBotService(CodeSpaceDbContext db, IMessageService messages)
    {
        _db = db;
        _messages = messages;
    }

    /// <summary>The bot's display name + author label across every team.</summary>
    public const string BotDisplayName = "CodeSpace";

    public async Task<MessageView> PostAsBotAsync(Guid conversationId, string body, MessageInteraction? interaction, CancellationToken cancellationToken)
    {
        var teamId = await ResolveConversationTeamAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var botId = await GetOrCreateTeamBotAsync(teamId, cancellationToken).ConfigureAwait(false);

        await EnsureConversationMemberAsync(teamId, conversationId, botId, cancellationToken).ConfigureAwait(false);

        return interaction == null
            ? await _messages.PostAsync(teamId, botId, conversationId, body, replyToMessageId: null, cancellationToken).ConfigureAwait(false)
            : await _messages.PostInteractiveAsync(teamId, botId, conversationId, body, interaction, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ConversationBelongsToTeamAsync(Guid conversationId, Guid teamId, CancellationToken cancellationToken) =>
        // Team-scoped ALIVE read: a foreign-team, unknown, or soft-deleted conversation finds nothing (fail-closed).
        // The caller asserts this BEFORE PostAsBotAsync, which would otherwise derive the team FROM the conversation
        // and post cross-tenant — or park a card into a deleted room every list/get read hides. Mirrors the
        // team-scoped reads elsewhere in this service.
        await _db.Conversation.AsNoTracking()
            .AnyAsync(c => c.Id == conversationId && c.TeamId == teamId && c.DeletedDate == null, cancellationToken).ConfigureAwait(false);

    public async Task<Guid> GetOrCreateTeamBotAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var email = BotEmail(teamId);

        var existing = await FindBotIdAsync(email, cancellationToken).ConfigureAwait(false);
        if (existing.HasValue) return existing.Value;

        try
        {
            return await CreateBotAsync(teamId, email, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Lost a create race — the unique app_user(email) index rejected the duplicate. Re-read
            // the winner (mirrors the dm_key race-safe find-or-create added in migration 0029).
            _db.ChangeTracker.Clear();
            return await FindBotIdAsync(email, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Bot for team {teamId} not found after a create race.");
        }
    }

    /// <summary>The team's bot email — deterministic so it's the race-safe unique key for get-or-create. Internal for the pin test.</summary>
    internal static string BotEmail(Guid teamId) => $"codespace-bot.{teamId:N}@bot.codespace.local";

    private async Task<Guid?> FindBotIdAsync(string email, CancellationToken cancellationToken) =>
        // This service MANAGES the bot, so it must see bot users even though the global User query
        // filter hides them by default — bypass the filter for this lookup specifically.
        await _db.User.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.Email == email && u.IsBot && u.DeletedDate == null)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private async Task<Guid> CreateBotAsync(Guid teamId, string email, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var botId = Guid.NewGuid();

        _db.User.Add(new User
        {
            Id = botId,
            Email = email,
            Name = BotDisplayName,
            IsBot = true,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
            CreatedDate = now,
            LastModifiedDate = now,
        });

        _db.TeamMembership.Add(new TeamMembership
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = botId,
            Role = TeamRole.Member,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
            CreatedDate = now,
            LastModifiedDate = now,
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return botId;
    }

    private async Task<Guid> ResolveConversationTeamAsync(Guid conversationId, CancellationToken cancellationToken) =>
        await _db.Conversation.AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => (Guid?)c.TeamId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Conversation {conversationId} not found.");

    private async Task EnsureConversationMemberAsync(Guid teamId, Guid conversationId, Guid botId, CancellationToken cancellationToken)
    {
        var alreadyMember = await _db.ConversationMember.AsNoTracking()
            .AnyAsync(m => m.ConversationId == conversationId && m.UserId == botId && m.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

        if (alreadyMember) return;

        var now = DateTimeOffset.UtcNow;
        _db.ConversationMember.Add(new ConversationMember
        {
            ConversationId = conversationId,
            UserId = botId,
            TeamId = teamId,
            Role = ConversationMemberRole.Member,
            JoinedDate = now,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
            CreatedDate = now,
            LastModifiedDate = now,
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Lost a join race — the (conversation_id, user_id) PK rejected the duplicate. The bot is
            // already a member, which is all we needed; drop the failed add and continue.
            _db.ChangeTracker.Clear();
        }
    }
}
