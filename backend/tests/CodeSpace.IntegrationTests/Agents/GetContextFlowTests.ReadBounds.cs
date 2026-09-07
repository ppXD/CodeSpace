using System.Collections;
using System.Data.Common;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Context.Sources;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

public partial class GetContextFlowTests
{
    [Fact]
    [Trait("P17", "Regression")]
    public async Task A_query_match_past_the_per_turn_display_clip_is_still_found()
    {
        // SQL's WHERE now matches the FULL (unclipped) result — the display clip (left(…, @leaf_take)) only bounds
        // what is RENDERED, never what is SEARCHED. Regression coverage for the old behaviour: the old client-side
        // re-filter re-checked the query against the CLIPPED rendered copy, so a needle past the clip boundary
        // passed SQL then failed that re-filter — a false "not found" for a turn that genuinely matched.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        const string needle = "NEEDLE_PAST_THE_DISPLAY_CLIP_9137";
        var hugeResult = new string('z', SessionTurnsContextSource.MaxOutputChars + 5000) + needle;
        await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "huge", JsonSerializer.Serialize(new { summary = hugeResult }));

        var result = await RetrieveTurnsAsync(teamId, sessionId, query: needle);

        result.Found.ShouldBeTrue("the needle sits past the per-leaf display clip, but SQL matches the full, unclipped result — the turn must still be found");
    }

    [Fact]
    [Trait("P17", "Regression")]
    public async Task Session_turns_query_matches_a_turns_produced_branch()
    {
        // The SQL predicate used to cover only goal/summary/combined/reason, even though the rendered text also
        // carries "Produced branch: X" — a query naming the branch silently found nothing. Covers the raw legacy
        // OutputsJson.branch leaf (the Description names the one remaining gap: a branch resolved ONLY from a
        // PublishManifest row, disagreeing with this leaf, is not itself searchable).
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "the work", JsonSerializer.Serialize(new { summary = "unrelated result text", branch = "feature/distinctive-branch-9137" }));
        await SeedTurnAsync(teamId, sessionId, turn: 2, goal: "other work", JsonSerializer.Serialize(new { summary = "other result", branch = "main" }));

        var result = await RetrieveTurnsAsync(teamId, sessionId, query: "distinctive-branch-9137");

        result.Found.ShouldBeTrue();
        result.Text.ShouldContain("distinctive-branch-9137");
        result.Text.ShouldNotContain("other result", customMessage: "the non-matching turn is filtered out");
    }

    [Fact]
    [Trait("P17", "Regression")]
    public async Task Session_turns_query_matches_a_turns_status()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedTurnWithStatusAsync(teamId, sessionId, turn: 1, goal: "flaky task", JsonSerializer.Serialize(new { summary = "did not finish" }), WorkflowRunStatus.Failure);
        await SeedTurnAsync(teamId, sessionId, turn: 2, goal: "other", JsonSerializer.Serialize(new { summary = "clean result" }));

        var result = await RetrieveTurnsAsync(teamId, sessionId, query: "Failure");

        result.Found.ShouldBeTrue();
        result.Text.ShouldContain("did not finish");
        result.Text.ShouldNotContain("clean result", customMessage: "the non-matching turn is filtered out");
    }

    /// <summary>Stage a finished turn with an EXPLICIT (possibly non-Success) status — <c>GetContextFlowTests.SeedTurnAsync</c> always seeds Success.</summary>
    private async Task SeedTurnWithStatusAsync(Guid teamId, Guid sessionId, int turn, string goal, string outputsJson, WorkflowRunStatus status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = JsonSerializer.Serialize(new { goal }),
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = Guid.NewGuid(), TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = status, SessionId = sessionId, SessionTurnIndex = turn,
            OutputsJson = outputsJson, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    [Fact]
    [Trait("P17", "Regression")]
    public async Task Context_retrieval_does_not_materialize_unrelated_json_roots_or_all_history_rows()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        var baggage = new string('x', 2 * 1024 * 1024);
        await SeedTurnAsync(teamId, sessionId, 1, "needle", JsonSerializer.Serialize(new { summary = "BOUNDED_SOURCE_NEEDLE", unrelated = baggage }));
        for (var turn = 2; turn <= 61; turn++) await SeedTurnAsync(teamId, sessionId, turn, $"unrelated-{turn}", JsonSerializer.Serialize(new { summary = "other result" }));
        var reads = new ContextReadCounter();
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(reads).Options;
        using var scope = _fixture.BeginScope(builder => builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>());

        var result = await scope.Resolve<SessionTurnsContextSource>().RetrieveAsync(new AgentContextQuery { TeamId = teamId, RunId = Guid.NewGuid(), SessionId = sessionId, Query = "BOUNDED_SOURCE_NEEDLE" }, CancellationToken.None);

        reads.Readers.ShouldNotBeEmpty("the counter observed actual PostgreSQL readers, not a mock query result");
        reads.Readers.Max(r => r.MaxStringChars).ShouldBeLessThanOrEqualTo(2 * SessionTurnsContextSource.MaxOutputChars, "unrelated multi-MiB JSON roots must remain inside PostgreSQL");
        reads.Readers.Max(r => r.Rows).ShouldBeLessThanOrEqualTo(SessionTurnsContextSource.MaxTurnsScanned + 1, "one source read has a bounded page plus lookahead, not all historical attempts");
        result.Found.ShouldBeTrue();
        result.Text.ShouldContain("BOUNDED_SOURCE_NEEDLE");
        result.Text.ShouldNotContain(baggage);
    }

    private sealed class ContextReadCounter : DbCommandInterceptor
    {
        public List<ContextReadPage> Readers { get; } = [];

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("workflow_run", StringComparison.Ordinal)) return ValueTask.FromResult(result);
            var page = new ContextReadPage();
            Readers.Add(page);
            return ValueTask.FromResult<DbDataReader>(new ContextCountedReader(result, page));
        }
    }

    private sealed class ContextReadPage
    {
        public int Rows { get; set; }
        public int MaxStringChars { get; set; }
        public T Observe<T>(T value) { if (value is string text) MaxStringChars = Math.Max(MaxStringChars, text.Length); return value; }
    }

    private sealed class ContextCountedReader(DbDataReader inner, ContextReadPage page) : DbDataReader
    {
        public override bool Read() { var read = inner.Read(); if (read) page.Rows++; return read; }
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken) { var read = await inner.ReadAsync(cancellationToken); if (read) page.Rows++; return read; }
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override int VisibleFieldCount => inner.VisibleFieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => page.Observe(inner[ordinal]);
        public override object this[string name] => page.Observe(inner[name]);
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => page.Observe(inner.GetString(ordinal));
        public override object GetValue(int ordinal) => page.Observe(inner.GetValue(ordinal));
        public override int GetValues(object[] values) { var count = inner.GetValues(values); for (var i = 0; i < count; i++) page.Observe(values[i]); return count; }
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override T GetFieldValue<T>(int ordinal) => page.Observe(inner.GetFieldValue<T>(ordinal));
        public override async Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => page.Observe(await inner.GetFieldValueAsync<T>(ordinal, cancellationToken));
        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => inner.IsDBNullAsync(ordinal, cancellationToken);
        public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);
        public override void Close() => inner.Close();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
    }
}
