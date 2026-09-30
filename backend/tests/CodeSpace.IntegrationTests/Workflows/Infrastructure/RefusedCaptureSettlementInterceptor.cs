using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Makes every capture-recovery settlement of <paramref name="intentId"/> fail at the database by skipping one value of the
/// <paramref name="column"/> its write binds. The write keeps every other column the settlement computed, so it fails
/// whatever outcome it carries, a retry or a terminal one. With <c>revision</c>, the value it assigns,
/// <c>agent_run_log_capture_intent_guard()</c> refuses it with its own P0001, the refusal the service meets whenever its
/// code and the database contract disagree. With <c>xmin</c>, the row version its WHERE clause expects, the write matches
/// no row, which is how a row that changed under the settlement's lock would look. A settlement releases its claim and a
/// claim takes one, so only a write that assigns recovery_owner_id NULL is touched. Only the recovery's own options carry
/// it, and only that intent's write is touched, so a neighbour's intent in the same wave settles.
/// </summary>
internal sealed partial class RefusedCaptureSettlementInterceptor(Guid intentId, string column = "revision") : DbCommandInterceptor
{
    private int _refused;

    public bool Refused => Volatile.Read(ref _refused) == 1;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (OwnSettlementValue(command) is { } forged)
        {
            forged.Value = Skipped(forged.Value);
            Volatile.Write(ref _refused, 1);
        }

        return ValueTask.FromResult(result);
    }

    /// <summary>The parameter this intent's settlement write binds to the forged column, or null for any other command.</summary>
    private DbParameter? OwnSettlementValue(DbCommand command)
    {
        if (!IntentUpdate().IsMatch(command.CommandText) || !command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == intentId)) return null;

        return Assigned(command, "recovery_owner_id") is { Value: DBNull } ? Assigned(command, column) : null;
    }

    // Each arm keeps its own type: a switch whose arms widen to one numeric type would bind the row version as a long.
    private static object Skipped(object? value) => value switch
    {
        long revision => (object)(revision + 1),
        uint xmin => (object)(xmin + 1u),
        _ => throw new InvalidOperationException($"The settlement bound {value?.GetType().Name ?? "null"} where a revision or row version was expected."),
    };

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
