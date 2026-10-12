using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Artifacts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>
/// Where a unique secret canary landed at rest: every row of EVERY base table whose text form carries it (so every
/// text / jsonb / bytea column — a bytea renders as hex, which is searched too), plus the stored bytes of every
/// artifact the team owns. A test asserts the list is empty, so a new table, column or offload path that freezes a
/// resolved secret reds here without the test having to know it exists. Encrypted columns hold ciphertext, never the
/// canary's bytes, so they never match.
/// </summary>
public static class SecretCanarySweep
{
    public static async Task<IReadOnlyList<string>> FindAsync(PostgresFixture fixture, Guid teamId, string canary)
    {
        using var scope = fixture.BeginScope();
        return await FindAsync(fixture.ConnectionString, scope.Resolve<IArtifactStore>(), scope.Resolve<CodeSpaceDbContext>(), teamId, canary);
    }

    /// <summary>The same sweep against any host's database — an API host under test hands its own connection string and services.</summary>
    public static async Task<IReadOnlyList<string>> FindAsync(string connectionString, IArtifactStore store, CodeSpaceDbContext db, Guid teamId, string canary)
    {
        var hits = new List<string>();

        hits.AddRange(await FindInTablesAsync(connectionString, canary));
        hits.AddRange(await FindInArtifactsAsync(store, db, teamId, canary));

        return hits;
    }

    private static async Task<IReadOnlyList<string>> FindInTablesAsync(string connectionString, string canary)
    {
        var hits = new List<string>();
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(canary)).ToLowerInvariant();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var table in await BaseTablesAsync(connection))
        {
            await using var command = new NpgsqlCommand($"SELECT x::text FROM {table} x WHERE x::text LIKE @plain OR x::text LIKE @hex LIMIT 5", connection);
            command.Parameters.AddWithValue("plain", "%" + canary + "%");
            command.Parameters.AddWithValue("hex", "%" + hex + "%");

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) hits.Add($"{table}: {Around(reader.GetString(0), canary, hex)}");
        }

        return hits;
    }

    private static async Task<IReadOnlyList<string>> BaseTablesAsync(NpgsqlConnection connection)
    {
        var tables = new List<string>();

        await using var command = new NpgsqlCommand("SELECT quote_ident(table_schema) || '.' || quote_ident(table_name) FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema')", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        return tables;
    }

    private static async Task<IReadOnlyList<string>> FindInArtifactsAsync(IArtifactStore store, CodeSpaceDbContext db, Guid teamId, string canary)
    {
        var artifactIds = await db.WorkflowArtifact.AsNoTracking().Where(artifact => artifact.TeamId == teamId).Select(artifact => artifact.Id).ToListAsync();
        var hits = new List<string>();

        foreach (var artifactId in artifactIds)
        {
            var bytes = (await store.GetBytesAsync(teamId, artifactId, CancellationToken.None))?.Bytes;
            if (bytes is not null && Encoding.UTF8.GetString(bytes).Contains(canary, StringComparison.Ordinal)) hits.Add($"artifact {artifactId}");
        }

        return hits;
    }

    private static string Around(string text, string canary, string hex)
    {
        var index = text.IndexOf(canary, StringComparison.Ordinal);
        if (index < 0) index = text.IndexOf(hex, StringComparison.Ordinal);

        return "…" + text[Math.Max(0, index - 160)..Math.Min(text.Length, index + canary.Length + 40)] + "…";
    }
}
