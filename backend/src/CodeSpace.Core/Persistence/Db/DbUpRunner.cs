using System.Reflection;
using DbUp;
using DbUp.ScriptProviders;

namespace CodeSpace.Core.Persistence.Db;

public class DbUpRunner
{
    public static readonly string ScriptFolder = Path.Combine("Persistence", "DbUpFiles");

    private readonly string _connectionString;

    public DbUpRunner(string connectionString) { _connectionString = connectionString; }

    public void Run()
    {
        EnsureDatabase.For.PostgresqlDatabase(_connectionString);

        var assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        var embeddedResourcePrefix = ScriptFolder.Replace(Path.DirectorySeparatorChar, '.');

        var upgradeEngine = DeployChanges.To.PostgresqlDatabase(_connectionString)
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

        var result = upgradeEngine.PerformUpgrade();

        if (!result.Successful)
        {
            if (result.ErrorScript != null)
            {
                Console.WriteLine($"DbUp failed on script: {result.ErrorScript.Name}");
                Console.WriteLine(result.ErrorScript.Contents);
            }

            throw result.Error;
        }
    }
}
