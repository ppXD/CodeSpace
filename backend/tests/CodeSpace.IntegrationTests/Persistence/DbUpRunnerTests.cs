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
    public async Task Table_exists_after_migration(string tableName)
    {
        var exists = await TableExistsAsync(tableName).ConfigureAwait(false);

        exists.ShouldBeTrue($"Table '{tableName}' should exist after migration");
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
}
