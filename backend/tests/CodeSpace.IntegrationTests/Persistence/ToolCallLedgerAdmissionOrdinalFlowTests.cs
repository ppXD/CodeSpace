using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres source-admission pins for the governed tool-call ledger. A later observation-only projection needs
/// an immutable, one-based order that is true after COMMIT for one AgentRun; CreatedDate and random ids cannot supply
/// that fact. Legacy rows deliberately remain NULL and therefore ineligible for projection.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ToolCallLedgerAdmissionOrdinalFlowTests
{
    private const string InputHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly PostgresFixture _fixture;

    public ToolCallLedgerAdmissionOrdinalFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Ef_insert_gets_a_one_based_per_agent_ordinal_and_cannot_forge_it()
    {
        var teamId = await SeedTeamAsync();
        var agentRunId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var first = Row(teamId, agentRunId, "git.open_pr");
        db.ToolCallLedger.Add(first);
        await db.SaveChangesAsync();

        var second = Row(teamId, agentRunId, "git.merge_pr");
        second.AdmissionOrdinal = 9001;
        db.ToolCallLedger.Add(second);
        await db.SaveChangesAsync();

        first.AdmissionOrdinal.ShouldBe(1L);
        second.AdmissionOrdinal.ShouldBe(2L, "the database owns admission; a caller-supplied rank is ignored");
    }

    [Fact]
    public async Task Same_agent_insert_waits_for_the_prior_transaction_and_commit_yields_ordinals_one_then_two()
    {
        var teamId = await SeedTeamAsync();
        var agentRunId = Guid.NewGuid();
        await using var firstConnection = await OpenAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        var firstAdmission = await InsertAsync(firstConnection, firstTransaction, teamId, agentRunId, "git.open_pr");

        var second = InsertInOwnTransactionAsync(teamId, agentRunId, "git.merge_pr");
        (await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(500)))).ShouldNotBe(second,
            "the second real INSERT must wait until the earlier allocator commits; allocating before commit permits a late lower rank");

        await firstTransaction.CommitAsync();
        var secondOrdinal = await second.WaitAsync(TimeSpan.FromSeconds(10));

        firstAdmission.Ordinal.ShouldBe(1L);
        secondOrdinal.ShouldBe(2L);
    }

    [Fact]
    public async Task Same_agent_insert_waits_for_the_prior_transaction_and_rollback_reuses_one_without_a_gap()
    {
        var teamId = await SeedTeamAsync();
        var agentRunId = Guid.NewGuid();
        await using var abandonedConnection = await OpenAsync();
        await using var abandonedTransaction = await abandonedConnection.BeginTransactionAsync();
        (await InsertAsync(abandonedConnection, abandonedTransaction, teamId, agentRunId, "git.open_pr")).Ordinal.ShouldBe(1L);

        var committed = InsertInOwnTransactionAsync(teamId, agentRunId, "git.merge_pr");
        (await Task.WhenAny(committed, Task.Delay(TimeSpan.FromMilliseconds(500)))).ShouldNotBe(committed,
            "the contender must not allocate around an uncommitted predecessor");

        await abandonedTransaction.RollbackAsync();

        (await committed.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(1L,
            "a rolled-back admission never existed, so the first committed call stays one-based and contiguous");
    }

    [Fact]
    public async Task Different_agents_do_not_share_the_admission_lock()
    {
        var teamId = await SeedTeamAsync();
        await using var firstConnection = await OpenAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        (await InsertAsync(firstConnection, firstTransaction, teamId, Guid.NewGuid(), "git.open_pr")).Ordinal.ShouldBe(1L);

        var unrelated = InsertInOwnTransactionAsync(teamId, Guid.NewGuid(), "git.merge_pr");

        (await unrelated.WaitAsync(TimeSpan.FromSeconds(3))).ShouldBe(1L,
            "the lock key is scoped to AgentRun; an unrelated execution must remain independently writable");
        await firstTransaction.RollbackAsync();
    }

    [Fact]
    public async Task Ordinal_and_scope_identity_are_immutable_and_one_agent_cannot_cross_teams()
    {
        var teamId = await SeedTeamAsync();
        var foreignTeamId = await SeedTeamAsync();
        var agentRunId = Guid.NewGuid();
        await using var connection = await OpenAsync();
        RawAdmission admission;
        await using (var insert = await connection.BeginTransactionAsync())
        {
            admission = await InsertAsync(connection, insert, teamId, agentRunId, "git.open_pr");
            admission.Ordinal.ShouldBe(1L);
            await insert.CommitAsync();
        }

        await ExecuteRejectedAsync(connection,
            "UPDATE tool_call_ledger SET admission_ordinal = 2 WHERE id = @id", admission.LedgerId,
            "the source rank must never move after admission");
        await ExecuteRejectedAsync(connection,
            "UPDATE tool_call_ledger SET agent_run_id = @replacement WHERE id = @id", admission.LedgerId,
            "moving a ranked source to another AgentRun changes its meaning");
        await InsertRejectedAsync(connection, foreignTeamId, agentRunId,
            "the same AgentRun cannot acquire a second team identity");
    }

    [Fact]
    public async Task Column_is_nullable_without_a_default_and_the_migration_never_backfills_legacy_rows()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'tool_call_ledger'
              AND column_name = 'admission_ordinal'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetString(0).ShouldBe("YES");
        reader.IsDBNull(1).ShouldBeTrue("the trigger admits new rows; NULL remains representable for honest legacy history");

        var migrationPath = Path.Combine(FindBackendRoot(), "src", "CodeSpace.Core", "Persistence", "DbUpFiles", "0156_tool_call_ledger_admission_ordinal.sql");
        var migration = await File.ReadAllTextAsync(migrationPath);
        migration.ShouldNotContain("UPDATE tool_call_ledger SET admission_ordinal");
    }

    [Fact]
    public async Task Partial_unique_index_is_shaped_for_the_per_agent_backward_lookup()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'tool_call_ledger'
              AND indexname = 'ux_tool_call_ledger_run_admission_ordinal'
            """, connection);

        var indexDefinition = (string?)(await command.ExecuteScalarAsync());

        indexDefinition.ShouldNotBeNull();
        indexDefinition.ShouldContain("UNIQUE INDEX");
        indexDefinition.ShouldContain("(agent_run_id, admission_ordinal)");
        indexDefinition.ShouldContain("WHERE (admission_ordinal IS NOT NULL)");
    }

    private async Task<long> InsertInOwnTransactionAsync(Guid teamId, Guid agentRunId, string toolKind)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var admission = await InsertAsync(connection, transaction, teamId, agentRunId, toolKind);
        await transaction.CommitAsync();
        return admission.Ordinal;
    }

    private static async Task<RawAdmission> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid teamId, Guid agentRunId, string toolKind)
    {
        var ledgerId = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            INSERT INTO tool_call_ledger (
                id, team_id, agent_run_id, tool_kind, idempotency_key, input_hash, status,
                fence_epoch, created_by, last_modified_by, admission_ordinal)
            VALUES (
                @id, @team, @run, @kind, @key, @hash, 'Pending',
                0, @actor, @actor, 9001)
            RETURNING admission_ordinal
            """, connection, transaction);
        command.Parameters.AddWithValue("id", ledgerId);
        command.Parameters.AddWithValue("team", teamId);
        command.Parameters.AddWithValue("run", agentRunId);
        command.Parameters.AddWithValue("kind", toolKind);
        command.Parameters.AddWithValue("key", $"{toolKind}:{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("hash", InputHash);
        command.Parameters.AddWithValue("actor", SystemUsers.SeederId);
        return new RawAdmission(ledgerId, (long)(await command.ExecuteScalarAsync())!);
    }

    private static async Task ExecuteRejectedAsync(NpgsqlConnection connection, string sql, Guid ledgerId, string reason)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", ledgerId);
        command.Parameters.AddWithValue("replacement", Guid.NewGuid());
        await command.ExecuteNonQueryAsync().ShouldThrowAsync<PostgresException>(reason);
        await transaction.RollbackAsync();
    }

    private static async Task InsertRejectedAsync(NpgsqlConnection connection, Guid teamId, Guid agentRunId, string reason)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertAsync(connection, transaction, teamId, agentRunId, "git.create_issue").ShouldThrowAsync<PostgresException>(reason);
        await transaction.RollbackAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"ordinal-{teamId:N}", Name = "Ordinal Team", Kind = TeamKind.Workspace });
        await db.SaveChangesAsync();
        return teamId;
    }

    private static ToolCallLedger Row(Guid teamId, Guid agentRunId, string toolKind) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = teamId,
        AgentRunId = agentRunId,
        ToolKind = toolKind,
        IdempotencyKey = $"{toolKind}:{Guid.NewGuid():N}",
        InputHash = InputHash,
        Status = ToolCallLedgerStatus.Pending,
        CreatedBy = SystemUsers.SeederId,
        LastModifiedBy = SystemUsers.SeederId,
    };

    private static string FindBackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeSpace.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the CodeSpace backend root.");
    }

    private sealed record RawAdmission(Guid LedgerId, long Ordinal);
}
