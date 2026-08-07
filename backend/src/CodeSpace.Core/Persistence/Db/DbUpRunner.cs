using System.Reflection;
using DbUp;
using DbUp.Engine;
using DbUp.ScriptProviders;
using Npgsql;

namespace CodeSpace.Core.Persistence.Db;

public class DbUpRunner
{
    public static readonly string ScriptFolder = Path.Combine("Persistence", "DbUpFiles");

    /// <summary>
    /// The advisory-lock key the migration gate holds. Every process that migrates this database MUST use this exact
    /// number — that is the whole mechanism, so it is pinned by a test: changing it silently un-serialises migration
    /// against any pod still running the old value, which is the failure the lock exists to prevent.
    ///
    /// <para>An arbitrary but fixed constant. Postgres advisory locks live in one global 64-bit namespace shared with
    /// anything else in the database, so it is deliberately not a small round number that another tool might pick.</para>
    /// </summary>
    public const long MigrationAdvisoryLockKey = 6_183_402_115_774_301L;

    private readonly string _connectionString;

    public DbUpRunner(string connectionString) { _connectionString = connectionString; }

    /// <summary>
    /// Apply every pending migration, serialised across processes by a session-level advisory lock.
    ///
    /// <para>The API and worker pods run the SAME assembly and both migrate at startup, so a rolling deploy routinely
    /// starts them together. DbUp's Postgres provider takes no lock of its own: two engines would read the journal
    /// concurrently, both decide the same script is pending, and both run it. With <c>WithTransaction</c> that
    /// surfaces as one pod dying on a duplicate-object error at best, and as a half-applied schema at worst.</para>
    ///
    /// <para><c>pg_advisory_lock</c> BLOCKS rather than failing, which is the behaviour we want: the second pod waits
    /// out the first pod's migration, then re-reads the journal, finds nothing pending, and starts. The lock is
    /// session-scoped and held on its own connection, so it covers the whole upgrade and is released by the server
    /// even if this process is killed mid-migration.</para>
    /// </summary>
    public void Run()
    {
        EnsureDatabase.For.PostgresqlDatabase(_connectionString);

        using var gate = OpenGate();

        Acquire(gate);

        try
        {
            Apply(BuildEngine().PerformUpgrade());
        }
        finally
        {
            Release(gate);
        }
    }

    private NpgsqlConnection OpenGate()
    {
        var gate = new NpgsqlConnection(_connectionString);
        gate.Open();
        return gate;
    }

    /// <summary>Blocks until this session owns the migration lock — a concurrently-starting pod waits here, it does not fail.</summary>
    private static void Acquire(NpgsqlConnection gate) => Execute(gate, "SELECT pg_advisory_lock(@key)");

    /// <summary>Best-effort release. A lost connection already released it server-side, so a failure here must never mask the migration's own outcome.</summary>
    private static void Release(NpgsqlConnection gate)
    {
        try { Execute(gate, "SELECT pg_advisory_unlock(@key)"); }
        catch (NpgsqlException) { /* the session is gone; the server released the lock with it */ }
    }

    private static void Execute(NpgsqlConnection gate, string sql)
    {
        using var command = new NpgsqlCommand(sql, gate);
        command.Parameters.AddWithValue("key", MigrationAdvisoryLockKey);
        command.ExecuteNonQuery();
    }

    private UpgradeEngine BuildEngine()
    {
        var assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        var embeddedResourcePrefix = ScriptFolder.Replace(Path.DirectorySeparatorChar, '.');

        return DeployChanges.To.PostgresqlDatabase(_connectionString)
            .WithScriptsFromFileSystem(
                Path.Combine(assemblyLocation, ScriptFolder),
                new FileSystemScriptOptions { IncludeSubDirectories = true })
            .WithScriptsAndCodeEmbeddedInAssembly(
                typeof(DbUpRunner).Assembly,
                s => s.StartsWith($"{typeof(DbUpRunner).Assembly.GetName().Name}.{embeddedResourcePrefix}"))
            // Disable $variable$ substitution — our PBKDF2 hash format uses '$' as a
            // section separator (pbkdf2$sha256$iter$salt$digest) and DbUp would otherwise
            // try to expand "$sha256$" as a variable lookup and abort.
            .WithVariablesDisabled()
            .WithTransaction()
            .LogToConsole()
            .Build();
    }

    private static void Apply(DatabaseUpgradeResult result)
    {
        if (result.Successful) return;

        if (result.ErrorScript != null)
        {
            Console.WriteLine($"DbUp failed on script: {result.ErrorScript.Name}");
            Console.WriteLine(result.ErrorScript.Contents);
        }

        throw result.Error;
    }
}
