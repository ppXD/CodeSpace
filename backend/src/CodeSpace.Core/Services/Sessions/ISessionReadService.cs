using CodeSpace.Messages.Dtos.Sessions;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// The READ side of the work-session layer — the team's sessions index + one thread as a conversation. Separate from
/// <see cref="IWorkSessionService"/> (the lifecycle writer: open / continue) so each owns one responsibility (Rule 16):
/// this service owns the read projections. Every method is team-scoped; a foreign / missing target is an
/// indistinguishable not-found (null), never a leak.
/// </summary>
public interface ISessionReadService
{
    /// <summary>The team's sessions, most-recently-active first, keyset-paginated by an opaque <paramref name="cursor"/>.</summary>
    Task<SessionPage> ListAsync(Guid teamId, string? cursor, int limit, CancellationToken cancellationToken);

    /// <summary>One thread as a conversation (its turns + nested attempts), or null if missing / not this team's.</summary>
    Task<SessionDetail?> GetDetailAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// The thread a run belongs to, anchored at that run's turn. Any run in the thread (a top-level turn or one of its
    /// rerun attempts) resolves to the same thread; a session-less / foreign / missing run is not-found (null).
    /// </summary>
    Task<SessionDetail?> GetByRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);
}
