using System.Diagnostics;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

public sealed partial class LegacyPlacementAdoptionTests
{
    [Fact]
    public async Task A_single_object_larger_than_the_byte_budget_still_advances_as_one_honest_oversize_pass()
    {
        var world = await SeedAsync(Candidate(new string('x', 4096)), Candidate(new string('y', 4096)));

        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
            new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 2, null)
            {
                ByteBudget = 32,
                TimeBudget = TimeSpan.FromMinutes(1),
            }, CancellationToken.None);

        result.Examined.ShouldBe(1, "the first member is the no-starvation escape hatch; a second member cannot ride its oversize pass");
        result.OversizedItem.ShouldBeTrue();
        result.YieldReason.ShouldBe(LegacyPlacementAdoptionYieldReasonValue.ByteBudget);
        result.Progress.ShouldNotBeNull().EvidenceExamined.ShouldBe(1);
        result.Progress.MemberCount.ShouldBe(2);
        result.NextCursor.ShouldNotBeNull();

        var audit = await scope.Resolve<CodeSpaceDbContext>().LegacyPlacementAdoptionPassAudit.AsNoTracking()
            .SingleAsync(value => value.Arc.TeamId == world.TeamId);
        audit.Outcome.ShouldBe(LegacyPlacementAdoptionPassOutcome.Advanced);
        audit.YieldReason.ShouldBe(LegacyPlacementAdoptionYieldReason.ByteBudget);
        audit.OversizedItem.ShouldBeTrue();
        audit.Examined.ShouldBe(1);
    }

    [Fact]
    public async Task Terminal_replay_carries_the_whole_arc_not_only_the_last_page_and_matches_append_only_pass_audit()
    {
        var world = await SeedAsync(Candidate("available"), Candidate("missing", LegacyShape.Missing),
            Candidate("corrupt!", LegacyShape.SameLengthCorrupt));
        LegacyPlacementAdoptionSummary result = await AdoptAsync(world, batchSize: 1);
        var replayCursor = result.NextCursor;
        while (result.NextCursor != null) result = await AdoptAsync(world, result.NextCursor, batchSize: 1);

        var progress = result.Progress.ShouldNotBeNull();
        progress.MemberCount.ShouldBe(3);
        progress.EvidenceExamined.ShouldBe(3);
        progress.EvidenceResolved.ShouldBe(3);
        progress.MintExamined.ShouldBe(3);
        progress.Available.ShouldBe(1);
        progress.Missing.ShouldBe(1);
        progress.Corrupt.ShouldBe(1);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var arc = await db.LegacyPlacementAdoptionArc.AsNoTracking().SingleAsync(value => value.TeamId == world.TeamId);
        var audits = await db.LegacyPlacementAdoptionPassAudit.AsNoTracking().Where(value => value.ArcId == arc.Id).ToListAsync();
        audits.Select(value => value.ClaimToken).Distinct().Count().ShouldBe(audits.Count,
            "one claimed pass can append at most one audit row, including abort/retry paths");
        audits.Sum(value => value.EvidenceExaminedDelta).ShouldBe(progress.EvidenceExamined);
        audits.Sum(value => value.MintExaminedDelta).ShouldBe(progress.MintExamined);
        (await AdoptAsync(world, replayCursor)).Progress.ShouldBe(progress,
            "the terminal tombstone must replay the same cumulative audit after the final response is lost");
    }

    [Fact]
    public async Task Expected_transport_failure_is_retryable_but_a_programming_exception_releases_the_claim_and_rethrows()
    {
        var transient = await SeedAsync(Candidate("transport retry"));
        using (var scope = DecoratingScope(lease => new ThrowingHeadDriver(lease, new IOException("must-not-persist"))))
        {
            var retry = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(transient.TeamId, transient.ActorId, transient.ProfileId, 1, null), CancellationToken.None);
            retry.Retryable.ShouldBe(1);
            retry.NextCursor.ShouldNotBeNull();
        }

        var programming = await SeedAsync(Candidate("programming failure"));
        using (var scope = DecoratingScope(lease => new ThrowingHeadDriver(lease, new InvalidOperationException("secret-bug-detail"))))
        {
            await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(programming.TeamId, programming.ActorId, programming.ProfileId, 1, null), CancellationToken.None)
                .ShouldThrowAsync<InvalidOperationException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var audit = await db.LegacyPlacementAdoptionPassAudit.AsNoTracking()
                .Where(value => value.Arc.TeamId == programming.TeamId).SingleAsync();
            audit.Outcome.ShouldBe(LegacyPlacementAdoptionPassOutcome.Interrupted);
            audit.FailureCode.ShouldBe(LegacyPlacementAdoptionPassFailureCode.ProgrammingFault);
            (await db.LegacyPlacementAdoptionArc.AsNoTracking().SingleAsync(value => value.TeamId == programming.TeamId))
                .ClaimToken.ShouldBeNull("unexpected code faults rethrow only after an exception-safe claim release");
            System.Text.Json.JsonSerializer.Serialize(audit).ShouldNotContain("secret-bug-detail");
        }
    }

    [Fact]
    public async Task A_long_pass_renews_one_claim_with_revision_carry_forward_without_fabricating_per_renewal_audit_rows()
    {
        var world = await SeedAsync(Candidate(new string('r', 256 * 1024)));
        var clock = new ManualTimeProvider();
        using var scope = RuntimeScope(clock, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10),
            lease => new AdvancingDriver(lease, clock, TimeSpan.FromSeconds(2)));

        var result = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
            new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);

        result.AdoptionAdmissible.ShouldBeTrue();
        result.Progress.ShouldNotBeNull().CompletedPasses.ShouldBe(1);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var arc = await db.LegacyPlacementAdoptionArc.AsNoTracking().SingleAsync(value => value.TeamId == world.TeamId);
        arc.Revision.ShouldBeGreaterThanOrEqualTo(6, "more than one renewal must be carried into the same mutable in-memory claim fence");
        arc.ClaimToken.ShouldBeNull();
        (await db.LegacyPlacementAdoptionPassAudit.AsNoTracking().CountAsync(value => value.ArcId == arc.Id)).ShouldBe(1,
            "renewal extends one claimed pass and must never become one audit row per heartbeat");
    }

    [Fact]
    public async Task Elapsed_time_is_a_between_objects_budget_and_the_first_object_still_advances()
    {
        var world = await SeedAsync(Candidate("first timed object"), Candidate("second timed object"));
        var clock = new ManualTimeProvider();
        using var scope = RuntimeScope(clock, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10),
            lease => new AdvancingDriver(lease, clock, TimeSpan.FromSeconds(2)));

        var result = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
            new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 2, null)
            {
                ByteBudget = LegacyPlacementAdoptionLimits.MaxBytesPerPass,
                TimeBudget = TimeSpan.FromSeconds(1),
            }, CancellationToken.None);

        result.Examined.ShouldBe(1);
        result.YieldReason.ShouldBe(LegacyPlacementAdoptionYieldReasonValue.TimeBudget);
        result.NextCursor.ShouldNotBeNull();
        result.Progress.ShouldNotBeNull().EvidenceExamined.ShouldBe(1);
    }

    [Fact]
    public async Task A_provider_operation_that_ignores_cancellation_yields_before_the_claim_lease_and_is_cleaned_late()
    {
        var world = await SeedAsync(Candidate("bounded hung provider"));
        var gate = new BlockingHead();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var scope = RuntimeScope(TimeProvider.System, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(75),
                lease => new BlockingHeadDriver(lease, gate));
            var result = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            result.Retryable.ShouldBe(1);
            result.YieldReason.ShouldBe(LegacyPlacementAdoptionYieldReasonValue.ProviderRetryable);
            result.NextCursor.ShouldNotBeNull();
            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
            var audit = await scope.Resolve<CodeSpaceDbContext>().LegacyPlacementAdoptionPassAudit.AsNoTracking()
                .SingleAsync(value => value.Arc.TeamId == world.TeamId);
            audit.FailureCode.ShouldBe(LegacyPlacementAdoptionPassFailureCode.ProviderTransient);
            audit.EndPosition.ShouldBe(audit.StartPosition, "a timed-out provider cannot advance the closed manifest");
        }
        finally
        {
            gate.Continue.TrySetResult();
        }
    }

    [Fact]
    public async Task A_typed_nonretryable_provider_exception_aborts_without_exposing_provider_text()
    {
        var world = await SeedAsync(Candidate("provider rejects this pass"));
        using (var scope = DecoratingScope(lease => new ThrowingHeadDriver(lease, new RejectedStorageException())))
        {
            var result = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);
            result.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.ProviderRejected);
            result.NextCursor.ShouldBeNull();
        }

        using var read = _fixture.BeginScope();
        var audit = await read.Resolve<CodeSpaceDbContext>().LegacyPlacementAdoptionPassAudit.AsNoTracking()
            .SingleAsync(value => value.Arc.TeamId == world.TeamId);
        audit.Outcome.ShouldBe(LegacyPlacementAdoptionPassOutcome.Aborted);
        audit.FailureCode.ShouldBe(LegacyPlacementAdoptionPassFailureCode.ProviderRejected);
        System.Text.Json.JsonSerializer.Serialize(audit).ShouldNotContain("credential-value");
    }

    [Fact]
    public async Task An_empty_manifest_has_an_exact_zero_progress_tombstone()
    {
        var world = await SeedAsync();

        var result = await AdoptAsync(world);

        result.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.AdmissionEvidenceMissing);
        var progress = result.Progress.ShouldNotBeNull();
        progress.MemberCount.ShouldBe(0);
        progress.CompletedPasses.ShouldBe(0);
    }

    [Fact]
    public async Task Pass_audit_is_append_only_and_can_leave_only_with_its_parent_tombstone()
    {
        var world = await SeedAsync(Candidate("append only audit"));
        var result = await AdoptAsync(world);
        result = await AdoptAsync(world, result.NextCursor);
        result.NextCursor.ShouldBeNull();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var arcId = await db.LegacyPlacementAdoptionArc.AsNoTracking().Where(value => value.TeamId == world.TeamId)
            .Select(value => value.Id).SingleAsync();
        await Should.ThrowAsync<Exception>(() => db.LegacyPlacementAdoptionPassAudit.Where(value => value.ArcId == arcId).ExecuteDeleteAsync());
        db.ChangeTracker.Clear();
        (await db.LegacyPlacementAdoptionArc.Where(value => value.Id == arcId).ExecuteDeleteAsync()).ShouldBe(1,
            "the bounded service cleanup deletes the retained parent; only its FK cascade may remove pass audit");
        (await db.LegacyPlacementAdoptionPassAudit.AsNoTracking().CountAsync(value => value.ArcId == arcId)).ShouldBe(0);
    }

    [Fact]
    public async Task Deferred_audit_guards_reject_one_sided_totals_and_a_token_that_the_arc_never_held()
    {
        var auditOnly = await SeedAsync(Candidate("audit only guard"));
        await AdoptAsync(auditOnly);
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var arcId = await db.LegacyPlacementAdoptionArc.AsNoTracking().Where(value => value.TeamId == auditOnly.TeamId)
                .Select(value => value.Id).SingleAsync();
            var failure = await Should.ThrowAsync<Exception>(() => InsertZeroAuditAsync(db, arcId, Guid.NewGuid()));
            failure.ToString().ShouldContain("cumulative counters must equal append-only pass audit");
        }

        var counterOnly = await SeedAsync(Candidate("counter only guard"));
        await AdoptAsync(counterOnly);
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var arcId = await db.LegacyPlacementAdoptionArc.AsNoTracking().Where(value => value.TeamId == counterOnly.TeamId)
                .Select(value => value.Id).SingleAsync();
            var failure = await Should.ThrowAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE legacy_placement_adoption_arc
                SET read_bytes = read_bytes + 1,
                    revision = revision + 1,
                    last_modified_at = GREATEST(last_modified_at, clock_timestamp())
                WHERE id = {arcId}
                """));
            failure.ToString().ShouldContain("cumulative counters must equal append-only pass audit");
        }

        var wrongClaim = await SeedAsync(Candidate("wrong claim guard"));
        await AdoptAsync(wrongClaim);
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var arcId = await db.LegacyPlacementAdoptionArc.AsNoTracking().Where(value => value.TeamId == wrongClaim.TeamId)
                .Select(value => value.Id).SingleAsync();
            var held = Guid.NewGuid();
            var forged = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE legacy_placement_adoption_arc
                SET claim_token = {held},
                    claim_started_at = clock_timestamp(),
                    claim_expires_at = clock_timestamp() + INTERVAL '5 minutes',
                    revision = revision + 1,
                    last_modified_at = GREATEST(last_modified_at, clock_timestamp())
                WHERE id = {arcId}
                """);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await InsertZeroAuditAsync(db, arcId, forged);
            var failure = await Should.ThrowAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE legacy_placement_adoption_arc
                SET completed_passes = completed_passes + 1,
                    last_settled_claim_token = {forged},
                    claim_token = NULL,
                    claim_started_at = NULL,
                    claim_expires_at = NULL,
                    revision = revision + 1,
                    last_modified_at = GREATEST(last_modified_at, clock_timestamp())
                WHERE id = {arcId}
                """));
            failure.ToString().ShouldContain("settled audit must name the claim held by the arc");
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task Bounded_cleaning_is_unclaimed_and_does_not_rewrite_the_terminal_audit_summary()
    {
        var world = await SeedAsync();
        var malformed = await AddArtifactAsync(world, Candidate("first malformed member"), DateTimeOffset.UnixEpoch.AddDays(1));
        for (var index = 0; index < LegacyPlacementAdoptionLimits.MaxRowsPerPass; index++)
            await AddArtifactAsync(world, Candidate($"cleanup member {index}"), DateTimeOffset.UnixEpoch.AddDays(index + 2));

        LegacyPlacementAdoptionSummary cleaning;
        using (var scope = ThrowingLayoutScope(malformed.StorageUrl))
            cleaning = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);

        cleaning.NextCursor.ShouldNotBeNull("the abort removes only one bounded cleanup page");
        var terminal = await AdoptAsync(world, cleaning.NextCursor, batchSize: 1);
        terminal.NextCursor.ShouldBeNull();
        terminal.Progress.ShouldNotBeNull().CompletedPasses.ShouldBe(1,
            "control-plane Cleaning is serialized by the arc row and is not a claimed provider pass");
        using var read = _fixture.BeginScope();
        var db = read.Resolve<CodeSpaceDbContext>();
        var arc = await db.LegacyPlacementAdoptionArc.AsNoTracking().SingleAsync(value => value.TeamId == world.TeamId);
        arc.ClaimToken.ShouldBeNull();
        (await db.LegacyPlacementAdoptionPassAudit.AsNoTracking().CountAsync(value => value.ArcId == arc.Id)).ShouldBe(1);
    }

    private ILifetimeScope RuntimeScope(TimeProvider clock, TimeSpan renewalInterval, TimeSpan operationTimeout,
        Func<StorageRuntimeDriverLease, IArtifactStorageDriver> decorate) => _fixture.BeginScope(builder =>
    {
        builder.Register<IStorageRuntimeDriverBroker>(context => new DecoratingBroker(
            context.Resolve<StorageRuntimeDriverBroker>(), decorate)).InstancePerLifetimeScope();
        builder.Register<ILegacyPlacementAdoptionRuntime>(context => new LegacyPlacementAdoptionRuntime(
            context.Resolve<IStorageProviderModuleCatalog>(), context.Resolve<IStorageRuntimeDriverBroker>(), clock)
        {
            ClaimTtl = TimeSpan.FromSeconds(30), ClaimRenewalInterval = renewalInterval,
            ProviderOperationTimeout = operationTimeout,
        }).InstancePerLifetimeScope();
    });

    private static Task<int> InsertZeroAuditAsync(CodeSpaceDbContext db, Guid arcId, Guid claimToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO legacy_placement_adoption_pass_audit (
                arc_id, claim_token, phase, outcome, yield_reason, failure_code,
                start_position, end_position, examined, resolved, confirmed,
                evidence_examined_delta, evidence_resolved_delta, evidence_confirmed_delta,
                mint_examined_delta, available_delta, missing_delta, corrupt_delta,
                already_recorded_delta, conflicts_delta, retryable_delta, read_bytes_delta,
                oversized_item, started_at, completed_at)
            VALUES ({arcId}, {claimToken}, 'Evidence', 'Interrupted', 'None', 'ProgrammingFault',
                0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                FALSE, clock_timestamp(), clock_timestamp())
            """);

    private sealed class ThrowingHeadDriver : DelegatingDriver
    {
        private readonly Exception _exception;

        public ThrowingHeadDriver(StorageRuntimeDriverLease lease, Exception exception) : base(lease) => _exception = exception;

        public override ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<ArtifactStorageHeadResult>(_exception);
    }

    private sealed class RejectedStorageException : Exception, IArtifactStorageOperationalException
    {
        public RejectedStorageException() : base("credential-value") { }
        public ArtifactStorageErrorCode Code => ArtifactStorageErrorCode.Forbidden;
        public bool IsRetryable => false;
    }

    private sealed class AdvancingDriver : DelegatingDriver
    {
        private readonly ManualTimeProvider _clock;
        private readonly TimeSpan _elapsed;

        public AdvancingDriver(StorageRuntimeDriverLease lease, ManualTimeProvider clock, TimeSpan elapsed) : base(lease)
        {
            _clock = clock;
            _elapsed = elapsed;
        }

        public override async ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken)
        {
            var result = await base.HeadAsync(request, cancellationToken);
            _clock.Advance(_elapsed);
            return result;
        }

        public override async ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken)
        {
            var result = await base.OpenReadAsync(request, cancellationToken);
            _clock.Advance(_elapsed);
            return result;
        }
    }

    private sealed class BlockingHead
    {
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingHeadDriver : DelegatingDriver
    {
        private readonly BlockingHead _gate;

        public BlockingHeadDriver(StorageRuntimeDriverLease lease, BlockingHead gate) : base(lease) => _gate = gate;

        public override async ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken)
        {
            await _gate.Continue.Task;
            return await base.HeadAsync(request, CancellationToken.None);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
