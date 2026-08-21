using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

/// <summary>
/// Metadata-first, tenant/run/event-scoped payload read. The event row proves the artifact reference before the
/// bounded artifact reader touches bytes; a caller cannot borrow an artifact id or global event sequence from a
/// different execution attempt.
/// </summary>
public sealed class ReadAgentRunEventDataRangeQueryHandler : IRequestHandler<ReadAgentRunEventDataRangeQuery, AgentRunEventDataRangeRead?>
{
    private readonly CodeSpaceDbContext _db;
    private readonly IArtifactRangeReader _artifacts;
    private readonly ICurrentTeam _currentTeam;

    public ReadAgentRunEventDataRangeQueryHandler(CodeSpaceDbContext db, IArtifactRangeReader artifacts, ICurrentTeam currentTeam)
    {
        _db = db;
        _artifacts = artifacts;
        _currentTeam = currentTeam;
    }

    public async Task<AgentRunEventDataRangeRead?> Handle(ReadAgentRunEventDataRangeQuery request, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;
        var source = await _db.AgentRunEvent.AsNoTracking()
            .Where(@event => @event.AgentRunId == request.AgentRunId && @event.Sequence == request.EventSequence && @event.Run.TeamId == teamId)
            .Select(@event => new EventDataSource(@event.DataArtifactId))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (source == null) return null;
        if (source.DataArtifactId is not { } artifactId)
            return AgentRunEventDataWire.Unavailable(request, null, AgentRunEventDataReadAvailability.NotReferenced);
        if (!AgentRunEventDataWire.ValidRange(request))
            return AgentRunEventDataWire.Unavailable(request, artifactId, AgentRunEventDataReadAvailability.InvalidRange);

        var read = await _artifacts.ReadRangeAsync(teamId, artifactId, request.OffsetBytes, request.LimitBytes, cancellationToken).ConfigureAwait(false);
        return read.State == ArtifactRangeReadState.Available
            ? AgentRunEventDataWire.Available(request, artifactId, read)
            : AgentRunEventDataWire.Unavailable(request, artifactId, AgentRunEventDataWire.Availability(read.State), read);
    }

    private sealed record EventDataSource(Guid? DataArtifactId);
}

internal static class AgentRunEventDataWire
{
    internal const int MaximumRangeBytes = 1024 * 1024;

    internal static bool ValidRange(ReadAgentRunEventDataRangeQuery request) => request.OffsetBytes >= 0
        && request.LimitBytes is > 0 and <= MaximumRangeBytes
        && request.OffsetBytes <= long.MaxValue - request.LimitBytes;

    internal static AgentRunEventDataRangeRead Available(ReadAgentRunEventDataRangeQuery request, Guid artifactId, ArtifactRangeReadResult read)
    {
        var content = read.Bytes ?? throw new InvalidOperationException("An available Agent Run event payload range has no bytes.");
        if (content.Length > request.LimitBytes)
            throw new InvalidOperationException("Artifact range reader returned more Agent Run event payload bytes than requested.");
        var total = read.TotalLength ?? throw new InvalidOperationException("An available Agent Run event payload range has no total length.");
        var next = checked(request.OffsetBytes + content.LongLength);
        return new AgentRunEventDataRangeRead
        {
            AgentRunId = request.AgentRunId,
            EventSequence = request.EventSequence,
            DataArtifactId = artifactId,
            Availability = AgentRunEventDataReadAvailability.Available,
            OffsetBytes = request.OffsetBytes,
            ReturnedBytes = content.Length,
            TotalBytes = total,
            NextOffsetBytes = next < total ? next : null,
            Sha256 = read.Sha256,
            ContentType = read.ContentType,
            IntegrityVerified = read.IntegrityVerified,
            IsRetryable = false,
            Content = content,
        };
    }

    internal static AgentRunEventDataRangeRead Unavailable(ReadAgentRunEventDataRangeQuery request, Guid? artifactId,
        AgentRunEventDataReadAvailability availability, ArtifactRangeReadResult? read = null) => new()
    {
        AgentRunId = request.AgentRunId,
        EventSequence = request.EventSequence,
        DataArtifactId = artifactId,
        Availability = availability,
        OffsetBytes = request.OffsetBytes,
        ReturnedBytes = 0,
        TotalBytes = read?.TotalLength,
        NextOffsetBytes = null,
        Sha256 = read?.Sha256,
        ContentType = read?.ContentType,
        IntegrityVerified = false,
        IsRetryable = availability == AgentRunEventDataReadAvailability.BackendUnavailable,
        ProblemCode = availability.ToString(),
    };

    internal static AgentRunEventDataReadAvailability Availability(ArtifactRangeReadState state) => state switch
    {
        ArtifactRangeReadState.MetadataMissing => AgentRunEventDataReadAvailability.MetadataMissing,
        ArtifactRangeReadState.PhysicalObjectMissing => AgentRunEventDataReadAvailability.PhysicalObjectMissing,
        ArtifactRangeReadState.IntegrityFailure => AgentRunEventDataReadAvailability.IntegrityFailure,
        ArtifactRangeReadState.BackendUnavailable => AgentRunEventDataReadAvailability.BackendUnavailable,
        ArtifactRangeReadState.AccessDenied => AgentRunEventDataReadAvailability.AccessDenied,
        ArtifactRangeReadState.InvalidOffset => AgentRunEventDataReadAvailability.InvalidRange,
        _ => throw new InvalidOperationException($"Unknown artifact range state '{state}'."),
    };
}
