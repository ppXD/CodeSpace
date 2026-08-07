using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.OAuth;

/// <summary>
/// Sweep every expired <c>oauth_pending_state</c> row. The exchange path already deletes a row on success, so this
/// only reclaims the flows a user abandoned (closed the tab between init and callback). Dispatched by the recurring
/// janitor; can also be sent ad-hoc (admin path / tests).
///
/// <para>NOT tenant-scoped — a system-wide internal sweep with no actor context. The delete is a plain expiry
/// predicate, so running it on several pods only ever means one of them wins each row.</para>
/// </summary>
public sealed record CleanupExpiredOAuthStatesCommand : ICommand<CleanupExpiredOAuthStatesResponse>;

/// <summary>Count of expired rows this sweep removed — surfaced in the job log so an operator can see the janitor working.</summary>
public sealed record CleanupExpiredOAuthStatesResponse
{
    public required int Deleted { get; init; }
}
