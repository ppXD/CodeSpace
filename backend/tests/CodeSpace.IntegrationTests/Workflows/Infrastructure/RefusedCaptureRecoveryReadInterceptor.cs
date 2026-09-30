using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Makes the database refuse every capture-session read the capture recovery step makes for <paramref name="teamId"/>. The
/// server raises the P0001, so the service meets the PostgresException a failing read would hand it, not one built here.
/// Only the recovery step reads <c>agent_run_log_capture_session</c> through the recovery's own options, so the claim
/// and the settlement around it are untouched. Every test run has its own team and only that team's read is refused, so a
/// neighbour's intent in the same wave recovers.
/// </summary>
internal sealed class RefusedCaptureRecoveryReadInterceptor(Guid teamId) : DbCommandInterceptor
{
    private int _refused;

    public bool Refused => Volatile.Read(ref _refused) == 1;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (!ReadsOwnCaptureSession(command)) return ValueTask.FromResult(result);

        command.Parameters.Clear();
        command.CommandText = $"DO $$ BEGIN RAISE EXCEPTION 'capture-session read refused for team {teamId}'; END $$";
        Volatile.Write(ref _refused, 1);

        return ValueTask.FromResult(result);
    }

    private bool ReadsOwnCaptureSession(DbCommand command) => command.CommandText.Contains("FROM agent_run_log_capture_session", StringComparison.Ordinal)
        && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == teamId);
}
