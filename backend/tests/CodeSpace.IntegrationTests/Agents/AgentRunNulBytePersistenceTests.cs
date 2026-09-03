using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// A stray U+0000 in one line of agent output must not be able to kill a run.
///
/// <para>Real-model run 33755336097 died on <c>22021 invalid byte sequence for encoding "UTF8": 0x00</c> out of
/// <c>AgentRunService.AppendEventsAsync</c>: the harness put a NUL in one event's text, the batched INSERT was
/// refused, and the run published no branch — so its delivery card said it "found no published branch" instead of
/// naming the policy conflict it had actually hit. Content-dependent, so it strikes at random.</para>
///
/// <para>The first test MEASURES the premise rather than assuming it, because the two rejections are different
/// and only one of them is the byte: <c>text</c> refuses the raw U+0000 (<c>22021</c>), while <c>jsonb</c> refuses
/// the six-character ESCAPE a JSON writer makes of it (<c>22P05</c>) — a document holding that escape contains no
/// NUL byte at all. A sanitizer that only stripped NUL characters would leave the jsonb payload exactly as
/// unstorable as it was. Everything after it proves the seams survive what would otherwise be refused.</para>
///
/// <para>MUTATION CHECK: drop the <c>PersistedText.Sanitize</c> call from <c>AppendEventsAsync</c> and
/// <c>An_event_whose_text_carries_a_nul_appends_and_round_trips_without_it</c> fails with 22021.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunNulBytePersistenceTests
{
    private const string Nul = "\u0000";

    private readonly PostgresFixture _fixture;

    public AgentRunNulBytePersistenceTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Postgres_rejects_a_raw_nul_in_text_and_the_nul_escape_in_jsonb()
    {
        // The premise, measured against the real server rather than taken on faith — writing PAST the service, so
        // this fails the day either rejection stops being real and the sanitizer becomes dead weight.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedAgentRunAsync(teamId);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        var rawByte = await Should.ThrowAsync<PostgresException>(() => InsertRawAsync(conn, runId, "a" + Nul + "b", null));
        rawByte.SqlState.ShouldBe("22021", "a `text` column refuses the raw NUL byte — this is the bug's own error");

        var escape = await Should.ThrowAsync<PostgresException>(() => InsertRawAsync(conn, runId, "clean", @"{""k"":""a\u0000b""}"));
        escape.SqlState.ShouldBe("22P05",
            "`jsonb` refuses the ESCAPE too, and that document holds NO nul byte — which is why stripping nul characters alone is not enough");
    }

    [Fact]
    public async Task An_event_whose_text_carries_a_nul_appends_and_round_trips_without_it()
    {
        // The crash site: the BATCHED raw-SQL path, where one bad byte used to take every event in the flush with it.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedAgentRunAsync(teamId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().AppendEventsAsync(runId, new[]
            {
                new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "before the nul" },
                new AgentEvent { Kind = AgentEventKind.CommandExecuted, Text = "npm" + Nul + " test" },
                new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "after the nul" },
            }, CancellationToken.None);

        var events = await ReadEventsAsync(runId);

        events.Count.ShouldBe(3, "the clean events in the batch must not be lost to their neighbour's byte");
        events[1].Text.ShouldBe("npm test", "the nul is removed and NOTHING else about the line is touched");
        events[0].Text.ShouldBe("before the nul");
        events[2].Text.ShouldBe("after the nul");
    }

    [Fact]
    public async Task An_event_whose_json_payload_carries_a_nul_appends_and_round_trips()
    {
        // The jsonb half — System.Text.Json writes the byte as the escape, so this row is refused for a DIFFERENT
        // reason than the one above, at a different SQLSTATE.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedAgentRunAsync(teamId);

        var payload = JsonSerializer.SerializeToElement(new { command = "npm" + Nul + " test", exitCode = 0 });
        payload.GetRawText().ShouldContain(@"\u0000", Case.Sensitive,
            "the writer must really be producing the escape, or this test is proving nothing");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().AppendEventAsync(runId,
                new AgentEvent { Kind = AgentEventKind.CommandExecuted, Text = "ran" + Nul + " it", Data = payload }, CancellationToken.None);

        var stored = (await ReadEventsAsync(runId)).ShouldHaveSingleItem();

        stored.Text.ShouldBe("ran it");
        stored.DataJson.ShouldNotBeNull();
        stored.DataJson!.ShouldContain("npm test");
        stored.DataJson!.ShouldNotContain(@"\u0000", Case.Sensitive);
        JsonDocument.Parse(stored.DataJson!).RootElement.GetProperty("exitCode").GetInt32()
            .ShouldBe(0, "the rest of the payload survives as valid json");
    }

    [Fact]
    public async Task A_completion_whose_summary_and_error_carry_a_nul_lands_terminal()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid runId;
        using (var scope = _fixture.BeginScope())
            runId = (await scope.Resolve<IAgentRunService>().CreateAsync(BuildTask(), teamId, null, null, cancellationToken: CancellationToken.None)).Id;

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().MarkRunningAsync(runId, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().CompleteAsync(runId, new AgentRunResult
            {
                Status = AgentRunStatus.Failed,
                ExitReason = "failed",
                Summary = "the patch" + Nul + " did not apply",
                Error = "fatal:" + Nul + " bad object",
            }, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(AgentRunStatus.Failed, "a run that finished must be able to SAY so");
        run.Error.ShouldBe("fatal: bad object");
        run.ResultJson.ShouldNotBeNull();
        run.ResultJson!.ShouldContain("the patch did not apply");
        run.ResultJson!.ShouldNotContain(@"\u0000", Case.Sensitive);
    }

    [Fact]
    public async Task A_run_record_whose_payload_carries_a_nul_persists()
    {
        // The second seam: every workflow run record — an llm completion, a node's outputs, an error line — is
        // written through RunRecordLogger.InsertAsync, so one sanitize there covers all of them.
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, ownerId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IRunRecordLogger>().LogAsync(runId, "agent", LogLevel.Warn, "the model said" + Nul + " this", CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var record = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.Log)
            .SingleAsync();

        record.PayloadJson.ShouldNotBeNull();
        JsonDocument.Parse(record.PayloadJson!).RootElement.GetProperty("message").GetString()
            .ShouldBe("the model said this");
    }

    private static Task InsertRawAsync(NpgsqlConnection conn, Guid runId, string text, string? dataJson)
    {
        var cmd = new NpgsqlCommand(
            "INSERT INTO agent_run_event (id, agent_run_id, kind, text, data_json) VALUES (@id, @run, 'AssistantMessage', @text, CAST(@data AS jsonb))", conn);

        cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("@run", runId);
        cmd.Parameters.AddWithValue("@text", text);
        cmd.Parameters.AddWithValue("@data", (object?)dataJson ?? DBNull.Value);

        return cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<AgentRunEvent>> ReadEventsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRunEvent.AsNoTracking()
            .Where(e => e.AgentRunId == runId).OrderBy(e => e.Sequence).ToListAsync();
    }

    private async Task<Guid> SeedAgentRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var runId = Guid.NewGuid();
        db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running });
        await db.SaveChangesAsync();

        return runId;
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "nul-byte-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private static AgentTask BuildTask() => new() { Goal = "fix the failing test", Harness = "codex-cli", Model = "gpt-5.3-codex" };
}
