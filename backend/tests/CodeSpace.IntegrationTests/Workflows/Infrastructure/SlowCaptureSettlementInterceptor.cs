using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Holds one capture-recovery settlement of <paramref name="agentRunId"/> at its verification-progress read, which the
/// settlement makes after the clock read its retry is scheduled from and before its write. Only the recovery's own
/// options carry it, and only that run's read waits, so a neighbour's intent in the same wave settles unheld.
/// </summary>
internal sealed class SlowCaptureSettlementInterceptor(Guid agentRunId, TimeSpan hold) : DbCommandInterceptor
{
    private int _held;

    public bool Held => Volatile.Read(ref _held) == 1;

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (ReadsOwnVerificationProgress(command) && Interlocked.Exchange(ref _held, 1) == 0) await Task.Delay(hold, cancellationToken);

        return result;
    }

    private bool ReadsOwnVerificationProgress(DbCommand command) => command.CommandText.Contains("FROM agent_run_log_verification", StringComparison.Ordinal)
        && command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == agentRunId);
}
