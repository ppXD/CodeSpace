using System.Diagnostics;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local.Legacy;
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
    public async Task Invalid_runtime_interval_order_is_rejected_before_arc_or_provider_work()
    {
        var world = await SeedAsync(Candidate("invalid runtime intervals"));
        using var scope = RuntimeScope(TimeProvider.System, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5),
            lease => new TrackingReadDriver(lease, new ReadTracker()));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
            new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None));

        exception.Message.ShouldContain("claim renewal < provider operation timeout < claim TTL");
        using var verification = _fixture.BeginScope();
        (await verification.Resolve<CodeSpaceDbContext>().LegacyPlacementAdoptionArc.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(0);
    }

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
        using var scope = RuntimeScope(clock, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
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
    public async Task Mint_byte_budget_is_rechecked_after_each_physical_read_before_the_next_object_starts()
    {
        const string witnessContent = "w";
        var secondContent = new string('b', 50);
        var secondKey = LegacyLocalObjectKeys.For(ArtifactStore.ComputeSha256Hex(System.Text.Encoding.UTF8.GetBytes(secondContent)))!;
        var world = await SeedAsync(Candidate(witnessContent), Candidate(secondContent));
        var evidence = await AdoptAsync(world, batchSize: 2);
        var reads = new ReadTracker();
        using var scope = RuntimeScope(TimeProvider.System, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
            lease => new TrackingReadDriver(lease, reads));

        var result = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
            new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 2, evidence.NextCursor)
            {
                ByteBudget = 52,
                TimeBudget = TimeSpan.FromMinutes(1),
            }, CancellationToken.None);

        result.Examined.ShouldBe(1);
        reads.Calls(secondKey).ShouldBe(0,
            "the second read cannot be pre-admitted against bytes that the first read has not charged yet");
    }

    [Fact]
    public async Task Mint_time_budget_is_rechecked_after_each_physical_read_before_the_next_object_starts()
    {
        const string witnessContent = "w";
        var secondContent = new string('t', 50);
        var witnessKey = LegacyLocalObjectKeys.For(ArtifactStore.ComputeSha256Hex(System.Text.Encoding.UTF8.GetBytes(witnessContent)))!;
        var secondKey = LegacyLocalObjectKeys.For(ArtifactStore.ComputeSha256Hex(System.Text.Encoding.UTF8.GetBytes(secondContent)))!;
        var world = await SeedAsync(Candidate(witnessContent), Candidate(secondContent));
        var evidence = await AdoptAsync(world, batchSize: 2);
        var clock = new ManualTimeProvider();
        var reads = new ReadTracker((key, calls) =>
        {
            if (key == witnessKey && calls == 2) clock.Advance(TimeSpan.FromSeconds(2));
        });
        using var scope = RuntimeScope(clock, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
            lease => new TrackingReadDriver(lease, reads));

        var result = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
            new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 2, evidence.NextCursor)
            {
                ByteBudget = LegacyPlacementAdoptionLimits.MaxBytesPerPass,
                TimeBudget = TimeSpan.FromSeconds(1),
            }, CancellationToken.None);

        result.Examined.ShouldBe(1);
        reads.Calls(secondKey).ShouldBe(0,
            "elapsed time from the first page-member read must stop the second read before it starts");
    }

    [Fact]
    public async Task Same_identity_duplicate_rows_each_receive_one_terminal_outcome_from_one_physical_observation()
    {
        var world = await SeedAsync(Candidate("duplicate bytes"), Candidate("second manifest row"));
        var evidence = await AdoptAsync(world, batchSize: 1);
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var arcId = await db.LegacyPlacementAdoptionArc.Where(value => value.TeamId == world.TeamId).Select(value => value.Id).SingleAsync();
            var members = await db.LegacyPlacementAdoptionMember.AsNoTracking()
                .Where(value => value.ArcId == arcId)
                .OrderBy(value => value.Position).ToArrayAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE legacy_placement_adoption_member DISABLE TRIGGER USER");
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($$"""
                    UPDATE legacy_placement_adoption_member
                    SET sha256 = {{members[0].Sha256}}, size_bytes = {{members[0].SizeBytes}}, storage_url = {{members[0].StorageUrl}}
                    WHERE arc_id = {{members[1].ArcId}} AND position = {{members[1].Position}}
                    """);
            }
            finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE legacy_placement_adoption_member ENABLE TRIGGER USER"); }
        }

        var result = evidence;
        while (result.NextCursor != null)
        {
            using var scope = _fixture.BeginScope();
            result = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 2, result.NextCursor), CancellationToken.None);
        }

        var progress = result.Progress.ShouldNotBeNull();
        progress.MintExamined.ShouldBe(2);
        progress.Available.ShouldBe(1);
        progress.AlreadyRecorded.ShouldBe(1);
        progress.Conflicts.ShouldBe(0);
        using var verificationScope = _fixture.BeginScope();
        var verificationDb = verificationScope.Resolve<CodeSpaceDbContext>();
        (await verificationDb.ArtifactLocation.AsNoTracking().CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(1);
        (await verificationDb.ArtifactLocationEvent.AsNoTracking().CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_provider_operation_that_ignores_cancellation_yields_before_the_claim_lease_and_is_cleaned_late()
    {
        var world = await SeedAsync(Candidate("bounded hung provider"));
        var gate = new BlockingHead();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var scope = RuntimeScope(TimeProvider.System, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(75),
                lease => new BlockingHeadDriver(lease, gate));
            var result = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            result.Retryable.ShouldBe(1);
            result.YieldReason.ShouldBe(LegacyPlacementAdoptionYieldReasonValue.ProviderRetryable);
            result.NextCursor.ShouldNotBeNull();
            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
            gate.Probes.ShouldBe(0, "a timed-out operation poisons that lease; no probe or second provider call may start on it");
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

    [Theory]
    [InlineData(ProviderFailureSurface.Head)]
    [InlineData(ProviderFailureSurface.Read)]
    [InlineData(ProviderFailureSurface.Probe)]
    [InlineData(ProviderFailureSurface.Thrown)]
    public async Task Typed_provider_rejection_has_one_boundary_across_head_read_probe_and_thrown_paths(ProviderFailureSurface surface)
    {
        var world = await SeedAsync(Candidate($"provider rejection {surface}"));
        using var scope = DecoratingScope(lease => new SurfaceFailureDriver(lease, surface));

        var result = await scope.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
            new LegacyPlacementAdoptionRequest(world.TeamId, world.ActorId, world.ProfileId, 1, null), CancellationToken.None);

        result.Refusal.ShouldBe(LegacyPlacementAdoptionRefusalValue.ProviderRejected);
        result.NextCursor.ShouldBeNull();
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
    public async Task Pass_audit_is_append_only_then_drains_before_its_restricted_parent_tombstone()
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
        await Should.ThrowAsync<Exception>(() => db.LegacyPlacementAdoptionArc.Where(value => value.Id == arcId).ExecuteDeleteAsync());
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE legacy_placement_adoption_pass_audit DISABLE TRIGGER USER");
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO legacy_placement_adoption_pass_audit
                SELECT audit.arc_id, gen_random_uuid(), audit.phase, audit.outcome, audit.yield_reason, audit.failure_code,
                    audit.start_position, audit.end_position, audit.examined, audit.resolved, audit.confirmed,
                    audit.evidence_examined_delta, audit.evidence_resolved_delta, audit.evidence_confirmed_delta,
                    audit.mint_examined_delta, audit.available_delta, audit.missing_delta, audit.corrupt_delta,
                    audit.already_recorded_delta, audit.conflicts_delta, audit.retryable_delta, audit.read_bytes_delta,
                    audit.oversized_item, audit.started_at, audit.completed_at
                FROM legacy_placement_adoption_pass_audit audit CROSS JOIN generate_series(1, 19)
                WHERE audit.arc_id = {{arcId}}
                """);
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE legacy_placement_adoption_pass_audit ENABLE TRIGGER USER"); }
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE legacy_placement_adoption_arc DISABLE TRIGGER USER");
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE legacy_placement_adoption_arc
                SET created_at = clock_timestamp() - INTERVAL '32 days', completed_at = clock_timestamp() - INTERVAL '31 days'
                WHERE id = {{arcId}}
                """);
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE legacy_placement_adoption_arc ENABLE TRIGGER USER"); }
        var cleanupWorld = await SeedAsync();
        await AdoptAsync(cleanupWorld);
        (await db.LegacyPlacementAdoptionPassAudit.AsNoTracking().CountAsync(value => value.ArcId == arcId)).ShouldBe(8,
            "one cleanup call must never drain more than the fixed 32-row cap");
        (await db.LegacyPlacementAdoptionArc.AsNoTracking().CountAsync(value => value.Id == arcId)).ShouldBe(1);
        var secondCleanupWorld = await SeedAsync();
        await AdoptAsync(secondCleanupWorld);
        (await db.LegacyPlacementAdoptionArc.AsNoTracking().CountAsync(value => value.Id == arcId)).ShouldBe(0,
            "a later bounded cleanup drains the remainder before deleting the RESTRICTed parent");
        (await db.LegacyPlacementAdoptionPassAudit.AsNoTracking().CountAsync(value => value.ArcId == arcId)).ShouldBe(0);
    }

    [Fact]
    public async Task Cross_team_cleanup_skips_another_transaction_claim_and_still_deletes_only_one_bounded_batch()
    {
        var retained = await SeedAsync(Candidate("cross team cleanup"));
        var result = await AdoptAsync(retained);
        result = await AdoptAsync(retained, result.NextCursor);
        using (var setup = _fixture.BeginScope())
        {
            var db = setup.Resolve<CodeSpaceDbContext>();
            var arcId = await db.LegacyPlacementAdoptionArc.Where(value => value.TeamId == retained.TeamId).Select(value => value.Id).SingleAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE legacy_placement_adoption_pass_audit DISABLE TRIGGER USER");
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO legacy_placement_adoption_pass_audit
                    SELECT audit.arc_id, gen_random_uuid(), audit.phase, audit.outcome, audit.yield_reason, audit.failure_code,
                        audit.start_position, audit.end_position, audit.examined, audit.resolved, audit.confirmed,
                        audit.evidence_examined_delta, audit.evidence_resolved_delta, audit.evidence_confirmed_delta,
                        audit.mint_examined_delta, audit.available_delta, audit.missing_delta, audit.corrupt_delta,
                        audit.already_recorded_delta, audit.conflicts_delta, audit.retryable_delta, audit.read_bytes_delta,
                        audit.oversized_item, audit.started_at, audit.completed_at
                    FROM legacy_placement_adoption_pass_audit audit CROSS JOIN generate_series(1, 31)
                    WHERE audit.arc_id = {{arcId}}
                    """);
            }
            finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE legacy_placement_adoption_pass_audit ENABLE TRIGGER USER"); }
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE legacy_placement_adoption_arc DISABLE TRIGGER USER");
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($$"""
                    UPDATE legacy_placement_adoption_arc
                    SET created_at = clock_timestamp() - INTERVAL '32 days', completed_at = clock_timestamp() - INTERVAL '31 days'
                    WHERE id = {{arcId}}
                    """);
            }
            finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE legacy_placement_adoption_arc ENABLE TRIGGER USER"); }
        }

        using var blocker = _fixture.BeginScope();
        var blockerDb = blocker.Resolve<CodeSpaceDbContext>();
        await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync();
        await blockerDb.Database.ExecuteSqlInterpolatedAsync($$"""
            SELECT 1 FROM legacy_placement_adoption_pass_audit audit
            JOIN legacy_placement_adoption_arc arc ON arc.id = audit.arc_id
            WHERE arc.team_id = {{retained.TeamId}}
            ORDER BY audit.completed_at, audit.arc_id, audit.claim_token
            FOR UPDATE OF audit LIMIT 32
            """);

        var otherTeam = await SeedAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using (var cleanup = _fixture.BeginScope())
            await cleanup.Resolve<ILegacyPlacementAdopter>().AdoptAsync(
                new LegacyPlacementAdoptionRequest(otherTeam.TeamId, otherTeam.ActorId, otherTeam.ProfileId, 1, null), timeout.Token);

        using var verification = _fixture.BeginScope();
        var remaining = await verification.Resolve<CodeSpaceDbContext>().LegacyPlacementAdoptionPassAudit.AsNoTracking()
            .CountAsync(value => value.Arc.TeamId == retained.TeamId);
        remaining.ShouldBe(32, "the second team must skip 32 locked rows and delete exactly the next bounded batch without waiting");
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
            failure.ToString().ShouldContain("pass audit must be the pass atomically settled by its parent");
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
            failure.ToString().ShouldContain("counters cannot change without exactly one pass settlement");
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

    [Theory]
    [InlineData("Minting", 0, 0, 0, "phase")]
    [InlineData("Evidence", -1, 0, 0, "position")]
    [InlineData("Evidence", 0, 1, 0, "delta")]
    public async Task Coordinated_forgery_with_a_held_token_still_cannot_lie_about_phase_position_or_delta(
        string phase, long startOffset, long auditReadDelta, long arcReadDelta, string expected)
    {
        var world = await SeedAsync(Candidate($"forged {expected} first"), Candidate($"forged {expected} second"));
        await AdoptAsync(world, batchSize: 1);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var arcId = await db.LegacyPlacementAdoptionArc.Where(value => value.TeamId == world.TeamId).Select(value => value.Id).SingleAsync();

        var failure = await Should.ThrowAsync<Exception>(() => ForgeSettlementAsync(db, arcId,
            new Forgery(phase, startOffset, auditReadDelta, arcReadDelta)));

        failure.ToString().ShouldContain(expected);
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

    private static async Task ForgeSettlementAsync(CodeSpaceDbContext db, Guid arcId, Forgery forgery)
    {
        var claimToken = Guid.NewGuid();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE legacy_placement_adoption_arc
            SET claim_token = {{claimToken}}, claim_started_at = clock_timestamp(),
                claim_expires_at = clock_timestamp() + INTERVAL '30 seconds', expires_at = GREATEST(expires_at, clock_timestamp() + INTERVAL '7 days'),
                revision = revision + 1, last_modified_at = GREATEST(last_modified_at, clock_timestamp())
            WHERE id = {{arcId}}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO legacy_placement_adoption_pass_audit (
                arc_id, claim_token, phase, outcome, yield_reason, failure_code,
                start_position, end_position, examined, resolved, confirmed,
                evidence_examined_delta, evidence_resolved_delta, evidence_confirmed_delta,
                mint_examined_delta, available_delta, missing_delta, corrupt_delta,
                already_recorded_delta, conflicts_delta, retryable_delta, read_bytes_delta,
                oversized_item, started_at, completed_at)
            SELECT id, {{claimToken}}, {{forgery.Phase}}, 'Interrupted', 'None', 'ProgrammingFault',
                current_position + {{forgery.StartOffset}}, current_position, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, {{forgery.AuditReadDelta}},
                FALSE, claim_started_at, clock_timestamp()
            FROM legacy_placement_adoption_arc WHERE id = {{arcId}}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE legacy_placement_adoption_arc
            SET read_bytes = read_bytes + {{forgery.ArcReadDelta}}, completed_passes = completed_passes + 1,
                last_settled_claim_token = {{claimToken}}, claim_token = NULL, claim_started_at = NULL, claim_expires_at = NULL,
                revision = revision + 1, last_modified_at = GREATEST(last_modified_at, clock_timestamp())
            WHERE id = {{arcId}}
            """);
        await transaction.CommitAsync();
    }

    private sealed record Forgery(string Phase, long StartOffset, long AuditReadDelta, long ArcReadDelta);

    private sealed class ThrowingHeadDriver : DelegatingDriver
    {
        private readonly Exception _exception;

        public ThrowingHeadDriver(StorageRuntimeDriverLease lease, Exception exception) : base(lease) => _exception = exception;

        public override ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<ArtifactStorageHeadResult>(_exception);
    }

    public enum ProviderFailureSurface { Head, Read, Probe, Thrown }

    private sealed class SurfaceFailureDriver : DelegatingDriver
    {
        private static readonly ArtifactStorageError Rejected = new(ArtifactStorageErrorCode.Forbidden, "credential-value");
        private readonly ProviderFailureSurface _surface;

        public SurfaceFailureDriver(StorageRuntimeDriverLease lease, ProviderFailureSurface surface) : base(lease) => _surface = surface;

        public override ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) => _surface switch
        {
            ProviderFailureSurface.Head => ValueTask.FromResult(ArtifactStorageHeadResult.Failed(Rejected)),
            ProviderFailureSurface.Probe => ValueTask.FromResult(ArtifactStorageHeadResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Missing, "missing"))),
            ProviderFailureSurface.Thrown => ValueTask.FromException<ArtifactStorageHeadResult>(new RejectedStorageException()),
            _ => base.HeadAsync(request, cancellationToken),
        };

        public override ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) =>
            _surface == ProviderFailureSurface.Read ? ValueTask.FromResult(ArtifactStorageReadResult.Failed(Rejected)) : base.OpenReadAsync(request, cancellationToken);

        public override ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) =>
            _surface == ProviderFailureSurface.Probe
                ? ValueTask.FromResult(new ArtifactStorageProbeResult { Status = ArtifactStorageProbeStatus.Unavailable, Latency = TimeSpan.Zero, Error = Rejected })
                : base.ProbeAsync(request, cancellationToken);
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
        private int _probes;
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Probes => Volatile.Read(ref _probes);
        public void Probed() => Interlocked.Increment(ref _probes);
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

        public override ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken)
        {
            _gate.Probed();
            return base.ProbeAsync(request, cancellationToken);
        }
    }

    private sealed class TrackingReadDriver : DelegatingDriver
    {
        private readonly ReadTracker _tracker;

        public TrackingReadDriver(StorageRuntimeDriverLease lease, ReadTracker tracker) : base(lease) => _tracker = tracker;

        public override ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken)
        {
            _tracker.Record(request.ObjectKey);
            return base.OpenReadAsync(request, cancellationToken);
        }
    }

    private sealed class ReadTracker
    {
        private readonly Action<string, int>? _afterReadStarted;
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

        public ReadTracker(Action<string, int>? afterReadStarted = null) => _afterReadStarted = afterReadStarted;

        public int Calls(string key)
        {
            lock (_calls) return _calls.GetValueOrDefault(key);
        }

        public void Record(string key)
        {
            int calls;
            lock (_calls)
            {
                calls = _calls.GetValueOrDefault(key) + 1;
                _calls[key] = calls;
            }
            _afterReadStarted?.Invoke(key, calls);
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
