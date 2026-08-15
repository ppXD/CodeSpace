using System.Buffers.Text;
using System.Text;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class ListAgentRunLogsQueryHandler : IRequestHandler<ListAgentRunLogsQuery, AgentRunLogPage?>
{
    private const int MaximumPageSize = 100;

    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;

    public ListAgentRunLogsQueryHandler(CodeSpaceDbContext db, ICurrentTeam currentTeam)
    {
        _db = db;
        _currentTeam = currentTeam;
    }

    public async Task<AgentRunLogPage?> Handle(ListAgentRunLogsQuery request, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;
        if (!await _db.AgentRun.AsNoTracking().AnyAsync(run => run.TeamId == teamId && run.Id == request.AgentRunId, cancellationToken).ConfigureAwait(false)) return null;

        var cursor = AgentRunLogCursor.Decode(request.Cursor);
        var take = Math.Clamp(request.Limit, 1, MaximumPageSize);
        var query = _db.AgentRunLogStream.AsNoTracking().Where(stream => stream.TeamId == teamId && stream.AgentRunId == request.AgentRunId);
        if (cursor is { } after)
            query = query.Where(stream => stream.CreatedAt > after.CreatedAt || (stream.CreatedAt == after.CreatedAt && stream.Id.CompareTo(after.Id) > 0));

        var rows = await query.OrderBy(stream => stream.CreatedAt).ThenBy(stream => stream.Id).Take(take + 1)
            .Select(stream => new AgentRunLogListRow
            {
                StreamId = stream.Id, AgentRunId = stream.AgentRunId, StreamKind = stream.StreamKind,
                ContentType = stream.ContentType, ContentEncoding = stream.ContentEncoding, CaptureSource = stream.CaptureSource,
                Retention = stream.Retention, State = stream.State, Revision = stream.Revision, SegmentCount = stream.SegmentCount,
                TotalBytes = stream.TotalBytes, ContentDigest = stream.ContentDigest, CreatedAt = stream.CreatedAt,
                LastModifiedAt = stream.LastModifiedAt, CompletedAt = stream.CompletedAt, ErrorCode = stream.ErrorCode,
            }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.GetRange(0, take) : rows;
        return new AgentRunLogPage
        {
            Items = page.Select(AgentRunLogWire.Project).ToList(),
            NextCursor = hasMore ? new AgentRunLogCursor(page[^1].CreatedAt, page[^1].StreamId).Encode() : null,
        };
    }
}

public sealed class GetAgentRunLogQueryHandler : IRequestHandler<GetAgentRunLogQuery, AgentRunLogStreamSummary?>
{
    private readonly IAgentRunLogService _logs;
    private readonly ICurrentTeam _currentTeam;

    public GetAgentRunLogQueryHandler(IAgentRunLogService logs, ICurrentTeam currentTeam)
    {
        _logs = logs;
        _currentTeam = currentTeam;
    }

    public async Task<AgentRunLogStreamSummary?> Handle(GetAgentRunLogQuery request, CancellationToken cancellationToken)
    {
        var result = await _logs.GetMetadataAsync(_currentTeam.Id!.Value, request.StreamId, cancellationToken).ConfigureAwait(false);
        return result is AgentRunLogMetadataResult.Found found && found.Metadata.AgentRunId == request.AgentRunId ? AgentRunLogWire.Project(found.Metadata) : null;
    }
}

public sealed class ReadAgentRunLogRangeQueryHandler : IRequestHandler<ReadAgentRunLogRangeQuery, AgentRunLogRangeRead?>
{
    private const int MaximumRangeBytes = 1024 * 1024;

    private readonly IAgentRunLogService _logs;
    private readonly ICurrentTeam _currentTeam;

    public ReadAgentRunLogRangeQueryHandler(IAgentRunLogService logs, ICurrentTeam currentTeam)
    {
        _logs = logs;
        _currentTeam = currentTeam;
    }

    public async Task<AgentRunLogRangeRead?> Handle(ReadAgentRunLogRangeQuery request, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;
        var metadata = await _logs.GetMetadataAsync(teamId, request.StreamId, cancellationToken).ConfigureAwait(false);
        if (metadata is not AgentRunLogMetadataResult.Found found || found.Metadata.AgentRunId != request.AgentRunId) return null;

        var limit = Math.Clamp(request.LimitBytes, 1, MaximumRangeBytes);
        var result = await _logs.ReadRangeAsync(new AgentRunLogRangeRequest(teamId, request.StreamId, request.OffsetBytes, limit), cancellationToken).ConfigureAwait(false);
        return result switch
        {
            AgentRunLogRangeResult.Available available => AgentRunLogWire.Available(available),
            AgentRunLogRangeResult.Unavailable unavailable => AgentRunLogWire.Unavailable(found.Metadata, request.OffsetBytes, unavailable.Problem),
            _ => throw new InvalidOperationException("Unknown Agent Run log range result."),
        };
    }
}

internal static class AgentRunLogWire
{
    public static AgentRunLogStreamSummary Project(AgentRunLogListRow value) => new()
    {
        StreamId = value.StreamId,
        AgentRunId = value.AgentRunId,
        StreamKind = value.StreamKind,
        ContentType = value.ContentType,
        ContentEncoding = value.ContentEncoding,
        CaptureSource = value.CaptureSource,
        Retention = value.Retention.ToString(),
        Status = Status(value.State),
        Revision = value.Revision,
        SegmentCount = value.SegmentCount,
        TotalBytes = value.TotalBytes,
        Sha256 = value.ContentDigest == null ? null : Convert.ToHexStringLower(value.ContentDigest),
        CreatedAt = value.CreatedAt,
        LastModifiedAt = value.LastModifiedAt,
        CompletedAt = value.CompletedAt,
        ErrorCode = value.ErrorCode,
    };

    public static AgentRunLogStreamSummary Project(AgentRunLogStream value) => new()
    {
        StreamId = value.Id,
        AgentRunId = value.AgentRunId,
        StreamKind = value.StreamKind,
        ContentType = value.ContentType,
        ContentEncoding = value.ContentEncoding,
        CaptureSource = value.CaptureSource,
        Retention = value.Retention.ToString(),
        Status = Status(value.State),
        Revision = value.Revision,
        SegmentCount = value.SegmentCount,
        TotalBytes = value.TotalBytes,
        Sha256 = value.ContentDigest == null ? null : Convert.ToHexStringLower(value.ContentDigest),
        CreatedAt = value.CreatedAt,
        LastModifiedAt = value.LastModifiedAt,
        CompletedAt = value.CompletedAt,
        ErrorCode = value.ErrorCode,
    };

    public static AgentRunLogStreamSummary Project(AgentRunLogMetadata value) => new()
    {
        StreamId = value.StreamId,
        AgentRunId = value.AgentRunId,
        StreamKind = value.StreamKind,
        ContentType = value.ContentType,
        ContentEncoding = value.ContentEncoding,
        CaptureSource = value.CaptureSource,
        Retention = value.Retention.ToString(),
        Status = Status(value.State),
        Revision = value.Revision,
        SegmentCount = value.SegmentCount,
        TotalBytes = value.TotalBytes,
        Sha256 = value.Sha256,
        CreatedAt = value.CreatedAt,
        LastModifiedAt = value.LastModifiedAt,
        CompletedAt = value.CompletedAt,
        ErrorCode = value.ErrorCode,
    };

    public static AgentRunLogRangeRead Available(AgentRunLogRangeResult.Available value)
    {
        var next = checked(value.OffsetBytes + value.Bytes.LongLength);
        return new AgentRunLogRangeRead
        {
            Availability = AgentRunLogReadAvailability.Available,
            Stream = Project(value.Metadata),
            OffsetBytes = value.OffsetBytes,
            NextOffsetBytes = next,
            HasMore = next < value.Metadata.TotalBytes,
            IsRetryable = false,
            Content = value.Bytes,
        };
    }

    public static AgentRunLogRangeRead Unavailable(AgentRunLogMetadata metadata, long offsetBytes, AgentRunLogProblem problem) => new()
    {
        Availability = Availability(problem.Code),
        Stream = Project(metadata),
        OffsetBytes = offsetBytes,
        NextOffsetBytes = offsetBytes,
        HasMore = offsetBytes < metadata.TotalBytes,
        IsRetryable = problem.IsRetryable,
        ProblemCode = problem.Code.ToString(),
    };

    private static AgentRunLogStatus Status(AgentRunLogStreamState value) => value switch
    {
        AgentRunLogStreamState.Open => AgentRunLogStatus.Open,
        AgentRunLogStreamState.Completed => AgentRunLogStatus.Completed,
        AgentRunLogStreamState.Truncated => AgentRunLogStatus.Truncated,
        AgentRunLogStreamState.Unavailable => AgentRunLogStatus.Unavailable,
        AgentRunLogStreamState.Corrupt => AgentRunLogStatus.Corrupt,
        AgentRunLogStreamState.CaptureFailed => AgentRunLogStatus.CaptureFailed,
        _ => throw new InvalidOperationException($"Unknown Agent Run log state '{value}'."),
    };

    private static AgentRunLogReadAvailability Availability(AgentRunLogProblemCode value) => value switch
    {
        AgentRunLogProblemCode.InvalidRequest => AgentRunLogReadAvailability.InvalidRange,
        AgentRunLogProblemCode.Missing or AgentRunLogProblemCode.ArtifactMissing => AgentRunLogReadAvailability.PhysicalObjectMissing,
        AgentRunLogProblemCode.ArtifactCorrupt => AgentRunLogReadAvailability.IntegrityFailure,
        AgentRunLogProblemCode.AccessDenied => AgentRunLogReadAvailability.AccessDenied,
        AgentRunLogProblemCode.ProviderTimeout => AgentRunLogReadAvailability.ProviderTimeout,
        AgentRunLogProblemCode.Unsupported => AgentRunLogReadAvailability.Unsupported,
        AgentRunLogProblemCode.BackendUnavailable => AgentRunLogReadAvailability.BackendUnavailable,
        _ => AgentRunLogReadAvailability.BackendUnavailable,
    };
}

internal sealed class AgentRunLogListRow
{
    public Guid StreamId { get; init; }
    public Guid AgentRunId { get; init; }
    public string StreamKind { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public string? ContentEncoding { get; init; }
    public string CaptureSource { get; init; } = string.Empty;
    public ArtifactRetention Retention { get; init; }
    public AgentRunLogStreamState State { get; init; }
    public long Revision { get; init; }
    public long SegmentCount { get; init; }
    public long TotalBytes { get; init; }
    public byte[]? ContentDigest { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastModifiedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ErrorCode { get; init; }
}

internal readonly record struct AgentRunLogCursor(DateTimeOffset CreatedAt, Guid Id)
{
    public string Encode()
    {
        var raw = $"{CreatedAt.UtcTicks}\n{Id:N}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    public static AgentRunLogCursor? Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        try
        {
            var raw = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            var parts = raw.Split('\n');
            if (parts.Length == 2 && long.TryParse(parts[0], out var ticks) && ticks >= 0 && ticks <= DateTimeOffset.MaxValue.Ticks
                && Guid.TryParseExact(parts[1], "N", out var id) && id != Guid.Empty)
                return new AgentRunLogCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (FormatException) { }
        throw new InvalidOperationException("Invalid Agent Run log cursor.");
    }
}
