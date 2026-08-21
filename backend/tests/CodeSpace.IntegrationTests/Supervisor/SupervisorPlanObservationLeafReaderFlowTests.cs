using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Observation;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Shouldly;

namespace CodeSpace.IntegrationTests.Supervisor;

/// <summary>
/// True-PostgreSQL pins for the additive Plan leaf foundation. Current Journal facts remain on #1615 by design; the
/// healthy parity assertions compare their values but do not cut those sources over without an omission contract.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorPlanObservationLeafReaderFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorPlanObservationLeafReaderFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Missing_and_foreign_runs_are_conflated_while_owned_empty_is_distinct()
    {
        var teamId = await SeedTeamAsync();
        var foreignTeam = await SeedTeamAsync();
        var ownedRun = await SeedRunAsync(teamId);
        var foreignRun = await SeedRunAsync(foreignTeam);
        await InsertPlanAsync(foreignTeam, foreignRun, "{}", null);

        (await ReadAsync(teamId, foreignRun)).ShouldBeNull();
        (await ReadAsync(teamId, Guid.NewGuid())).ShouldBeNull();

        var empty = await ReadAsync(teamId, ownedRun);
        empty.ShouldNotBeNull();
        empty.Items.ShouldBeEmpty();
        empty.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task Healthy_case_insensitive_plan_and_exact_model_usage_match_existing_sources_without_transferring_two_megabyte_baggage()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId);
        var decisionId = await InsertLargeHealthyPlanAsync(teamId, runId);
        var tokenTypesId = await InsertDecisionAsync(teamId, runId, SupervisorDecisionKinds.Plan,
            """{"subtasks":[{"id":"typed","title":"Typed tokens","instruction":"Check types"}]}""",
            """{"modelUsage":{"model":"typed-model","inputTokens":"7","outputTokens":2.0}}""");
        await InsertDecisionAsync(teamId, runId, "future.plan-like", "{}", "{}");

        var page = (await ReadAsync(teamId, runId))!;
        page.Items.Count.ShouldBe(2, "unknown open kinds are not misclassified as Plan rows");
        var item = page.Items.Single(value => value.Metadata.DecisionId == decisionId);
        item.Metadata.DecisionId.ShouldBe(decisionId);
        item.Metadata.DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan);
        item.Metadata.Status.ShouldBe(SupervisorDecisionObservationStatus.Succeeded);
        item.Metadata.StoryOrder.ShouldBeGreaterThan(0);
        item.Metadata.ObservationRevision.ShouldBeGreaterThan(0);
        item.SubtasksState.ShouldBe(SupervisorPlanObservationLeafState.Exact);
        item.Subtasks.Select(subtask => (subtask.IdPrefix, subtask.TitlePrefix))
            .ShouldBe(new[] { ("s1", "Research"), ("s2", "Write") }, "AgentJson case-insensitive payload names stay readable");
        item.ModelUsageState.ShouldBe(SupervisorPlanObservationLeafState.Exact);
        item.ModelUsage.ShouldNotBeNull();
        item.ModelUsage!.ModelPrefix.ShouldBe("metis-coder-plus");
        item.ModelUsage.InputTokens.ShouldBe(1_000);
        item.ModelUsage.OutputTokens.ShouldBe(200);
        var typedTokens = page.Items.Single(value => value.Metadata.DecisionId == tokenTypesId);
        typedTokens.ModelUsageState.ShouldBe(SupervisorPlanObservationLeafState.Exact);
        typedTokens.ModelUsage!.InputTokens.ShouldBeNull("a JSON string is not a token number");
        typedTokens.ModelUsage.OutputTokens.ShouldBeNull("2.0 is numeric but not an integer token in JsonElement.TryGetInt32 semantics");

        using (var scope = _fixture.BeginScope())
        {
            var bundle = scope.Resolve<ISupervisorDecisionObservationBundle>();
            var planFacts = await new PlanFactsSource(bundle).GatherAsync(runId, teamId, CancellationToken.None);
            var callFacts = await new SupervisorPlanModelCallFactsSource(bundle).GatherAsync(runId, teamId, CancellationToken.None);
            var stepId = $"supervisor-{decisionId:N}";
            planFacts[stepId].Plan!.Select(subtask => (subtask.SubtaskId, subtask.Title))
                .ShouldBe(item.Subtasks.Select(subtask => (subtask.IdPrefix, subtask.TitlePrefix)));
            callFacts[stepId].ModelCall!.Model.ShouldBe(item.ModelUsage.ModelPrefix);
            callFacts[stepId].ModelCall!.InputTokens.ShouldBe(item.ModelUsage.InputTokens);
            callFacts[stepId].ModelCall!.OutputTokens.ShouldBe(item.ModelUsage.OutputTokens);
            var typedStepId = $"supervisor-{tokenTypesId:N}";
            callFacts[typedStepId].ModelCall!.InputTokens.ShouldBe(typedTokens.ModelUsage.InputTokens);
            callFacts[typedStepId].ModelCall!.OutputTokens.ShouldBe(typedTokens.ModelUsage.OutputTokens);
        }

        var wire = JsonSerializer.Serialize(page);
        wire.ShouldNotContain("PAYLOAD-SENTINEL");
        wire.ShouldNotContain("OUTCOME-SENTINEL");
        wire.Length.ShouldBeLessThan(12_000, "2 MiB JSONB baggage must contribute zero bytes to the leaf DTO");
    }

    [Fact]
    public async Task Caps_and_malformed_shapes_are_explicit_instead_of_becoming_partial_or_empty_truth()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId);
        var cappedId = await InsertCappedPlanAsync(teamId, runId);
        var invalidId = await InsertPlanAsync(teamId, runId, """{"subtasks":[{"id":"bad","title":"Missing instruction"}]}""", """{"modelUsage":{"Model":"wrong-case"}}""");
        var missingId = await InsertPlanAsync(teamId, runId, "{}", null);
        var duplicateRootId = await InsertPlanAsync(teamId, runId,
            """{"subtasks":[],"Subtasks":[{"id":"ambiguous","title":"Ambiguous","instruction":"Do it"}]}""", "{}");
        var duplicateLeafId = await InsertPlanAsync(teamId, runId,
            """{"subtasks":[{"id":"a","ID":"b","title":"Ambiguous","instruction":"Do it"}]}""", "{}");
        var corruptStatusId = await InsertPlanAsync(teamId, runId, """{"subtasks":[]}""", "{}");
        await SetStatusAsync(corruptStatusId, "FutureTerminal");

        var page = (await ReadAsync(teamId, runId))!;
        var capped = page.Items.Single(item => item.Metadata.DecisionId == cappedId);
        capped.SubtasksState.ShouldBe(SupervisorPlanObservationLeafState.Truncated);
        capped.SubtasksTotalCount.ShouldBe(25);
        capped.Subtasks.Count.ShouldBe(SupervisorPlanObservationLeafLimits.MaximumSubtasks);
        capped.SubtasksOmittedCount.ShouldBe(5);
        capped.Subtasks[0].IdPrefix.Length.ShouldBe(SupervisorPlanObservationLeafLimits.MaximumIdChars);
        capped.Subtasks[0].IdTotalBytes.ShouldBeGreaterThan(capped.Subtasks[0].IdPrefix.Length);
        capped.Subtasks[0].TitlePrefix.Length.ShouldBe(SupervisorPlanObservationLeafLimits.MaximumTitleChars);
        capped.ModelUsageState.ShouldBe(SupervisorPlanObservationLeafState.Truncated);
        capped.ModelUsage!.ModelPrefix.Length.ShouldBe(SupervisorPlanObservationLeafLimits.MaximumModelChars);
        capped.ModelUsage.ModelTotalBytes.ShouldBeGreaterThan(capped.ModelUsage.ModelPrefix.Length);

        var invalid = page.Items.Single(item => item.Metadata.DecisionId == invalidId);
        invalid.SubtasksState.ShouldBe(SupervisorPlanObservationLeafState.Invalid);
        invalid.Subtasks.ShouldBeEmpty("invalid leaves are never promoted to trusted partial data");
        invalid.SubtasksOmittedCount.ShouldBe(1);
        invalid.ModelUsageState.ShouldBe(SupervisorPlanObservationLeafState.Invalid, "modelUsage keys remain exact-case");
        invalid.ModelUsage.ShouldBeNull();

        var missing = page.Items.Single(item => item.Metadata.DecisionId == missingId);
        missing.SubtasksState.ShouldBe(SupervisorPlanObservationLeafState.Missing);
        missing.ModelUsageState.ShouldBe(SupervisorPlanObservationLeafState.Missing);

        page.Items.Single(item => item.Metadata.DecisionId == duplicateRootId).SubtasksState
            .ShouldBe(SupervisorPlanObservationLeafState.Invalid, "JSONB canonical order cannot reconstruct AgentJson last-wins for casing duplicates");
        page.Items.Single(item => item.Metadata.DecisionId == duplicateLeafId).SubtasksState
            .ShouldBe(SupervisorPlanObservationLeafState.Invalid, "case-insensitive duplicate leaf keys are ambiguous, not arbitrary");

        page.Items.Single(item => item.Metadata.DecisionId == corruptStatusId).Metadata.Status
            .ShouldBe(SupervisorDecisionObservationStatus.Corrupt, "a future persisted status cannot EF-materialize or masquerade as known");
    }

    [Fact]
    public async Task Ten_thousand_newer_non_plan_rows_cannot_turn_a_plan_page_into_an_o_run_filter_scan()
    {
        var teamId = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId);
        await InsertFloodAsync(teamId, runId, SupervisorDecisionKinds.Plan, 600);
        await InsertFloodAsync(teamId, runId, SupervisorDecisionKinds.Spawn, 10_050);

        var tail = (await ReadAsync(teamId, runId, limit: 500))!;
        tail.Items.Count.ShouldBe(500);
        tail.Items.Select(item => item.Metadata.StoryOrder).ShouldBeInOrder();
        tail.Items.Select(item => item.Metadata.ObservationRevision).ShouldAllBe(revision => revision > 0);
        tail.HasMore.ShouldBeTrue();
        tail.NextOlderCursor.ShouldNotBeNull();

        var older = (await ReadAsync(teamId, runId, SupervisorDecisionObservationStoryPageMode.Older, tail.NextOlderCursor, 500))!;
        older.Items.Count.ShouldBe(100);
        older.Items.Select(item => item.Metadata.StoryOrder).ShouldBeInOrder();
        older.Items.Select(item => item.Metadata.DecisionId).Intersect(tail.Items.Select(item => item.Metadata.DecisionId)).ShouldBeEmpty();
        older.HasMore.ShouldBeFalse();

        var newer = (await ReadAsync(teamId, runId, SupervisorDecisionObservationStoryPageMode.Newer, tail.NextNewerCursor, 500))!;
        newer.Items.ShouldBeEmpty("newer rows of other kinds never leak into the Plan-only story page");

        var plan = await ExplainAsync(SupervisorPlanObservationLeafReader.OlderSql, teamId, runId, long.MaxValue, 501);
        plan.ShouldContain("ix_supervisor_decision_run_kind_story_order");
        plan.ShouldNotContain("Seq Scan on supervisor_decision");
        plan.ShouldNotContain("Rows Removed by Filter: 10050", customMessage: "newer non-Plan rows are outside the index range, not discarded after an O(run) walk");
    }

    private async Task<SupervisorPlanObservationPage?> ReadAsync(Guid teamId, Guid runId, SupervisorDecisionObservationStoryPageMode mode = SupervisorDecisionObservationStoryPageMode.Tail, string? cursor = null, int limit = SupervisorDecisionObservationPageLimits.DefaultLimit)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISupervisorPlanObservationLeafReader>()
            .ReadPageAsync(new SupervisorPlanObservationPageRequest(teamId, runId, mode, cursor, limit), CancellationToken.None);
    }

    private async Task<Guid> InsertLargeHealthyPlanAsync(Guid teamId, Guid runId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var id = Guid.NewGuid();
        await using var command = InsertCommand(connection, new DecisionIdentity(teamId, runId, id, SupervisorDecisionKinds.Plan, "Pending"));
        command.CommandText = InsertPrefix + """
            jsonb_build_object(
                'SuBtAsKs', jsonb_build_array(
                    jsonb_build_object('ID', 's1', 'TITLE', 'Research', 'INSTRUCTION', 'Do research'),
                    jsonb_build_object('id', 's2', 'title', 'Write', 'instruction', 'Write report')),
                'baggage', repeat('PAYLOAD-SENTINEL', 140000)),
            jsonb_build_object(
                'modelUsage', jsonb_build_object('model', 'metis-coder-plus', 'inputTokens', 1000, 'outputTokens', 200),
                'baggage', repeat('OUTCOME-SENTINEL', 130000)),
            0, @actor, @actor, -1, -1)
            """;
        await command.ExecuteNonQueryAsync();
        await SetStatusAsync(id, "Succeeded");
        return id;
    }

    private async Task<Guid> InsertCappedPlanAsync(Guid teamId, Guid runId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var id = Guid.NewGuid();
        await using var command = InsertCommand(connection, new DecisionIdentity(teamId, runId, id, SupervisorDecisionKinds.Plan, "Succeeded"));
        command.CommandText = InsertPrefix + """
            jsonb_build_object('subtasks', (
                SELECT jsonb_agg(jsonb_build_object(
                    'id', CASE WHEN value = 1 THEN repeat('界', 250) ELSE 's' || value::text END,
                    'title', CASE WHEN value = 1 THEN repeat('界', 500) ELSE 'Task ' || value::text END,
                    'instruction', 'Do it') ORDER BY value)
                FROM generate_series(1, 25) AS value)),
            jsonb_build_object('modelUsage', jsonb_build_object('model', repeat('m', 300), 'inputTokens', 10, 'outputTokens', 5)),
            0, @actor, @actor, -1, -1)
            """;
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private Task<Guid> InsertPlanAsync(Guid teamId, Guid runId, string payload, string? outcome) =>
        InsertDecisionAsync(teamId, runId, SupervisorDecisionKinds.Plan, payload, outcome);

    private async Task<Guid> InsertDecisionAsync(Guid teamId, Guid runId, string kind, string payload, string? outcome)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var id = Guid.NewGuid();
        await using var command = InsertCommand(connection, new DecisionIdentity(teamId, runId, id, kind, "Succeeded"));
        command.CommandText = InsertPrefix + "(@payload)::jsonb, (@outcome)::jsonb, 0, @actor, @actor, -1, -1)";
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        command.Parameters.Add("outcome", NpgsqlDbType.Jsonb).Value = (object?)outcome ?? DBNull.Value;
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static NpgsqlCommand InsertCommand(NpgsqlConnection connection, DecisionIdentity decision)
    {
        var command = new NpgsqlCommand { Connection = connection };
        command.Parameters.AddWithValue("id", decision.DecisionId);
        command.Parameters.AddWithValue("team", decision.TeamId);
        command.Parameters.AddWithValue("run", decision.RunId);
        command.Parameters.AddWithValue("kind", decision.Kind);
        command.Parameters.AddWithValue("key", $"{decision.Kind}:{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("status", decision.Status);
        command.Parameters.AddWithValue("actor", SystemUsers.SeederId);
        return command;
    }

    private const string InsertPrefix = """
        INSERT INTO supervisor_decision
            (id, team_id, supervisor_run_id, decision_kind, idempotency_key, input_hash, status,
             payload_jsonb, outcome_jsonb, fence_epoch, created_by, last_modified_by, story_order, observation_revision)
        VALUES
            (@id, @team, @run, @kind, @key, repeat('0', 64), @status,
        """;

    private async Task SetStatusAsync(Guid decisionId, string status)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE supervisor_decision SET status = @status WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", decisionId);
        command.Parameters.AddWithValue("status", status);
        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    private async Task InsertFloodAsync(Guid teamId, Guid runId, string kind, int count)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO supervisor_decision
                (id, team_id, supervisor_run_id, decision_kind, idempotency_key, input_hash, status,
                 payload_jsonb, outcome_jsonb, fence_epoch, created_by, last_modified_by, story_order, observation_revision)
            SELECT md5(@salt || value::text)::uuid, @team, @run, @kind, @salt || ':' || value::text,
                   repeat('0', 64), 'Pending',
                   '{"subtasks":[{"id":"s","title":"T","instruction":"I"}]}'::jsonb,
                   NULL, 0, @actor, @actor, -1, -1
            FROM generate_series(1, @count) AS value
            """, connection);
        command.Parameters.AddWithValue("salt", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("team", teamId);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("actor", SystemUsers.SeederId);
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync();
        await using var analyze = new NpgsqlCommand("ANALYZE supervisor_decision", connection);
        await analyze.ExecuteNonQueryAsync();
    }

    private async Task<string> ExplainAsync(string sql, Guid teamId, Guid runId, long cursor, int take)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, COSTS OFF, SUMMARY OFF, TIMING OFF) " + sql, connection);
        command.Parameters.AddWithValue("team_id", teamId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("plan_kind", SupervisorDecisionKinds.Plan);
        command.Parameters.AddWithValue("cursor", cursor);
        command.Parameters.AddWithValue("take", take);
        command.Parameters.AddWithValue("error_chars", SupervisorDecisionObservationMetadataReader.ErrorPrefixMaximumChars);
        command.Parameters.AddWithValue("max_subtasks", SupervisorPlanObservationLeafLimits.MaximumSubtasks);
        command.Parameters.AddWithValue("id_chars", SupervisorPlanObservationLeafLimits.MaximumIdChars);
        command.Parameters.AddWithValue("title_chars", SupervisorPlanObservationLeafLimits.MaximumTitleChars);
        command.Parameters.AddWithValue("model_chars", SupervisorPlanObservationLeafLimits.MaximumModelChars);
        command.Parameters.AddWithValue("token_chars", 64);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    private async Task<Guid> SeedTeamAsync()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        return teamId;
    }

    private async Task<Guid> SeedRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot,
            ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Failure,
            ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = now,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    private sealed record DecisionIdentity(Guid TeamId, Guid RunId, Guid DecisionId, string Kind, string Status);
}
