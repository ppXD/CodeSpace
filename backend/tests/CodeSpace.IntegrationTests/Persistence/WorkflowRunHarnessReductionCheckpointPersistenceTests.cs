using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Reduction;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres proof that a reduction checkpoint cannot lie about the prefix it consumed, and that a resurrected
/// reducer cannot REWIND one. Most assertions are COUNTER-EXAMPLES: the illegal row is offered and the database refuses
/// it, because an invariant that only holds while every writer remembers it is not an invariant.
///
/// <para>A few assert an ACCEPTANCE instead, and deliberately. The schema cannot authenticate a writer — a row trigger
/// sees OLD and NEW, never which session sent them — so a displaced reducer's advance over a frontier that is not
/// behind IS accepted here, and only the writer's own predicate refuses it. That boundary is asserted rather than left
/// unsaid, so the wiring slice reads what this row actually guarantees instead of what a wall of refusals implies.</para>
///
/// <para>The payloads are serialized from the SAME contract records the reducer produces, through the same
/// <see cref="AgentJson"/> options, so these tests also pin the three JSON keys the guard cross-checks. Hand-written
/// JSON would pass here and leave the guard reading fields that production never writes.</para>
///
/// <para>Nothing reads or writes this table in production yet, so these teeth are the entire contract the wiring slice
/// will build on.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunHarnessReductionCheckpointPersistenceTests
{
    private static readonly Guid PrimaryStreamId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProtocolStreamId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly PostgresFixture _fixture;

    public WorkflowRunHarnessReductionCheckpointPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_checkpoint_is_born_unclaimed_at_revision_one_and_advances_its_frontier_forward()
    {
        var world = await SeedWorldAsync();
        var checkpoint = await SeedCheckpointAsync(world);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessReductionCheckpoint.SingleAsync(candidate => candidate.Id == checkpoint.Id);
            stored.Revision.ShouldBe(1);
            stored.ReducerFence.ShouldBe(0);
            stored.ReducerOwnerId.ShouldBeNull();
            stored.RecordsConsumed.ShouldBe(0);
        }

        (await AdvanceAsync(checkpoint, (PrimaryStreamId, 4L))).ShouldBeEmpty();
        (await AdvanceAsync(checkpoint, (PrimaryStreamId, 7L), (ProtocolStreamId, 3L))).ShouldBeEmpty();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessReductionCheckpoint.SingleAsync(candidate => candidate.Id == checkpoint.Id);
            stored.RecordsConsumed.ShouldBe(10, customMessage: "a stream at nextOrdinal k accounts for exactly k zero-based records");
            stored.Revision.ShouldBe(3);
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessReductionCheckpoint.SingleAsync(candidate => candidate.Id == checkpoint.Id);
            db.Remove(stored);
            var deleted = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            deleted.InnerException?.Message.ShouldContain("durable reduction state — DELETE rejected");
        }
    }

    /// <summary>
    /// The invariant the lane exists for. The consumed count is stated three ways — the frontier's own sum, the column,
    /// and the reduced state's own field — and no write may leave any two of them disagreeing, so a checkpoint cannot
    /// claim a position nothing folded.
    /// </summary>
    [Fact]
    public async Task A_checkpoint_cannot_claim_a_position_it_has_not_consumed()
    {
        var world = await SeedWorldAsync();
        var checkpoint = await SeedCheckpointAsync(world);

        (await MutateAsync(checkpoint, stored =>
        {
            stored.PositionJson = Position((PrimaryStreamId, 9L));   // frontier moved, count left behind
        })).ShouldContain("cannot claim a position it has not consumed");

        (await MutateAsync(checkpoint, stored =>
        {
            stored.RecordsConsumed = 9;                              // count moved, frontier left behind
            stored.ReducedStateJson = State(9);
        })).ShouldContain("cannot claim a position it has not consumed");

        (await MutateAsync(checkpoint, stored =>
        {
            stored.PositionJson = Position((PrimaryStreamId, 9L));
            stored.RecordsConsumed = 9;
            stored.ReducedStateJson = State(4);                      // the state reduced a different prefix
        })).ShouldContain("must state the exact count it reduced");

        (await MutateAsync(checkpoint, stored =>
        {
            stored.PositionJson = Position((PrimaryStreamId, 4L), (PrimaryStreamId, 5L));
            stored.RecordsConsumed = 9;
            stored.ReducedStateJson = State(9);
        })).ShouldContain("must be distinct per-stream zero-based frontiers");

        (await MutateAsync(checkpoint, stored =>
        {
            stored.PositionJson = $"{{\"streams\":[{{\"streamId\":\"{PrimaryStreamId:D}\",\"nextOrdinal\":\"9\"}}]}}";
            stored.RecordsConsumed = 9;
            stored.ReducedStateJson = State(9);
        })).ShouldContain("must be distinct per-stream zero-based frontiers");

        // A MISSING key is the trap the file's own inner guards were written against: jsonb_typeof() answers SQL NULL,
        // an OR chain of NULLs is NULL, `IF NULL THEN` is false, and a CHECK that evaluates to NULL is SATISFIED — so a
        // `<>`/`=` comparison would have let a position with no frontier at all through and totalled it as zero.
        (await MutateAsync(checkpoint, stored => stored.PositionJson = "{}"))
            .ShouldContain("must be distinct per-stream zero-based frontiers", customMessage: "a position with no streams key is unreadable in-process — HarnessReductionPosition.Streams is required, so the row would throw on deserialize");
        (await MutateAsync(checkpoint, stored => stored.PositionJson = "{\"frontier\":[]}"))
            .ShouldContain("must be distinct per-stream zero-based frontiers");
        (await MutateAsync(checkpoint, stored => stored.PositionJson = "{\"streams\":{}}"))
            .ShouldContain("must be distinct per-stream zero-based frontiers");
    }

    /// <summary>
    /// The stored state must carry its own prefix witness and its own contract version. Without the witness a state
    /// that reduced a DIFFERENT prefix is indistinguishable from this one, and a tail-only fold could be stored as a
    /// whole-prefix fold with nothing able to tell.
    /// </summary>
    [Fact]
    public async Task A_reduced_state_without_its_witness_or_its_own_version_is_refused()
    {
        var world = await SeedWorldAsync();
        var checkpoint = await SeedCheckpointAsync(world);

        (await MutateAsync(checkpoint, stored => stored.ReducedStateJson = StateWithout("prefixDigest")))
            .ShouldContain("must carry a canonical prefix digest");

        (await MutateAsync(checkpoint, stored => stored.ReducedStateJson = State(0).Replace(Digest(0), "NOTLOWERCASEHEX", StringComparison.Ordinal)))
            .ShouldContain("must carry a canonical prefix digest");

        (await MutateAsync(checkpoint, stored => stored.ReducedStateJson = StateWithout("recordsConsumed")))
            .ShouldContain("must state the exact count it reduced");

        (await MutateAsync(checkpoint, stored => stored.ReducedStateJson = StateWithout("contractVersion")))
            .ShouldContain("must carry its own contract version");
    }

    [Fact]
    public async Task The_frontier_is_monotonic_per_stream_and_no_stream_may_leave_it()
    {
        var world = await SeedWorldAsync();
        var checkpoint = await SeedCheckpointAsync(world);

        (await AdvanceAsync(checkpoint, (PrimaryStreamId, 7L), (ProtocolStreamId, 3L))).ShouldBeEmpty();

        (await AdvanceAsync(checkpoint, (PrimaryStreamId, 6L), (ProtocolStreamId, 3L)))
            .ShouldContain("frontier is monotonic per stream");

        (await AdvanceAsync(checkpoint, (PrimaryStreamId, 7L)))
            .ShouldContain("no stream may leave it", customMessage: "dropping a stream would silently re-open the records it had already folded");

        // Gaining a stream is legitimate: a channel may open at any point in an execution.
        (await AdvanceAsync(checkpoint, (PrimaryStreamId, 7L), (ProtocolStreamId, 3L), (Guid.Parse("77777777-7777-7777-7777-777777777777"), 2L))).ShouldBeEmpty();
    }

    /// <summary>
    /// The claim arm enters on EITHER axis. A guard that entered on the fence alone would let an owner swap that leaves
    /// the fence untouched take a live lease, which is precisely the bypass 0132's and 0137's arms exist to close.
    /// </summary>
    [Fact]
    public async Task A_live_reducer_lease_cannot_be_taken_on_either_axis()
    {
        var world = await SeedWorldAsync();
        var checkpoint = await SeedCheckpointAsync(world);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        (await ClaimAsync(checkpoint, first, fence: 1)).ShouldBeEmpty();

        (await ClaimAsync(checkpoint, second, fence: 2)).ShouldContain("live reducer lease cannot be reclaimed");
        (await MutateAsync(checkpoint, stored =>
        {
            stored.ReducerOwnerId = second;                          // owner axis only — the fence is untouched
        })).ShouldContain("live reducer lease cannot be reclaimed");

        (await LapseAsync(checkpoint)).ShouldBeEmpty();

        (await MutateAsync(checkpoint, stored =>
        {
            stored.ReducerOwnerId = second;
            stored.ReducerLeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        })).ShouldContain("must advance the fence exactly once",
            customMessage: "an owner swap with an untouched fence must be refused even once the lease has lapsed — two owners would otherwise share one fence value");

        (await ClaimAsync(checkpoint, second, fence: 3)).ShouldContain("must advance the fence exactly once");
        (await ClaimAsync(checkpoint, second, fence: 2, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1))).ShouldContain("with a live expiry");

        (await MutateAsync(checkpoint, stored =>
        {
            stored.ReducerOwnerId = second;
            stored.ReducerFence = 2;
            stored.ReducerLeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            stored.PositionJson = Position((PrimaryStreamId, 5L));
            stored.RecordsConsumed = 5;
            stored.ReducedStateJson = State(5);
        })).ShouldContain("claim cannot move the reduction");

        (await ClaimAsync(checkpoint, second, fence: 2)).ShouldBeEmpty();
    }

    /// <summary>
    /// The resurrected-reducer boundary, recorded where it actually is. The database refuses a displaced reducer that
    /// would REWIND the row to its own older prefix, and it refuses one that tries to take the lease back. It does NOT
    /// refuse an advance whose frontier is merely not behind: a row trigger sees OLD and NEW, never which session sent
    /// them, so that write is accepted and only the writer's own predicate can fail it. The acceptance is asserted
    /// rather than left unsaid, because a suite that only shows refusals implies a guarantee this schema does not hold.
    /// </summary>
    [Fact]
    public async Task A_displaced_reducer_cannot_rewind_the_row_and_the_schema_alone_cannot_refuse_its_advance()
    {
        var world = await SeedWorldAsync();
        var checkpoint = await SeedCheckpointAsync(world);
        var displaced = Guid.NewGuid();
        var holder = Guid.NewGuid();

        (await ClaimAsync(checkpoint, displaced, fence: 1)).ShouldBeEmpty();
        (await AdvanceAsync(checkpoint, (PrimaryStreamId, 6L))).ShouldBeEmpty();

        (await LapseAsync(checkpoint)).ShouldBeEmpty();
        (await ClaimAsync(checkpoint, holder, fence: 2)).ShouldBeEmpty();

        // Taking the row back rather than advancing it is a CLAIM on the owner axis, and the live lease refuses it.
        (await MutateAsync(checkpoint, stored =>
        {
            stored.ReducerOwnerId = displaced;
            stored.PositionJson = Position((PrimaryStreamId, 9L));
            stored.RecordsConsumed = 9;
            stored.ReducedStateJson = State(9);
        })).ShouldContain("live reducer lease cannot be reclaimed");

        (await AdvanceAsync(checkpoint, (PrimaryStreamId, 4L)))
            .ShouldContain("frontier is monotonic per stream", customMessage: "the displaced reducer's own shorter prefix must not be writable over the longer one the holder stored");

        (await AdvanceAsync(checkpoint, (PrimaryStreamId, 9L))).ShouldBeEmpty(
            customMessage: "an advance that carries no holder predicate is accepted from any session — the wiring slice owes WHERE reducer_owner_id = @me AND reducer_fence = @observed AND revision = @observed. If this ever starts failing, the schema grew a holder check and its header must say so.");
    }

    [Fact]
    public async Task A_release_hands_back_the_lease_and_nothing_else()
    {
        var world = await SeedWorldAsync();
        var checkpoint = await SeedCheckpointAsync(world);
        var owner = Guid.NewGuid();

        (await ClaimAsync(checkpoint, owner, fence: 1)).ShouldBeEmpty();

        (await MutateAsync(checkpoint, stored =>
        {
            stored.ReducerOwnerId = null;
            stored.ReducerLeaseExpiresAt = null;
            stored.PositionJson = Position((PrimaryStreamId, 2L));
            stored.RecordsConsumed = 2;
            stored.ReducedStateJson = State(2);
        })).ShouldContain("release hands back the lease only");

        (await MutateAsync(checkpoint, stored =>
        {
            stored.ReducerOwnerId = null;
            stored.ReducerLeaseExpiresAt = null;
        })).ShouldBeEmpty();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var released = await db.WorkflowRunHarnessReductionCheckpoint.SingleAsync(candidate => candidate.Id == checkpoint.Id);
        released.ReducerFence.ShouldBe(1, customMessage: "a release leaves the fence where it is; only an acquisition moves it");
    }

    [Fact]
    public async Task Stable_reduction_identity_is_immutable_and_revision_advances_exactly_once()
    {
        var world = await SeedWorldAsync();
        var checkpoint = await SeedCheckpointAsync(world);
        var sibling = await SeedExecutionAsync(world, generation: 2);

        (await MutateAsync(checkpoint, stored => stored.ExecutionId = sibling.Id)).ShouldContain("stable reduction identity is immutable");
        (await MutateAsync(checkpoint, stored => stored.ReducerKind = "harness-prefix/v2")).ShouldContain("stable reduction identity is immutable");
        // The state's own contractVersion is cross-checked BEFORE the identity arm runs, so this forgery has to move
        // both copies — moving the column alone would prove the cross-check fires, not that identity is immutable.
        (await MutateAsync(checkpoint, stored =>
        {
            stored.ContractVersion = 2;
            stored.ReducedStateJson = State(0).Replace("\"contractVersion\":1", "\"contractVersion\":2", StringComparison.Ordinal);
        })).ShouldContain("stable reduction identity is immutable");
        (await MutateAsync(checkpoint, stored => stored.CreatedAt = stored.CreatedAt.AddSeconds(-1))).ShouldContain("stable reduction identity is immutable");

        (await MutateAsync(checkpoint, stored => stored.Revision = stored.Revision + 4)).ShouldContain("revision must advance exactly once");
        (await MutateAsync(checkpoint, stored => stored.Revision = stored.Revision - 1)).ShouldContain("revision must advance exactly once");
        (await MutateAsync(checkpoint, stored => stored.LastModifiedAt = stored.CreatedAt.AddMinutes(-5))).ShouldContain("time must not rewind");
    }

    [Fact]
    public async Task One_reduction_per_execution_and_kind_and_a_forged_birth_is_refused()
    {
        var world = await SeedWorldAsync();
        var execution = await SeedExecutionAsync(world, generation: 1);
        await InsertAsync(Checkpoint(world, execution));

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunHarnessReductionCheckpoint.Add(Checkpoint(world, execution));
            var duplicate = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            duplicate.InnerException?.Message.ShouldContain("ux_workflow_run_harness_reduction_checkpoint_reducer");
        }

        var second = Checkpoint(world, execution);
        second.ReducerKind = "harness-cost/v1";
        (await InsertAsync(second)).ShouldBeEmpty(customMessage: "a second reduction over the same execution is its own row, never a fight over one");

        var claimedAtBirth = Checkpoint(world, execution);
        claimedAtBirth.ReducerKind = "harness-tools/v1";
        claimedAtBirth.ReducerOwnerId = Guid.NewGuid();
        claimedAtBirth.ReducerFence = 1;
        claimedAtBirth.ReducerLeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        (await InsertAsync(claimedAtBirth)).ShouldContain("must start as an unclaimed revision-one row");

        var orphan = Checkpoint(world, execution);
        orphan.ReducerKind = "harness-orphan/v1";
        orphan.AgentRunId = Guid.NewGuid();
        (await InsertAsync(orphan)).ShouldContain("fk_workflow_run_harness_reduction_checkpoint_execution",
            customMessage: "the denormalized Agent Run id is proved through the execution's scope key, never trusted");
    }

    /// <summary>Moves the frontier and the state together, and touches NOTHING else — exactly the columns EF puts in the SET list for a writer that folded more records.</summary>
    private async Task<string> AdvanceAsync(WorkflowRunHarnessReductionCheckpoint checkpoint, params (Guid StreamId, long NextOrdinal)[] streams)
    {
        var total = streams.Sum(stream => stream.NextOrdinal);

        return await MutateAsync(checkpoint, stored =>
        {
            stored.PositionJson = Position(streams);
            stored.RecordsConsumed = total;
            stored.ReducedStateJson = State(total);
        });
    }

    private async Task<string> ClaimAsync(WorkflowRunHarnessReductionCheckpoint checkpoint, Guid owner, long fence, DateTimeOffset? expiresAt = null) =>
        await MutateAsync(checkpoint, stored =>
        {
            stored.ReducerOwnerId = owner;
            stored.ReducerFence = fence;
            stored.ReducerLeaseExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5);
        });

    /// <summary>Expires the current lease in place — a same-owner, same-fence write, which is a degenerate advance rather than a claim.</summary>
    private async Task<string> LapseAsync(WorkflowRunHarnessReductionCheckpoint checkpoint) =>
        await MutateAsync(checkpoint, stored => stored.ReducerLeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1));

    /// <summary>Applies <paramref name="forge"/> over a freshly read row with revision and time already advanced legally, so a test states only what it is actually forging. Returns the refusal message, or empty on success.</summary>
    private async Task<string> MutateAsync(WorkflowRunHarnessReductionCheckpoint checkpoint, Action<WorkflowRunHarnessReductionCheckpoint> forge)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var stored = await db.WorkflowRunHarnessReductionCheckpoint.SingleAsync(candidate => candidate.Id == checkpoint.Id);
        stored.Revision++;
        stored.LastModifiedAt = DateTimeOffset.UtcNow;
        forge(stored);

        try
        {
            await db.SaveChangesAsync();
            return string.Empty;
        }
        catch (DbUpdateException refused)
        {
            return refused.InnerException?.Message ?? refused.Message;
        }
    }

    private async Task<string> InsertAsync(WorkflowRunHarnessReductionCheckpoint checkpoint)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunHarnessReductionCheckpoint.Add(checkpoint);

        try
        {
            await db.SaveChangesAsync();
            return string.Empty;
        }
        catch (DbUpdateException refused)
        {
            return refused.InnerException?.Message ?? refused.Message;
        }
    }

    private async Task<WorkflowRunHarnessReductionCheckpoint> SeedCheckpointAsync(World world)
    {
        var execution = await SeedExecutionAsync(world, generation: 1);
        var checkpoint = Checkpoint(world, execution);

        (await InsertAsync(checkpoint)).ShouldBeEmpty();
        return checkpoint;
    }

    private async Task<WorkflowRunHarnessExecution> SeedExecutionAsync(World world, int generation)
    {
        var now = DateTimeOffset.UtcNow;
        var execution = new WorkflowRunHarnessExecution
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, AgentRunId = world.AgentRunId, Generation = generation,
            HarnessTypeKey = "codex-cli/v2", RunnerKind = "local", RunnerLocatorSchemaVersion = 1,
            State = HarnessExecutionState.Pending, AttemptCount = 0, NextAttemptOrdinal = 1, LeaseFence = 0,
            Revision = 1, CreatedAt = now, LastModifiedAt = now,
        };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        if (generation > 1)
        {
            var previous = await db.WorkflowRunHarnessExecution
                .Where(candidate => candidate.TeamId == world.TeamId && candidate.AgentRunId == world.AgentRunId)
                .OrderByDescending(candidate => candidate.Generation).FirstAsync();
            previous.State = HarnessExecutionState.Abandoned;
            previous.TerminalAt = now;
            previous.ErrorCode = "reduction-test.superseded";
            previous.Revision++;
            previous.LastModifiedAt = now;
            await db.SaveChangesAsync();
        }

        db.WorkflowRunHarnessExecution.Add(execution);
        await db.SaveChangesAsync();
        return execution;
    }

    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var agentRunId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"harness-reduce-{actorId:N}@test.local", Name = $"harness-reduce-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"harness-reduce-{teamId:N}", Name = "Harness Reduction Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();

        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, TaskJson = "{}",
            FenceEpoch = 7, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();
        return new World(teamId, agentRunId);
    }

    private static WorkflowRunHarnessReductionCheckpoint Checkpoint(World world, WorkflowRunHarnessExecution execution)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowRunHarnessReductionCheckpoint
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, AgentRunId = world.AgentRunId, ExecutionId = execution.Id,
            ReducerKind = HarnessReductionFold.ReducerKind, ContractVersion = WorkflowRunDataContract.CurrentVersion,
            PositionJson = Position(), RecordsConsumed = 0, ReducedStateJson = State(0),
            ReducerFence = 0, Revision = 1, CreatedAt = now, LastModifiedAt = now,
        };
    }

    /// <summary>Serialized from the real contract record, so these tests pin the exact JSON the guard cross-checks rather than a hand-written imitation of it.</summary>
    private static string Position(params (Guid StreamId, long NextOrdinal)[] streams)
    {
        var position = new HarnessReductionPosition
        {
            Streams = streams.Select(stream => new HarnessStreamPosition { StreamId = stream.StreamId, NextOrdinal = stream.NextOrdinal }).ToArray(),
        };

        return JsonSerializer.Serialize(position, AgentJson.Options);
    }

    private static string State(long recordsConsumed)
    {
        var state = new HarnessReducedStateV1
        {
            ContractVersion = WorkflowRunDataContract.CurrentVersion,
            RecordsConsumed = recordsConsumed,
            ProjectionsConsumed = 0,
            ExactlyGroundedProjections = 0,
            RequiredProjections = 0,
            ChannelsSeen = recordsConsumed == 0 ? Array.Empty<NativeRecordChannel>() : new[] { NativeRecordChannel.Stdout },
            RedactedByteCount = 0,
            PrefixDigest = Digest(recordsConsumed),
        };

        state.Validate().ShouldBeEmpty();
        return JsonSerializer.Serialize(state, AgentJson.Options);
    }

    private static string StateWithout(string property)
    {
        using var document = JsonDocument.Parse(State(0));
        var kept = document.RootElement.EnumerateObject().Where(member => member.Name != property);

        return $"{{{string.Join(",", kept.Select(member => $"\"{member.Name}\":{member.Value.GetRawText()}"))}}}";
    }

    private static string Digest(long recordsConsumed) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"prefix:{recordsConsumed}"))).ToLowerInvariant();

    private sealed record World(Guid TeamId, Guid AgentRunId);
}
