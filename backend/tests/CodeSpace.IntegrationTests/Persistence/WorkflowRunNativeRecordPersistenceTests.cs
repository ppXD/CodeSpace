using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres proof that the native-record plane cannot be made to lie. Every assertion here is a COUNTER-EXAMPLE:
/// the illegal row is offered and the database REFUSES it, because an invariant that only holds while every writer
/// remembers it is not an invariant — and this plane's whole value is that a reader can trust what a record claims.
///
/// <para>The load-bearing ones: a frame carries exactly one payload arm, so an absent payload can never read as an
/// empty frame; a projection must name the frames it was folded from, so an "exact" fact is never a claim about
/// nothing; and an exactly-grounded projection's sources must actually be verbatim, because <c>NativeRecordV1</c>
/// already refuses a masked frame carrying an exact payload claim and a laxer database would simply move that lie one
/// table over.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunNativeRecordPersistenceTests
{
    private const string CanonicalDigest = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly PostgresFixture _fixture;

    public WorkflowRunNativeRecordPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>The happy path, first — an arm that refuses everything would pass every counter-example below while recording nothing.</summary>
    [Fact]
    public async Task A_captured_frame_and_the_projection_that_cites_it_both_land()
    {
        var world = await SeedWorldAsync();
        var record = await SeedRecordAsync(world, ordinal: 0);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunSemanticEvent.Add(Event(world, record.Id));
            await db.SaveChangesAsync();
        }

        using var read = _fixture.BeginScope();
        var stored = read.Resolve<CodeSpaceDbContext>();
        (await stored.WorkflowRunNativeRecord.SingleAsync(candidate => candidate.Id == record.Id)).Normalization.ShouldBe(NativeRecordNormalization.Projected);
        (await stored.WorkflowRunSemanticEvent.SingleAsync(candidate => candidate.ExecutionId == world.ExecutionId))
            .SourceNativeRecordIds.ShouldBe(new[] { record.Id });
    }

    /// <summary>
    /// Contiguity. A gap in a stream's ordinals is an unrecorded frame, which is exactly the loss this plane exists to
    /// make impossible — so the FIRST frame must be zero and every later one must have its predecessor.
    /// </summary>
    [Fact]
    public async Task Stream_ordinals_are_contiguous_from_zero()
    {
        var world = await SeedWorldAsync();
        var stream = Guid.NewGuid();

        (await WriteRecordAsync(world, record => { record.StreamId = stream; record.Ordinal = 1; }))
            .ShouldContain("ordinals are contiguous from zero", customMessage: "a stream that starts at one has already lost frame zero");

        (await WriteRecordAsync(world, record => { record.StreamId = stream; record.Ordinal = 0; })).ShouldBeEmpty();
        (await WriteRecordAsync(world, record => { record.StreamId = stream; record.Ordinal = 2; }))
            .ShouldContain("ordinals are contiguous from zero", customMessage: "a skipped ordinal is a frame nobody can prove was never captured");
        (await WriteRecordAsync(world, record => { record.StreamId = stream; record.Ordinal = 1; })).ShouldBeEmpty();

        // The unique index is the CONCURRENCY backstop: in one session the trigger sees the predecessor first, but two
        // appenders racing past their own snapshots each see it and neither sees the other's ordinal — only the index does.
        var definitions = await IndexDefinitionsAsync("workflow_run_native_record", "ux_workflow_run_native_record_ordinal");
        definitions.ShouldHaveSingleItem(
            customMessage: "ux_workflow_run_native_record_ordinal must exist after 0139 applies. Diagnose with: psql -c '\\di ux_workflow_run_native_record_ordinal'.");
        definitions[0].ShouldStartWith("CREATE UNIQUE", customMessage: "an index that is not UNIQUE rejects nothing, so two racing appenders both take the same ordinal");
        definitions[0].ShouldContain("(team_id, stream_id, ordinal)");
    }

    /// <summary>The payload XOR. Both arms and neither arm are the same defect from opposite sides: a reader can no longer tell which bytes are the frame.</summary>
    [Fact]
    public async Task A_frame_carries_exactly_one_payload_arm()
    {
        var world = await SeedWorldAsync();

        (await WriteRecordAsync(world, record => record.PayloadRefJson = "{\"artifactId\":\"x\"}"))
            .ShouldContain("ck_workflow_run_native_record_payload", customMessage: "two payload arms leave a reader no way to know which bytes are the frame");
        (await WriteRecordAsync(world, record => record.InlinePayload = null))
            .ShouldContain("ck_workflow_run_native_record_payload", customMessage: "a frame with no payload arm is indistinguishable from a frame that was empty");
        (await WriteRecordAsync(world, record => { record.InlinePayload = null; record.PayloadRefJson = "[]"; }))
            .ShouldContain("ck_workflow_run_native_record_payload", customMessage: "a reference must be an object; a bare array is not a resolvable content reference");
    }

    /// <summary>The redaction and normalization vocabularies, and the two rules hanging off them that a reader's trust rests on.</summary>
    [Fact]
    public async Task A_frame_cannot_claim_a_redaction_or_a_normalization_it_does_not_support()
    {
        var world = await SeedWorldAsync();

        (await WriteRecordAsync(world, record => record.Redaction = NativeRecordRedaction.Withheld))
            .ShouldContain("ck_workflow_run_native_record_redaction", customMessage: "a frame that was deliberately never captured cannot also present inline bytes");
        (await WriteRecordAsync(world, record => record.Normalization = NativeRecordNormalization.Failed))
            .ShouldContain("ck_workflow_run_native_record_normalization", customMessage: "'the parser threw' with no reason is a hole with a label on it");
        (await WriteRecordAsync(world, record => record.NormalizationErrorCode = "normalization.parser-threw"))
            .ShouldContain("ck_workflow_run_native_record_normalization", customMessage: "a reason on a frame that normalized fine makes the marker unreadable");
        (await WriteRecordAsync(world, record => record.Digest = "NOTAHEXDIGEST"))
            .ShouldContain("ck_workflow_run_native_record_digest", customMessage: "a digest that is not canonical cannot verify the bytes it is supposed to bind");
        (await WriteRecordAsync(world, record => record.NativeType = "   "))
            .ShouldContain("ck_workflow_run_native_record_bounds", customMessage: "a blank frame type makes 'which native classes are we losing' unanswerable");
    }

    /// <summary>
    /// The composite foreign key proves the execution's tenant and Agent Run; what it cannot prove is that the attempt
    /// named on the frame is a process OF THAT execution rather than of another one in the same run.
    /// </summary>
    [Fact]
    public async Task A_frame_must_name_a_process_attempt_of_its_own_execution()
    {
        var world = await SeedWorldAsync();

        (await WriteRecordAsync(world, record => record.AttemptId = Guid.NewGuid()))
            .ShouldContain("requires its tenant-bound process attempt");

        var second = await SeedExecutionAsync(world);
        var foreignAttempt = await SeedAttemptAsync(world, second);

        (await WriteRecordAsync(world, record => record.AttemptId = foreignAttempt.Id))
            .ShouldContain("attempt must belong to its own execution",
                customMessage: "a frame attributed to the wrong process makes per-attempt cost and per-attempt log geometry silently wrong");
    }

    /// <summary>
    /// The grounding rule, and the load-bearing case inside it. This table is deliberately STRICTER than
    /// <c>AgentSemanticEventV1.Validate()</c>, which tolerates an ungrounded event as long as it claims no exactness:
    /// here every event is a projection of a frame, so zero sources is never honest for any quality — which also
    /// refuses an Exact claim with no source record, the claim about nothing.
    /// </summary>
    [Theory]
    [InlineData(SemanticProjectionQuality.Exact)]
    [InlineData(SemanticProjectionQuality.Derived)]
    [InlineData(SemanticProjectionQuality.Unknown)]
    public async Task A_projection_with_no_source_frame_is_refused_whatever_it_claims(SemanticProjectionQuality quality)
    {
        var world = await SeedWorldAsync();

        (await WriteEventAsync(world, @event => { @event.SourceNativeRecordIds = Array.Empty<Guid>(); @event.ProjectionQuality = quality; }))
            .ShouldContain("ck_workflow_run_semantic_event_grounding",
                customMessage: $"a {quality} projection citing nothing has no frame anyone can check it against — and array_length of an empty array is NULL, which a CHECK reads as SATISFIED unless it is COALESCEd");
    }

    /// <summary>An array cannot carry a foreign key, so the guard IS the referential integrity of a projection's grounding.</summary>
    [Fact]
    public async Task A_projection_must_cite_frames_that_exist_in_its_own_execution()
    {
        var world = await SeedWorldAsync();
        var record = await SeedRecordAsync(world, ordinal: 0);

        (await WriteEventAsync(world, @event => @event.SourceNativeRecordIds = new[] { Guid.NewGuid() }))
            .ShouldContain("must cite native records of its own execution");
        (await WriteEventAsync(world, @event => @event.SourceNativeRecordIds = new[] { record.Id, Guid.NewGuid() }))
            .ShouldContain("must cite native records of its own execution",
                customMessage: "one real source must not launder a fabricated one — the count is compared, not merely tested for a hit");
        (await WriteEventAsync(world, @event => @event.SourceNativeRecordIds = new[] { Guid.Empty }))
            .ShouldContain("ck_workflow_run_semantic_event_grounding");

        var other = await SeedExecutionAsync(world);
        var foreignRecord = await SeedRecordAsync(world, ordinal: 0, execution: other);

        (await WriteEventAsync(world, @event => @event.SourceNativeRecordIds = new[] { foreignRecord.Id }))
            .ShouldContain("must cite native records of its own execution",
                customMessage: "a frame of another execution is not this projection's grounding, however real it is");
    }

    /// <summary>
    /// Exactness is a claim about BYTES, so it can never outrun the bytes actually captured. <c>NativeRecordV1</c>
    /// already refuses a Masked or Withheld frame carrying an Exact payload claim; a projection allowed to claim Exact
    /// over those same bytes would move the lie one table over rather than prevent it.
    /// </summary>
    [Theory]
    [InlineData(NativeRecordRedaction.None, SemanticProjectionQuality.Exact, null)]
    [InlineData(NativeRecordRedaction.None, SemanticProjectionQuality.RedactedExact, null)]
    [InlineData(NativeRecordRedaction.Masked, SemanticProjectionQuality.RedactedExact, null)]
    [InlineData(NativeRecordRedaction.Masked, SemanticProjectionQuality.Derived, null)]
    [InlineData(NativeRecordRedaction.Masked, SemanticProjectionQuality.Exact, "cannot claim")]
    [InlineData(NativeRecordRedaction.Withheld, SemanticProjectionQuality.Exact, "cannot claim")]
    [InlineData(NativeRecordRedaction.Withheld, SemanticProjectionQuality.RedactedExact, "cannot claim")]
    [InlineData(NativeRecordRedaction.Withheld, SemanticProjectionQuality.Heuristic, null)]
    public async Task An_exactly_grounded_projection_needs_sources_that_actually_survived_capture(NativeRecordRedaction redaction, SemanticProjectionQuality quality, string? expectedRefusal)
    {
        var world = await SeedWorldAsync();
        var record = await SeedRecordAsync(world, ordinal: 0, redaction: redaction);

        var refusal = await WriteEventAsync(world, @event =>
        {
            @event.SourceNativeRecordIds = new[] { record.Id };
            @event.ProjectionQuality = quality;
        });

        if (expectedRefusal is null) refusal.ShouldBeEmpty(customMessage: $"a {quality} projection over {redaction} bytes is honest and must be writable");
        else refusal.ShouldContain(expectedRefusal, customMessage: $"a {quality} projection over {redaction} bytes claims more than the bytes support");
    }

    /// <summary>
    /// Both tables are append-only. That is what makes the normalization marker trustworthy: it is decided at insert,
    /// so a parse failure can never be papered over by rewriting the frame to match a later reading — and a projection
    /// that changed its mind is a new event citing the same frames, leaving the old reading auditable.
    /// </summary>
    [Fact]
    public async Task Neither_a_frame_nor_a_projection_can_be_rewritten_or_deleted()
    {
        var world = await SeedWorldAsync();
        var record = await SeedRecordAsync(world, ordinal: 0);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunNativeRecord.SingleAsync(candidate => candidate.Id == record.Id);
            stored.Normalization = NativeRecordNormalization.Projected;
            stored.InlinePayload = "a tidier version of what the harness said";
            var rewritten = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            rewritten.InnerException?.Message.ShouldContain("append-only capture floor");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var deletion = await Should.ThrowAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM workflow_run_native_record WHERE id = {record.Id}"));
            deletion.Message.ShouldContain("append-only capture floor");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunSemanticEvent.Add(Event(world, record.Id));
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunSemanticEvent.SingleAsync(candidate => candidate.ExecutionId == world.ExecutionId);
            stored.ProjectionQuality = SemanticProjectionQuality.Exact;
            var relabelled = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            relabelled.InnerException?.Message.ShouldContain("append-only projection");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var deletion = await Should.ThrowAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM workflow_run_semantic_event WHERE execution_id = {world.ExecutionId}"));
            deletion.Message.ShouldContain("append-only projection");
        }
    }

    /// <summary>Reading a frame's projections back is a containment question over the source array, which only an inverted index answers — a b-tree on a UUID[] cannot serve it at all.</summary>
    [Fact]
    public async Task The_grounding_index_can_answer_which_projections_cite_a_frame()
    {
        var definitions = await IndexDefinitionsAsync("workflow_run_semantic_event", "ix_workflow_run_semantic_event_sources");

        definitions.ShouldHaveSingleItem(
            customMessage: "ix_workflow_run_semantic_event_sources must exist after 0139 applies — without it 'what was projected from this frame' is a sequential scan of every event ever recorded. Diagnose with: psql -c '\\d workflow_run_semantic_event'.");
        definitions[0].ShouldContain("USING gin", customMessage: "a b-tree over a UUID[] cannot answer a containment query, so the index would exist and never be used");
        definitions[0].ShouldContain("source_native_record_ids");
    }

    /// <summary>Offers one record write and reports the database's refusal, or empty when it was accepted — so a whole table of legal and illegal shapes reads as one line each. Catches broadly on purpose: the contiguity rule is a DEFERRED constraint, so its violation surfaces from the commit rather than from the insert.</summary>
    private async Task<string> WriteRecordAsync(World world, Action<WorkflowRunNativeRecord> forge)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var record = Record(world, ordinal: 0);
        forge(record);
        db.WorkflowRunNativeRecord.Add(record);

        return await RefusalOfAsync(db);
    }

    private async Task<string> WriteEventAsync(World world, Action<WorkflowRunSemanticEvent> forge)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var projection = Event(world, Guid.NewGuid());
        forge(projection);
        db.WorkflowRunSemanticEvent.Add(projection);

        return await RefusalOfAsync(db);
    }

    private static async Task<string> RefusalOfAsync(CodeSpaceDbContext db)
    {
        try
        {
            await db.SaveChangesAsync();
            return string.Empty;
        }
        catch (Exception refused)
        {
            return $"{refused.Message} {refused.InnerException?.Message}";
        }
    }

    private async Task<IReadOnlyList<string>> IndexDefinitionsAsync(string tableName, string indexName)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = @table AND indexname = @index", connection);
        command.Parameters.AddWithValue("table", tableName);
        command.Parameters.AddWithValue("index", indexName);
        await using var reader = await command.ExecuteReaderAsync();
        var definitions = new List<string>();
        while (await reader.ReadAsync()) definitions.Add(reader.GetString(0));
        return definitions;
    }

    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var agentRunId = Guid.NewGuid();
        const long fenceEpoch = 5;
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"native-record-{actorId:N}@test.local", Name = $"native-record-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"native-record-{teamId:N}", Name = "Native Record Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();

        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, TaskJson = "{}",
            FenceEpoch = fenceEpoch, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();

        var world = new World(teamId, agentRunId, fenceEpoch, Guid.Empty, Guid.Empty);
        var execution = await SeedExecutionAsync(world);
        var attempt = await SeedAttemptAsync(world, execution);

        return world with { ExecutionId = execution.Id, AttemptId = attempt.Id };
    }

    private async Task<WorkflowRunHarnessExecution> SeedExecutionAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var generation = 1 + await db.WorkflowRunHarnessExecution
            .CountAsync(candidate => candidate.TeamId == world.TeamId && candidate.AgentRunId == world.AgentRunId);
        var execution = new WorkflowRunHarnessExecution
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, AgentRunId = world.AgentRunId, Generation = generation,
            HarnessTypeKey = "codex-cli/v1", RunnerKind = "local", RunnerLocatorSchemaVersion = 1,
            State = HarnessExecutionState.Pending, AttemptCount = 0, NextAttemptOrdinal = 1, LeaseFence = 0,
            Revision = 1, CreatedAt = now, LastModifiedAt = now,
        };

        await CloseLivePredecessorsAsync(db, world, now);

        db.WorkflowRunHarnessExecution.Add(execution);
        await db.SaveChangesAsync();

        return execution;
    }

    /// <summary>
    /// 0137's generation gate refuses a new generation over a live predecessor, and its terminalize gate refuses a
    /// close while any attempt is still Running — so a second execution for the same run needs the attempts closed
    /// FIRST, in their own round trip, and then the execution Abandoned with a reason (the only close a generation
    /// whose processes were never observed exiting admits).
    /// </summary>
    private static async Task CloseLivePredecessorsAsync(CodeSpaceDbContext db, World world, DateTimeOffset now)
    {
        var live = await db.WorkflowRunHarnessExecution
            .Where(candidate => candidate.TeamId == world.TeamId && candidate.AgentRunId == world.AgentRunId
                && (candidate.State == HarnessExecutionState.Pending || candidate.State == HarnessExecutionState.Running))
            .ToListAsync();

        if (live.Count == 0) return;

        var liveIds = live.Select(execution => execution.Id).ToList();

        foreach (var attempt in await db.WorkflowRunHarnessProcessAttempt
            .Where(candidate => liveIds.Contains(candidate.ExecutionId) && candidate.State == HarnessProcessAttemptState.Running)
            .ToListAsync())
        {
            attempt.State = HarnessProcessAttemptState.Lost;
            attempt.ExitedAt = now;
            attempt.LastObservedAt = now;
            attempt.ErrorCode = "test.superseded";
            attempt.ErrorMessage = "closed so the next generation can open";
            attempt.Revision++;
            attempt.LastModifiedAt = now;
        }

        await db.SaveChangesAsync();

        foreach (var execution in live)
        {
            execution.State = HarnessExecutionState.Abandoned;
            execution.TerminalAt = now;
            execution.ErrorCode = "test.superseded";
            execution.ErrorMessage = "closed so the next generation can open";
            execution.Revision++;
            execution.LastModifiedAt = now;
        }

        await db.SaveChangesAsync();
    }

    private async Task<WorkflowRunHarnessProcessAttempt> SeedAttemptAsync(World world, WorkflowRunHarnessExecution execution)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var attempt = new WorkflowRunHarnessProcessAttempt
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, AgentRunId = world.AgentRunId, ExecutionId = execution.Id,
            AttemptOrdinal = 1, WorkerFenceEpoch = world.FenceEpoch, RunnerLocatorJson = "{\"spoolKey\":\"round-0\"}",
            State = HarnessProcessAttemptState.Running, ClaimFence = 0, Revision = 1,
            StartedAt = now, LastObservedAt = now, CreatedAt = now, LastModifiedAt = now,
        };

        db.WorkflowRunHarnessProcessAttempt.Add(attempt);
        await db.SaveChangesAsync();

        return attempt;
    }

    private async Task<WorkflowRunNativeRecord> SeedRecordAsync(World world, long ordinal, NativeRecordRedaction redaction = NativeRecordRedaction.None, WorkflowRunHarnessExecution? execution = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var record = Record(world, ordinal);

        if (execution is not null)
        {
            record.ExecutionId = execution.Id;
            record.AttemptId = (await SeedAttemptAsync(world, execution)).Id;
        }

        // A withheld frame has metadata only, so its payload must be a reference to unavailable content.
        record.Redaction = redaction;
        if (redaction == NativeRecordRedaction.Withheld)
        {
            record.InlinePayload = null;
            record.PayloadRefJson = "{\"completeness\":\"Unavailable\"}";
        }

        db.WorkflowRunNativeRecord.Add(record);
        await db.SaveChangesAsync();

        return record;
    }

    private static WorkflowRunNativeRecord Record(World world, long ordinal)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkflowRunNativeRecord
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, AgentRunId = world.AgentRunId, ExecutionId = world.ExecutionId,
            AttemptId = world.AttemptId, StreamId = Guid.NewGuid(), Ordinal = ordinal,
            Channel = NativeRecordChannel.Stdout, NativeType = "assistant", IngestedAt = now,
            SourceOffsetBytes = ordinal * 8, SourceLengthBytes = 7, InlinePayload = "{\"type\":\"assistant\"}",
            DigestAlgorithm = WorkflowRunDataContract.Sha256Algorithm, Digest = CanonicalDigest, SizeBytes = 20,
            PayloadEncoding = NativeRecordPayloadEncoding.Utf8, Redaction = NativeRecordRedaction.None, IsFinal = true,
            Normalization = NativeRecordNormalization.Projected,
            ContractVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now,
        };
    }

    private static WorkflowRunSemanticEvent Event(World world, Guid sourceRecordId)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkflowRunSemanticEvent
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, AgentRunId = world.AgentRunId, ExecutionId = world.ExecutionId,
            SourceNativeRecordIds = new[] { sourceRecordId },
            EventType = "https://codespace.dev/agent/v1/assistant-message", EventSchemaVersion = 1,
            Necessity = SemanticEventNecessity.Ignorable, ProjectionQuality = SemanticProjectionQuality.Derived,
            ContractVersion = WorkflowRunDataContract.CurrentVersion, ProjectedAt = now, CreatedAt = now,
        };
    }

    private sealed record World(Guid TeamId, Guid AgentRunId, long FenceEpoch, Guid ExecutionId, Guid AttemptId);
}
