using System.ComponentModel.DataAnnotations;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class PageAgentRunEventsQueryHandler : IRequestHandler<PageAgentRunEventsQuery, AgentRunEventPage?>
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;

    public PageAgentRunEventsQueryHandler(CodeSpaceDbContext db, ICurrentTeam currentTeam)
    {
        _db = db;
        _currentTeam = currentTeam;
    }

    public async Task<AgentRunEventPage?> Handle(PageAgentRunEventsQuery request, CancellationToken cancellationToken)
    {
        EnsureValid(request);
        var teamId = _currentTeam.Id!.Value;
        if (!await _db.AgentRun.AsNoTracking().AnyAsync(run => run.Id == request.AgentRunId && run.TeamId == teamId, cancellationToken).ConfigureAwait(false)) return null;

        var cursor = request.TryGetCursor(out var parsedCursor) ? parsedCursor : 0;
        var rows = await PageRowsQuery(_db, request.AgentRunId, request.Direction, cursor, request.Limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasExtra = rows.Count > request.Limit;
        if (hasExtra) rows.RemoveAt(rows.Count - 1);
        if (request.Direction != AgentRunEventPageDirection.Newer) rows.Reverse();

        var runRows = _db.AgentRunEvent.AsNoTracking().Where(item => item.AgentRunId == request.AgentRunId);
        var hasOlder = request.Direction == AgentRunEventPageDirection.Newer
            ? await runRows.AnyAsync(item => item.Sequence <= cursor, cancellationToken).ConfigureAwait(false)
            : hasExtra;
        var hasNewer = request.Direction switch
        {
            AgentRunEventPageDirection.Newer => hasExtra,
            AgentRunEventPageDirection.Older => await runRows.AnyAsync(item => item.Sequence >= cursor, cancellationToken).ConfigureAwait(false),
            _ => false,
        };

        return new AgentRunEventPage
        {
            AgentRunId = request.AgentRunId,
            Mode = request.Direction.ToString(),
            RequestCursor = request.Cursor,
            Items = rows,
            HasOlder = hasOlder,
            HasNewer = hasNewer,
            NextOlderCursor = NextOlderCursor(rows, request.Direction, cursor, hasOlder),
            NextNewerCursor = (rows.Count == 0 ? EmptyNewerCursor(request.Direction, cursor) : rows[^1].Sequence).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The only row-bearing query: exact run predicate, strict sequence keyset, stable order, and Take before materialization.</summary>
    internal static IQueryable<AgentRunEventDto> PageRowsQuery(CodeSpaceDbContext db, Guid runId, AgentRunEventPageDirection direction, long cursor, int take)
    {
        var rows = db.AgentRunEvent.AsNoTracking().Where(item => item.AgentRunId == runId);
        if (direction == AgentRunEventPageDirection.Older) rows = rows.Where(item => item.Sequence < cursor);
        if (direction == AgentRunEventPageDirection.Newer) rows = rows.Where(item => item.Sequence > cursor);

        var ordered = direction == AgentRunEventPageDirection.Newer
            ? rows.OrderBy(item => item.Sequence)
            : rows.OrderByDescending(item => item.Sequence);

        return ordered.Take(take).Select(item => new AgentRunEventDto
        {
            Sequence = item.Sequence,
            Kind = item.Kind,
            Text = item.Text,
            Data = item.DataJson,
            DataArtifactId = item.DataArtifactId,
            OccurredAt = item.OccurredAt,
        });
    }

    private static long EmptyNewerCursor(AgentRunEventPageDirection direction, long cursor) => direction switch
    {
        AgentRunEventPageDirection.Newer => cursor,
        AgentRunEventPageDirection.Older => cursor - 1,
        _ => 0,
    };

    private static string? NextOlderCursor(IReadOnlyList<AgentRunEventDto> rows, AgentRunEventPageDirection direction, long cursor, bool hasOlder)
    {
        if (!hasOlder) return null;
        var value = rows.Count == 0 && direction == AgentRunEventPageDirection.Newer ? cursor : rows[0].Sequence;
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void EnsureValid(PageAgentRunEventsQuery request)
    {
        var errors = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), errors, validateAllProperties: true)) return;
        throw new AgentRunEventPageRequestException(errors.Select(error => error.ErrorMessage ?? "Invalid value.").ToList());
    }
}
