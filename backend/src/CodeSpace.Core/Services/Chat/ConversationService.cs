using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Chat;

public sealed class ConversationService : IConversationService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public ConversationService(CodeSpaceDbContext db) { _db = db; }

    public async Task<Guid> CreateChannelAsync(Guid teamId, string name, string slug, bool isPrivate, Guid actorUserId, CancellationToken cancellationToken)
    {
        var normalizedSlug = NormalizeSlug(slug);

        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Channel name is required.", nameof(name));
        if (normalizedSlug.Length == 0) throw new ArgumentException("Channel slug must contain at least one url-safe character.", nameof(slug));

        await EnsureSlugFreeAsync(teamId, normalizedSlug, cancellationToken).ConfigureAwait(false);

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Kind = ConversationKind.Channel,
            Slug = normalizedSlug,
            Name = name.Trim(),
            Visibility = isPrivate ? ConversationVisibility.Private : ConversationVisibility.Public,
        };

        _db.Conversation.Add(conversation);
        _db.ConversationMember.Add(BuildMember(conversation.Id, teamId, actorUserId, ConversationMemberRole.Owner));

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return conversation.Id;
    }

    public async Task<Guid> GetOrCreateDirectAsync(Guid teamId, Guid actorUserId, Guid otherUserId, CancellationToken cancellationToken)
    {
        if (actorUserId == otherUserId) throw new ArgumentException("Cannot open a direct message with yourself.", nameof(otherUserId));

        var dmKey = BuildDmKey(actorUserId, otherUserId);

        var existingId = await FindDirectByKeyAsync(teamId, dmKey, cancellationToken).ConfigureAwait(false);
        if (existingId.HasValue) return existingId.Value;

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Kind = ConversationKind.Direct,
            DmKey = dmKey,
        };

        _db.Conversation.Add(conversation);
        _db.ConversationMember.Add(BuildMember(conversation.Id, teamId, actorUserId, ConversationMemberRole.Member));
        _db.ConversationMember.Add(BuildMember(conversation.Id, teamId, otherUserId, ConversationMemberRole.Member));

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return conversation.Id;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent open won the dm_key race. Detach our losing rows + return the
            // winner so the pair still resolves to exactly one DM.
            DetachConversationGraph(conversation);
            var winner = await FindDirectByKeyAsync(teamId, dmKey, cancellationToken).ConfigureAwait(false);
            return winner ?? throw new InvalidOperationException("DM dm_key unique violation but no existing row found — index/migration drift.");
        }
    }

    public async Task<Guid> CreateGroupAsync(Guid teamId, string? name, IReadOnlyList<Guid> memberUserIds, Guid actorUserId, CancellationToken cancellationToken)
    {
        var members = DistinctMembersIncludingActor(memberUserIds, actorUserId);

        if (members.Count < 2) throw new ArgumentException("A group needs at least two distinct members.", nameof(memberUserIds));

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Kind = ConversationKind.Group,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
        };

        _db.Conversation.Add(conversation);
        foreach (var userId in members)
            _db.ConversationMember.Add(BuildMember(conversation.Id, teamId, userId, userId == actorUserId ? ConversationMemberRole.Owner : ConversationMemberRole.Member));

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return conversation.Id;
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListForUserAsync(Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        var conversationIds = await _db.ConversationMember.AsNoTracking()
            .Where(m => m.TeamId == teamId && m.UserId == userId && m.DeletedDate == null)
            .Select(m => m.ConversationId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (conversationIds.Count == 0) return Array.Empty<ConversationSummary>();

        var summaries = await QuerySummaries(c => c.TeamId == teamId && conversationIds.Contains(c.Id) && c.DeletedDate == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return summaries.OrderByDescending(s => s.CreatedDate).ToList();
    }

    public async Task<ConversationSummary?> GetAsync(Guid teamId, Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var isMember = await IsActiveMemberAsync(conversationId, userId, cancellationToken).ConfigureAwait(false);
        if (!isMember) return null;   // never leak existence of a conversation the caller isn't in

        return await QuerySummaries(c => c.TeamId == teamId && c.Id == conversationId && c.DeletedDate == null)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddMemberAsync(Guid teamId, Guid actorUserId, Guid conversationId, Guid newMemberUserId, CancellationToken cancellationToken)
    {
        var conversation = await LoadConversationAsync(teamId, conversationId, cancellationToken).ConfigureAwait(false);

        if (conversation.Kind == ConversationKind.Direct) throw new InvalidOperationException("Direct messages are fixed pairs — members cannot be added.");

        var actorIsMember = await IsActiveMemberAsync(conversationId, actorUserId, cancellationToken).ConfigureAwait(false);
        if (!actorIsMember) throw new InvalidOperationException("Only a member can add others to this conversation.");

        await UpsertMemberAsync(conversationId, teamId, newMemberUserId, cancellationToken).ConfigureAwait(false);
    }

    // ─── Private helpers ─────────────────────────────────────────────────────────

    private async Task EnsureSlugFreeAsync(Guid teamId, string slug, CancellationToken cancellationToken)
    {
        var taken = await _db.Conversation.AsNoTracking()
            .AnyAsync(c => c.TeamId == teamId && c.Slug == slug && c.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

        if (taken) throw new InvalidOperationException($"A channel with slug '{slug}' already exists in this team.");
    }

    private async Task<Guid?> FindDirectByKeyAsync(Guid teamId, string dmKey, CancellationToken cancellationToken)
    {
        var id = await _db.Conversation.AsNoTracking()
            .Where(c => c.TeamId == teamId && c.DmKey == dmKey && c.DeletedDate == null)
            .Select(c => (Guid?)c.Id)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return id;
    }

    private async Task<Conversation> LoadConversationAsync(Guid teamId, Guid conversationId, CancellationToken cancellationToken)
    {
        return await _db.Conversation
            .SingleOrDefaultAsync(c => c.TeamId == teamId && c.Id == conversationId && c.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Conversation {conversationId} not found in team {teamId}.");
    }

    private async Task<bool> IsActiveMemberAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        return await _db.ConversationMember.AsNoTracking()
            .AnyAsync(m => m.ConversationId == conversationId && m.UserId == userId && m.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Idempotent add. A previously-removed (soft-deleted) member resurrects the existing row
    /// rather than inserting a duplicate (composite PK would otherwise throw).
    /// </summary>
    private async Task UpsertMemberAsync(Guid conversationId, Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        var existing = await _db.ConversationMember
            .SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is { DeletedDate: null }) return;   // already active — no-op

        if (existing != null)
        {
            existing.DeletedDate = null;
            existing.JoinedDate = DateTimeOffset.UtcNow;
        }
        else
        {
            _db.ConversationMember.Add(BuildMember(conversationId, teamId, userId, ConversationMemberRole.Member));
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private IQueryable<ConversationSummary> QuerySummaries(System.Linq.Expressions.Expression<Func<Conversation, bool>> predicate)
    {
        return _db.Conversation.AsNoTracking()
            .Where(predicate)
            .Select(c => new ConversationSummary
            {
                Id = c.Id,
                Kind = c.Kind,
                Slug = c.Slug,
                Name = c.Name,
                Description = c.Description,
                Visibility = c.Visibility,
                Archived = c.Archived,
                MemberCount = _db.ConversationMember.Count(m => m.ConversationId == c.Id && m.DeletedDate == null),
                MemberUserIds = _db.ConversationMember
                    .Where(m => m.ConversationId == c.Id && m.DeletedDate == null)
                    .Select(m => m.UserId)
                    .ToList(),
                CreatedDate = c.CreatedDate,
            });
    }

    private static ConversationMember BuildMember(Guid conversationId, Guid teamId, Guid userId, ConversationMemberRole role) => new()
    {
        ConversationId = conversationId,
        UserId = userId,
        TeamId = teamId,
        Role = role,
        JoinedDate = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Order-independent DM key so (A,B) and (B,A) resolve to the same conversation. Sorting
    /// the two ids and joining with ':' makes it deterministic without a separate lookup.
    /// </summary>
    private static string BuildDmKey(Guid a, Guid b)
    {
        var (lo, hi) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
        return $"{lo}:{hi}";
    }

    private static List<Guid> DistinctMembersIncludingActor(IReadOnlyList<Guid> memberUserIds, Guid actorUserId)
    {
        var set = new HashSet<Guid>(memberUserIds) { actorUserId };
        return set.ToList();
    }

    /// <summary>Normalize a channel slug to lowercase url-safe — letters / digits / hyphen,
    /// collapsing runs of other chars to a single hyphen. Mirrors the project slug rules.</summary>
    private static string NormalizeSlug(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        var lastHyphen = true;   // leading-hyphen suppression

        foreach (var ch in raw.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastHyphen = false;
            }
            else if (!lastHyphen)
            {
                sb.Append('-');
                lastHyphen = true;
            }
        }

        return sb.ToString().Trim('-');
    }

    private void DetachConversationGraph(Conversation conversation)
    {
        foreach (var entry in _db.ChangeTracker.Entries<ConversationMember>().Where(e => e.Entity.ConversationId == conversation.Id).ToList())
            entry.State = EntityState.Detached;

        _db.Entry(conversation).State = EntityState.Detached;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
