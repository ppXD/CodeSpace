using System.Security.Claims;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Auth;
using CodeSpace.Core.Services.Tasks.Trace;
using CodeSpace.Core.Services.Users;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Queries.Tasks;
using CodeSpace.Messages.Tasks.Trace;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Proves the live ledger tail (the Room SSE relay's source): <see cref="IRunRecordStreamer"/> yields every
/// workflow_run_record row beyond a cursor in Sequence order and STOPS at a terminal run record; it honors the cursor
/// (exclusive — a resuming client never re-receives a row); and it is team-scoped — a foreign team tails nothing.
/// Read-only against the REAL ledger + real team boundary, so a tenancy or ordering regression fails here.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RunRecordStreamerFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunRecordStreamerFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Tails_records_after_the_cursor_in_sequence_order_and_stops_at_a_terminal_run_record()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        var correlationId = Guid.NewGuid();
        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            await logger.RunStartedAsync(runId, CancellationToken.None);
            await logger.RecordInteractionAsync(runId, WorkflowRunRecordTypes.InteractionStarted, "llm1", "llm1#0", correlationId, null, Payload(new { kind = "llm.complete" }), CancellationToken.None);
            await logger.RecordInteractionAsync(runId, WorkflowRunRecordTypes.InteractionDelta, "llm1", "llm1#0", correlationId, null, Payload(new { ordinal = 0, text = "hello there" }), CancellationToken.None);
            await logger.RecordInteractionAsync(runId, WorkflowRunRecordTypes.InteractionCompleted, "llm1", "llm1#0", correlationId, null, Payload(new { output = "hello there" }), CancellationToken.None);
            await logger.RunCompletedAsync(runId, TimeSpan.FromSeconds(1), outputsPresent: true, CancellationToken.None);
        }

        var records = await TailAsync(teamId, userId, runId, after: 0);

        records.Select(r => r.Sequence).ShouldBeInOrder(SortDirection.Ascending, "the tail yields rows in ledger Sequence order");
        records[^1].RecordType.ShouldBe(WorkflowRunRecordTypes.RunCompleted, "the tail STOPS at the terminal run record — it does not hang");
        records.ShouldContain(r => r.RecordType == WorkflowRunRecordTypes.InteractionDelta, "the interaction.delta row is streamed");

        var delta = records.First(r => r.RecordType == WorkflowRunRecordTypes.InteractionDelta);
        JsonDocument.Parse(delta.PayloadJson).RootElement.GetProperty("ordinal").GetInt32().ShouldBe(0);
        delta.CorrelationId.ShouldBe(correlationId, "the streamed delta carries its correlation id for the consumer to group by");
    }

    [Fact]
    public async Task Yields_only_records_strictly_after_the_given_cursor()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            await logger.RunStartedAsync(runId, CancellationToken.None);
            await logger.RecordInteractionAsync(runId, WorkflowRunRecordTypes.InteractionDelta, "llm1", "llm1#0", Guid.NewGuid(), null, Payload(new { ordinal = 0 }), CancellationToken.None);
            await logger.RunCompletedAsync(runId, TimeSpan.FromSeconds(1), true, CancellationToken.None);
        }

        var all = await TailAsync(teamId, userId, runId, after: 0);
        var midCursor = all[all.Count / 2].Sequence;

        var after = await TailAsync(teamId, userId, runId, after: midCursor);

        after.ShouldAllBe(r => r.Sequence > midCursor, "the cursor is EXCLUSIVE — a resuming client never re-receives a row it already saw");
    }

    [Fact]
    public async Task Run_scoped_sequence_is_assigned_after_the_commit_gate_so_a_late_transaction_cannot_land_behind_the_live_cursor()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var baseline = await ReadRunCursorAsync(runId);

        await using var firstConnection = new NpgsqlConnection(_fixture.ConnectionString);
        await firstConnection.OpenAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        var firstSequence = await InsertRecordAsync(firstConnection, firstTransaction, runId, "test.first-transaction");

        var applicationName = $"run-record-cursor-{Guid.NewGuid():N}";
        var secondInsert = InsertRecordAndCommitAsync(runId, WorkflowRunRecordTypes.RunCompleted, applicationName);
        var secondWaitsForFirstCommit = await WaitForLockAsync(applicationName, secondInsert);
        if (!secondWaitsForFirstCommit)
        {
            await firstTransaction.RollbackAsync();
            await secondInsert;
        }

        secondWaitsForFirstCommit.ShouldBeTrue(
            "the production BEFORE INSERT trigger must retain the run gate from sequence assignment through transaction commit");

        await firstTransaction.CommitAsync();
        var secondSequence = await secondInsert.WaitAsync(TimeSpan.FromSeconds(10));
        firstSequence.ShouldBeLessThan(secondSequence);

        var streamed = await TailAsync(teamId, userId, runId, baseline);
        streamed.Select(x => x.Sequence).ShouldBe(new[] { firstSequence, secondSequence });
        streamed.Select(x => x.RecordType).ShouldBe(new[] { "test.first-transaction", WorkflowRunRecordTypes.RunCompleted });
    }

    [Fact]
    public async Task Sequence_is_database_owned_even_when_an_EF_caller_supplies_a_value()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var record = new WorkflowRunRecord
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            Sequence = long.MaxValue,
            RecordType = "test.explicit-sequence",
            IterationKey = string.Empty,
            PayloadJson = "{}",
        };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunRecord.Add(record);
        await db.SaveChangesAsync();

        record.Sequence.ShouldNotBe(long.MaxValue, "the tracked entity must receive the trigger-assigned cursor rather than retaining a caller-supplied value");
        record.Sequence.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_foreign_team_tails_nothing()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamA, userA);

        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            await logger.RunStartedAsync(runId, CancellationToken.None);
            await logger.RunCompletedAsync(runId, TimeSpan.FromSeconds(1), true, CancellationToken.None);
        }

        var (teamB, userB) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var records = await TailAsync(teamB, userB, runId, after: 0);

        records.ShouldBeEmpty("a foreign run yields nothing — the run precheck IS the tenancy boundary");
    }

    /// <summary>
    /// The run precheck compares the run's team to <c>X-Team-Id</c> and nothing else, so it cannot be the tenancy
    /// boundary on its own: a caller who merely SENDS the run's team id passes it. The stream is therefore reached only
    /// through <see cref="StreamRunRecordsQuery"/>, whose membership marker refuses a non-member at dispatch — before
    /// the tail is ever enumerated, which is what lets the HTTP layer answer 403 instead of an opened, empty stream.
    /// </summary>
    [Theory]
    [InlineData(true)]    // a member of the run's team → the tail yields the ledger
    [InlineData(false)]   // a stranger naming the run's team in the header → refused at dispatch
    public async Task The_stream_query_is_refused_at_dispatch_unless_the_caller_is_a_member_of_the_header_team(bool isMember)
    {
        var (teamA, ownerA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamA, ownerA);

        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            await logger.RunStartedAsync(runId, CancellationToken.None);
            await logger.RunCompletedAsync(runId, TimeSpan.FromSeconds(1), true, CancellationToken.None);
        }

        var callerId = isMember ? ownerA : (await WorkflowsTestSeed.SeedTeamAsync(_fixture)).UserId;
        PresentSession(callerId, await ReadSecurityStampAsync(callerId));

        using var callerScope = _fixture.BeginScopeAs(callerId, teamA);   // no global role: the membership row is the whole answer
        var mediator = callerScope.Resolve<MediatR.IMediator>();
        var query = new StreamRunRecordsQuery { RunId = runId, AfterSequence = 0 };

        if (!isMember)
        {
            await Should.ThrowAsync<TenantAccessDeniedException>(() => mediator.Send(query), "a non-member is refused when the stream is requested, not after it opens");
            return;
        }

        var tail = await mediator.Send(query);
        var records = new List<RunRecordView>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));   // safety net — a terminal run ends the tail itself
        await foreach (var record in tail.WithCancellation(cts.Token)) records.Add(record);

        records.ShouldContain(r => r.RecordType == WorkflowRunRecordTypes.RunStarted, "the member receives the run's ledger");
        records[^1].RecordType.ShouldBe(WorkflowRunRecordTypes.RunCompleted, "and the tail ends at the terminal record");
    }

    /// <summary>
    /// The dispatch-time check above runs once, when the tail is requested, and a run waiting on a decision can stay
    /// non-terminal for days. So the tail re-asks, from a fresh scope on every poll that has rows to hand out, whether
    /// the caller still holds a membership row on a live team under a live session, and ends WITHOUT those rows once any
    /// of them lapses. Driven under the session the bearer handler leaves on the request: the per-poll scope is a child of
    /// the root, as in production, so it knows the caller only from that principal and reads their roles from the
    /// database, which is why the global-admin rows grant Admin as a role row rather than through a test identity. The
    /// Kept row proves the later record and the terminal one still arrive for a caller whose standing holds, so their
    /// absence elsewhere is the re-check. The global-admin rows hold the re-check to the bypass the connect-time check
    /// grants: an Admin needs no membership row, but still needs a live session under a token still accepted.
    /// </summary>
    [Theory]
    [InlineData(StandingChange.Kept, false, true)]
    [InlineData(StandingChange.MembershipRemoved, false, false)]
    [InlineData(StandingChange.TeamDeleted, false, false)]
    [InlineData(StandingChange.SessionSuperseded, false, false)]
    [InlineData(StandingChange.AccountDeactivated, false, false)]
    [InlineData(StandingChange.TokenExpired, false, false)]
    [InlineData(StandingChange.MembershipRemoved, true, true)]
    [InlineData(StandingChange.SessionSuperseded, true, false)]
    [InlineData(StandingChange.TokenExpired, true, false)]
    public async Task An_open_tail_yields_nothing_written_after_the_caller_loses_standing(StandingChange change, bool globalAdmin, bool stillDelivered)
    {
        var (teamId, memberId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, memberId);
        var cursor = await ReadRunCursorAsync(runId);   // past whatever seeding wrote, so the first record read is ours
        if (globalAdmin) await GrantGlobalAdminAsync(memberId);
        await InsertRecordAndCommitAsync(runId, WorkflowRunRecordTypes.RunStarted, "standing-before");

        PresentSession(memberId, await ReadSecurityStampAsync(memberId), change == StandingChange.TokenExpired ? ShortLivedTokenExpiry() : null);

        using var scope = _fixture.BeginScopeAs(memberId, teamId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));   // safety net — the terminal record or the re-check ends the tail
        await using var tail = scope.Resolve<IRunRecordStreamer>().TailAsync(runId, cursor, cts.Token).GetAsyncEnumerator(cts.Token);

        (await tail.MoveNextAsync()).ShouldBeTrue($"{change}: the caller tails the run while still holding standing");
        tail.Current.RecordType.ShouldBe(WorkflowRunRecordTypes.RunStarted);

        await ChangeStandingAsync(change, memberId, teamId);
        await InsertRecordAndCommitAsync(runId, LaterRecordType, "standing-after");
        await InsertRecordAndCommitAsync(runId, WorkflowRunRecordTypes.RunCompleted, "standing-terminal");

        var remainder = await DrainAsync(tail, change);

        remainder.Any(r => r.RecordType == LaterRecordType).ShouldBe(stillDelivered, $"{change} (admin: {globalAdmin}): a record written after the change is yielded only while standing holds");
        remainder.Any(r => r.RecordType == WorkflowRunRecordTypes.RunCompleted).ShouldBe(stillDelivered, $"{change} (admin: {globalAdmin}): once standing is gone the tail ends by the re-check, before the terminal record");
    }

    /// <summary>
    /// A token that ages out is the one way a session ends with nobody acting, so no write ever follows it to wake a
    /// re-check. An idle tail must therefore end on its own once the bearer handler stops accepting the caller's token,
    /// before anything else is written. The control is the same idle tail under a token still accepted: it stays open
    /// over the same wait, and the next record written reaches it, so the expiring row's early end is the expiry.
    /// </summary>
    [Theory]
    [InlineData(true)]    // the token ages out while nothing is written → the tail ends on its own, with nothing more
    [InlineData(false)]   // a token still accepted → the same idle tail stays open and the next record arrives
    public async Task An_idle_tail_ends_once_the_callers_token_is_no_longer_accepted(bool tokenExpires)
    {
        var (teamId, memberId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, memberId);
        var cursor = await ReadRunCursorAsync(runId);
        await InsertRecordAndCommitAsync(runId, WorkflowRunRecordTypes.RunStarted, "idle-before");

        PresentSession(memberId, await ReadSecurityStampAsync(memberId), tokenExpires ? ShortLivedTokenExpiry() : null);

        using var scope = _fixture.BeginScopeAs(memberId, teamId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));   // safety net — the expiry or the terminal record ends the tail
        await using var tail = scope.Resolve<IRunRecordStreamer>().TailAsync(runId, cursor, cts.Token).GetAsyncEnumerator(cts.Token);

        (await tail.MoveNextAsync()).ShouldBeTrue("the caller tails the run while the token is still accepted");

        var next = tail.MoveNextAsync().AsTask();
        var endedWhileIdle = await Task.WhenAny(next, Task.Delay(ShortTokenWindow + TimeSpan.FromSeconds(2))) == next;

        if (!endedWhileIdle) await InsertRecordAndCommitAsync(runId, WorkflowRunRecordTypes.RunCompleted, "idle-terminal");   // wakes a tail still open, so it ends either way

        var yielded = await next;

        endedWhileIdle.ShouldBe(tokenExpires, $"tokenExpires={tokenExpires}: an idle tail ends by itself exactly when the caller's token stops being accepted; nothing was written to wake it");
        yielded.ShouldBe(!tokenExpires, $"tokenExpires={tokenExpires}: only the tail under an accepted token is still live to receive the next record");
    }

    public enum StandingChange { Kept, MembershipRemoved, TeamDeleted, SessionSuperseded, AccountDeactivated, TokenExpired }

    private const string LaterRecordType = "test.written-after-the-change";

    /// <summary>How long a short-lived token stays accepted once presented. Long enough to open the tail and read one record.</summary>
    private static readonly TimeSpan ShortTokenWindow = TimeSpan.FromSeconds(5);

    /// <summary>An <c>exp</c> the bearer handler accepts for <see cref="ShortTokenWindow"/> more: its skew already covers the past part.</summary>
    private static DateTimeOffset ShortLivedTokenExpiry() => DateTimeOffset.UtcNow - BearerTokenLifetime.ClockSkew + ShortTokenWindow;

    private async Task ChangeStandingAsync(StandingChange change, Guid memberId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        switch (change)
        {
            case StandingChange.Kept:
                return;
            case StandingChange.MembershipRemoved:
                (await db.TeamMembership.Where(m => m.TeamId == teamId && m.UserId == memberId).ExecuteDeleteAsync()).ShouldBe(1);
                return;
            case StandingChange.TeamDeleted:
                (await db.Team.Where(t => t.Id == teamId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DeletedDate, DateTimeOffset.UtcNow))).ShouldBe(1);
                return;
            case StandingChange.SessionSuperseded:
                (await db.User.Where(u => u.Id == memberId).ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, Guid.NewGuid()))).ShouldBe(1);   // what a password change does to every other session
                return;
            case StandingChange.AccountDeactivated:
                await scope.Resolve<IAccountLifecycleService>().DeactivateAsync(memberId, CancellationToken.None);   // switches the account off and rotates its stamp
                return;
            case StandingChange.TokenExpired:
                await Task.Delay(ShortTokenWindow + TimeSpan.FromSeconds(1));   // nobody acts: the token simply ages out
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }
    }

    private async Task GrantGlobalAdminAsync(Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.RoleUser.Add(new RoleUser { Id = Guid.NewGuid(), RoleId = await db.Role.Where(r => r.Name == Roles.Admin).Select(r => r.Id).SingleAsync(), UserId = userId });
        await db.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<RunRecordView>> DrainAsync(IAsyncEnumerator<RunRecordView> tail, StandingChange change)
    {
        var records = new List<RunRecordView>();

        try
        {
            while (await tail.MoveNextAsync()) records.Add(tail.Current);
        }
        catch (OperationCanceledException)
        {
            throw new ShouldAssertException($"{change}: the tail neither ended nor reached the terminal record within 20s; got {records.Count} record(s). A terminal record was committed, so a live tail ends on it; a hang means the tail stopped polling. Check RunRecordStreamer.TailAsync.");
        }

        return records;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> ReadSecurityStampAsync(Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().User.AsNoTracking().Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync();
    }

    /// <summary>
    /// Leaves on the current flow the principal the bearer handler leaves on a real request: the subject, the stamp the
    /// token was minted under, and its <c>exp</c> (an hour out unless given). Synchronous on purpose — an ambient value
    /// set inside an async method is discarded when that method returns, so the caller would tail with no session at all.
    /// </summary>
    private void PresentSession(Guid userId, Guid stamp, DateTimeOffset? expiresAt = null)
    {
        var exp = (expiresAt ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(SessionValidator.SecurityStampClaim, stamp.ToString()), new Claim("exp", exp, ClaimValueTypes.Integer64) }, "Bearer");

        using var scope = _fixture.BeginScope();
        scope.Resolve<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    private static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value);

    private async Task<long> InsertRecordAndCommitAsync(Guid runId, string recordType, string applicationName)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { ApplicationName = applicationName }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var sequence = await InsertRecordAsync(connection, transaction, runId, recordType);
        await transaction.CommitAsync();
        return sequence;
    }

    private static async Task<long> InsertRecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, string recordType)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO workflow_run_record (id, run_id, record_type, iteration_key, payload_json)
            VALUES (gen_random_uuid(), @run_id, @record_type, '', '{}'::jsonb)
            RETURNING sequence
            """, connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("record_type", recordType);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> ReadRunCursorAsync(Guid runId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COALESCE(MAX(sequence), 0) FROM workflow_run_record WHERE run_id = @run_id", connection);
        command.Parameters.AddWithValue("run_id", runId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> WaitForLockAsync(string applicationName, Task<long> insert)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            if (insert.IsCompleted) return false;
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE application_name = @application_name AND wait_event_type = 'Lock'
                )
                """, connection);
            command.Parameters.AddWithValue("application_name", applicationName);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return true;
            await Task.Delay(20, timeout.Token);
        }

        return false;
    }

    private async Task<Guid> SeedRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "sse-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<IReadOnlyList<RunRecordView>> TailAsync(Guid teamId, Guid userId, Guid runId, long after)
    {
        PresentSession(userId, await ReadSecurityStampAsync(userId));   // the tail re-verifies the caller's session before handing out each poll's rows

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var streamer = scope.Resolve<IRunRecordStreamer>();

        var records = new List<RunRecordView>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));   // safety net — a correct tail stops itself at the terminal record
        await foreach (var r in streamer.TailAsync(runId, after, cts.Token))
            records.Add(r);

        return records;
    }
}
