using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Makes <c>agent_run_log_capture_intent_guard()</c> refuse every capture-recovery settlement of <paramref name="intentId"/>
/// with its own P0001, the refusal the service meets whenever its code and the database contract disagree. The write
/// keeps every column the settlement computed except its revision, which skips one, so the guard refuses it whatever
/// outcome it carries, a retry or a terminal one. A settlement releases its claim and a claim takes one, so only a write
/// that assigns recovery_owner_id NULL is touched. Only the recovery's own options carry it, and only that intent's write
/// is touched, so a neighbour's intent in the same wave settles.
/// </summary>
internal sealed partial class RefusedCaptureSettlementInterceptor(Guid intentId) : DbCommandInterceptor
{
    private int _refused;

    public bool Refused => Volatile.Read(ref _refused) == 1;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (OwnSettlementRevision(command) is { } revision)
        {
            revision.Value = (long)revision.Value! + 1;
            Volatile.Write(ref _refused, 1);
        }

        return ValueTask.FromResult(result);
    }

    /// <summary>The revision parameter of this intent's settlement write, or null for any other command.</summary>
    private DbParameter? OwnSettlementRevision(DbCommand command)
    {
        if (!IntentUpdate().IsMatch(command.CommandText) || !command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == intentId)) return null;

        return Assigned(command, "recovery_owner_id") is { Value: DBNull } ? Assigned(command, "revision") : null;
    }

    private static DbParameter? Assigned(DbCommand command, string column)
    {
        var assignment = Assignment().Matches(command.CommandText).FirstOrDefault(match => match.Groups["column"].Value == column);

        return assignment == null ? null : command.Parameters.Cast<DbParameter>().Single(parameter => parameter.ParameterName.TrimStart('@') == assignment.Groups["name"].Value);
    }

    [GeneratedRegex(@"UPDATE ""?agent_run_log_capture_intent""? SET ", RegexOptions.CultureInvariant)]
    private static partial Regex IntentUpdate();

    [GeneratedRegex(@"""?(?<column>\w+)""? = @(?<name>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();
}
