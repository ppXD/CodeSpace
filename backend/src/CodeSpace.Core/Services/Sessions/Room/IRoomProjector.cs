using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Sessions.Room;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// Projects a work session into the backend-authored <see cref="RoomView"/> — the AI work transcript. The focused turn
/// is fully rendered (execution map + narrative + agents + decisions + actions); the other turns are light cards (the
/// frontend re-focuses one by navigating to its run, which is cheap). Tenancy: a foreign / absent run or session reads
/// as not-found (null) — never a leak. READ-ONLY.
/// </summary>
public interface IRoomProjector : IScopedDependency
{
    /// <summary>The room for the session a run belongs to, focused on that run's turn. Null when the run is foreign / absent / session-less.</summary>
    Task<RoomView?> ProjectByRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>The room for a session, focused on <paramref name="focusRunId"/>'s turn when given (else the latest turn). Null when the session is foreign / absent.</summary>
    Task<RoomView?> ProjectAsync(Guid sessionId, Guid? focusRunId, Guid teamId, CancellationToken cancellationToken);
}
