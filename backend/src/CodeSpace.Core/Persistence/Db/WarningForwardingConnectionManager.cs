using System.Data;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.Postgresql;
using Npgsql;

namespace CodeSpace.Core.Persistence.Db;

/// <summary>
/// DbUp's Postgres connection manager, with the server's warnings written to the upgrade log.
///
/// <para>A migration that must not refuse — a refused migration is a deployment that does not start — reports what it
/// left for an operator with <c>RAISE WARNING</c>: 0118 names the teams it found without an Owner, 0245 the duplicate
/// addresses that kept its unique index from being created. Postgres sends a warning to the client as a notice, and
/// nothing listened for one, so it reached only the database server's own log while the startup log said "Upgrade
/// successful". Forwarded here, it is printed right under the "Executing Database Server script" line that raised
/// it.</para>
///
/// <para>Only WARNING. The lower severities are routine — every <c>IF NOT EXISTS</c> that finds its object already
/// there sends a NOTICE — and would bury the one line an operator has to act on.</para>
/// </summary>
internal sealed class WarningForwardingConnectionManager : DatabaseConnectionManager
{
    private readonly PostgresqlConnectionManager _postgres;

    public WarningForwardingConnectionManager(string connectionString) : base(log => Open(connectionString, log)) { _postgres = new PostgresqlConnectionManager(connectionString); }

    /// <summary>Statements split exactly as DbUp's own Postgres manager splits them, dollar quoting and all.</summary>
    public override IEnumerable<string> SplitScriptIntoCommands(string scriptContents) => _postgres.SplitScriptIntoCommands(scriptContents);

    /// <summary>
    /// A warning, as psql would print it, into the upgrade log. Passed as an argument rather than as the format: a
    /// message can carry braces — 0118's lists team ids as a Postgres array, <c>{…}</c> — that a format string would
    /// throw on.
    /// </summary>
    internal static void Forward(PostgresNotice notice, IUpgradeLog log)
    {
        if (notice.InvariantSeverity != "WARNING") return;

        log.LogWarning("{0}", Describe(notice));
    }

    private static string Describe(PostgresNotice notice) => notice.Hint == null ? $"WARNING: {notice.MessageText}" : $"WARNING: {notice.MessageText} HINT: {notice.Hint}";

    private static IDbConnection Open(string connectionString, IUpgradeLog log)
    {
        var connection = new NpgsqlConnection(connectionString);

        connection.Notice += (_, e) => Forward(e.Notice, log);

        return connection;
    }
}
