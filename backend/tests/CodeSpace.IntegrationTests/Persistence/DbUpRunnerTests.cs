using CodeSpace.Core.Persistence.Db;
using CodeSpace.IntegrationTests.Infrastructure;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DbUpRunnerTests
{
    private readonly PostgresFixture _fixture;

    public DbUpRunnerTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public void Migration_is_idempotent_when_run_again()
    {
        var act = () => new DbUpRunner(_fixture.ConnectionString).Run();

        act.ShouldNotThrow();
    }

    [Theory]
    [InlineData("app_user")]
    [InlineData("team")]
    [InlineData("team_membership")]
    [InlineData("provider_instance")]
    [InlineData("credential")]
    [InlineData("repository")]
    [InlineData("repository_webhook")]
    [InlineData("conversation")]
    [InlineData("conversation_member")]
    [InlineData("message")]
    [InlineData("message_reference")]
    public async Task Table_exists_after_migration(string tableName)
    {
        var exists = await TableExistsAsync(tableName).ConfigureAwait(false);

        exists.ShouldBeTrue($"Table '{tableName}' should exist after migration");
    }

    [Theory]
    [InlineData("credential", "ownership")]   // added by 0030 — drives team-service vs personal credential governance
    public async Task Column_exists_after_migration(string tableName, string columnName)
    {
        var exists = await ColumnExistsAsync(tableName, columnName).ConfigureAwait(false);

        exists.ShouldBeTrue(
            $"Column '{tableName}.{columnName}' must exist after migrations apply — added by 0030_credential_ownership.sql. " +
            $"If missing, 0030 did not run. Diagnose with: psql -c '\\d {tableName}' against the test database.");
    }

    [Theory]
    [InlineData("repository", "project_id")]   // dropped by 0027 — see PR notes; the link table is now the sole project membership source
    public async Task Column_does_not_exist_after_migration(string tableName, string columnName)
    {
        var exists = await ColumnExistsAsync(tableName, columnName).ConfigureAwait(false);

        exists.ShouldBeFalse(
            $"Column '{tableName}.{columnName}' must NOT exist after migrations apply — it was dropped by 0027_drop_repository_project_id.sql. " +
            $"If present, either 0027 did not run, or a later migration re-added the column (unintentional regression). " +
            $"Diagnose with: psql -c '\\d {tableName}' against the test database.");
    }

    private async Task<bool> TableExistsAsync(string tableName)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @t)",
            conn);
        cmd.Parameters.AddWithValue("@t", tableName);

        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return (bool)result!;
    }

    private async Task<bool> ColumnExistsAsync(string tableName, string columnName)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t AND column_name = @c)",
            conn);
        cmd.Parameters.AddWithValue("@t", tableName);
        cmd.Parameters.AddWithValue("@c", columnName);

        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return (bool)result!;
    }
}
