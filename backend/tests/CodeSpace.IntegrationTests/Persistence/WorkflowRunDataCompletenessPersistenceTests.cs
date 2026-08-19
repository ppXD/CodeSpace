using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres proof that a run's completeness statement cannot lie, and that a gap in the record cannot be hidden.
/// Almost every assertion is a COUNTER-EXAMPLE: the dishonest row is offered and the database refuses it, because an
/// invariant that holds only while every writer remembers it is not an invariant.
///
/// <para>The one that matters most is the FAIL-CLOSED arm. A manifest may not read as complete when it could not
/// determine whether something is present: an unstated expectation is refused as a complete verdict, and so is a
/// shortfall and a known-missing span. A manifest that reported complete because it could not check would have turned
/// an unknown into a false assurance — strictly worse than having no manifest.</para>
///
/// <para>The second is that ARRIVAL ORDER cannot decide the outcome. A gap is never refused to protect a claim: raising
/// a complete verdict over an open gap is refused, and a gap noticed after a complete verdict was already recorded
/// downgrades it. Both directions are pinned, because only one of them is a constraint and the other is a trigger.</para>
///
/// <para>Nothing reads or writes these tables in production yet, so these teeth are the entire contract a later
/// capture slice — and, later still, a terminal-authority cutover — will build on.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunDataCompletenessPersistenceTests
{
    private const string Completeness = "ck_workflow_run_data_manifest_completeness";
    private const string Range = "ck_workflow_run_capture_gap_range";
    private readonly PostgresFixture _fixture;

    public WorkflowRunDataCompletenessPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The complete verdict is reachable, and reachable only over a determinate, fully present, gapless facet. Both
    /// strictly readable states are offered, because "a redacted record is still a whole one" is a claim about the
    /// vocabulary that has to hold in the database too.
    /// </summary>
    [Theory]
    [InlineData(WorkflowRunCaptureCompleteness.Exact)]
    [InlineData(WorkflowRunCaptureCompleteness.RedactedExact)]
    public async Task A_determinate_gapless_facet_may_claim_a_complete_record(WorkflowRunCaptureCompleteness verdict)
    {
        var world = await SeedRunAsync();

        var manifest = await SeedManifestAsync(world, WorkflowRunDataOwnerKinds.NativeRecord, statement => statement.Verdict = verdict);

        using var scope = _fixture.BeginScope();
        var stored = await Manifests(scope).SingleAsync(candidate => candidate.Id == manifest.Id);
        stored.Verdict.ShouldBe(verdict);
        stored.Verdict.IsStrictlyReadable().ShouldBeTrue();
        stored.ExpectedRecordCount.ShouldBe(3);
        stored.KnownMissingCount.ShouldBe(0);
        stored.Revision.ShouldBe(1);
    }

    /// <summary>
    /// THE decision this slice exists to make. An indeterminate expectation — nobody could establish what should be
    /// here — is storable, is NOT complete, and cannot be restated as complete. The other two ways a complete claim can
    /// outrun its evidence are pinned beside it: a shortfall against a stated expectation, and a known-missing span.
    /// </summary>
    [Fact]
    public async Task An_indeterminate_expectation_resolves_to_not_complete_and_can_never_be_restated_as_complete()
    {
        var world = await SeedRunAsync();

        var indeterminate = await SeedManifestAsync(world, WorkflowRunDataOwnerKinds.NativeRecord, statement =>
        {
            statement.ExpectedRecordCount = null;
            statement.PresentRecordCount = 7;
            statement.Verdict = WorkflowRunCaptureCompleteness.LegacyUnknown;
        });

        using (var scope = _fixture.BeginScope())
        {
            var stored = await Manifests(scope).SingleAsync(candidate => candidate.Id == indeterminate.Id);
            stored.ExpectedRecordCount.ShouldBeNull(customMessage: "an unstated expectation is the indeterminate state; zero would be the determinate claim 'expected to be empty'");
            stored.Verdict.IsStrictlyReadable().ShouldBeFalse(
                customMessage: "a manifest that could not determine what belongs here must not read as complete — that is the whole reason the table exists");
        }

        // Seven records are present and the expectation is unknown, which is precisely the case a producer is tempted
        // to round up: "I found everything I looked for." The database refuses it.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunDataManifest.SingleAsync(candidate => candidate.Id == indeterminate.Id);
            stored.Verdict = WorkflowRunCaptureCompleteness.Exact;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            stored.Revision++;
            var roundedUp = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            roundedUp.InnerException?.Message.ShouldContain(Completeness,
                customMessage: "a complete verdict over an unstated expectation converts an unknown into a false assurance");
        }

        await RejectsManifestAsync(world, Completeness, statement => statement.ExpectedRecordCount = null);
        await RejectsManifestAsync(world, Completeness, statement => statement.PresentRecordCount = 2);
        await RejectsManifestAsync(world, Completeness, statement => statement.KnownMissingCount = 1);
        await RejectsManifestAsync(world, Completeness, statement =>
        {
            statement.Verdict = WorkflowRunCaptureCompleteness.RedactedExact;
            statement.ExpectedRecordCount = null;
        });
    }

    /// <summary>
    /// The asymmetry that keeps the fail-closed arm from becoming fail-always. A SURPLUS over a declared expectation is
    /// admitted, because a re-observed record can legitimately push the present count past it — and a plane that made
    /// that unwritable would push producers into not counting at all, which is where the silence came from.
    /// </summary>
    [Fact]
    public async Task A_surplus_over_a_declared_expectation_does_not_block_a_complete_record()
    {
        var world = await SeedRunAsync();

        var surplus = await SeedManifestAsync(world, WorkflowRunDataOwnerKinds.ToolCall, statement => statement.PresentRecordCount = 5);

        using var scope = _fixture.BeginScope();
        var stored = await Manifests(scope).SingleAsync(candidate => candidate.Id == surplus.Id);
        stored.PresentRecordCount.ShouldBe(5);
        stored.ExpectedRecordCount.ShouldBe(3);
        stored.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Exact);
    }

    /// <summary>
    /// The cross-table half, in the order where a CHECK could never see it: the gap exists first, and every facet of
    /// the run is refused a complete verdict — including a facet the gap does not belong to. Conservative on purpose:
    /// "something in this run is known-missing" is not a fact one part of the record gets to read past.
    /// </summary>
    [Fact]
    public async Task A_manifest_cannot_claim_complete_while_a_gap_covering_its_run_is_open()
    {
        var world = await SeedRunAsync();
        var gap = await SeedGapAsync(world);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            // Every CHECK on this row passes — a determinate expectation, everything present, nothing counted as
            // missing. Only the gap plane contradicts it, which is why the refusal has to come from the trigger.
            db.WorkflowRunDataManifest.Add(Manifest(world, WorkflowRunDataOwnerKinds.NativeRecord));
            var overGap = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            overGap.InnerException?.Message.ShouldContain("cannot claim a complete record while a known-missing span of the run is still open");
            overGap.InnerException?.Message.ShouldContain(gap.Id.ToString(),
                customMessage: "the refusal must name the span, or an operator cannot go and look at it");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunDataManifest.Add(Manifest(world, WorkflowRunDataOwnerKinds.ModelCall));
            var otherFacet = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            otherFacet.InnerException?.Message.ShouldContain("cannot claim a complete record while a known-missing span of the run is still open",
                customMessage: "a gap anywhere in the run blocks every facet's complete claim — the conservative arm, on purpose");
        }

        // A not-complete statement over the same run is admitted, which is what keeps the plane usable rather than
        // merely strict: the honest answer is always writable.
        var honest = await SeedManifestAsync(world, WorkflowRunDataOwnerKinds.NativeRecord, statement =>
        {
            statement.KnownMissingCount = 1;
            statement.Verdict = WorkflowRunCaptureCompleteness.Partial;
        });

        using (var scope = _fixture.BeginScope())
        {
            (await Manifests(scope).SingleAsync(candidate => candidate.Id == honest.Id)).Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Partial);
        }
    }

    /// <summary>
    /// ...and the other order, which no CHECK can hold at all: the complete verdict is already recorded when the gap is
    /// noticed. The gap is still admitted — refusing the honest observation to protect the claim would be the exact
    /// inversion this plane exists to prevent — and the DATABASE downgrades the statement instead.
    /// </summary>
    [Fact]
    public async Task A_gap_noticed_afterwards_downgrades_a_manifest_that_already_claimed_complete()
    {
        var world = await SeedRunAsync();
        var claimed = await SeedManifestAsync(world, WorkflowRunDataOwnerKinds.NativeRecord);
        var untouchedFacet = await SeedManifestAsync(world, WorkflowRunDataOwnerKinds.ToolCall, statement =>
        {
            statement.ExpectedRecordCount = null;
            statement.Verdict = WorkflowRunCaptureCompleteness.LegacyUnknown;
        });

        await SeedGapAsync(world);

        using var scope = _fixture.BeginScope();
        var stored = await Manifests(scope).SingleAsync(candidate => candidate.Id == claimed.Id);
        stored.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Partial,
            customMessage: "a gap noticed after the fact must un-complete its run's statement, or the order the two writers arrive in decides whether the record reads as whole");
        stored.KnownMissingCount.ShouldBe(1, customMessage: "the gap is counted against the facet it happened in, not merely used to suppress the verdict");
        stored.Revision.ShouldBe(2, customMessage: "the downgrade is a write like any other and owes its revision");

        var neighbour = await Manifests(scope).SingleAsync(candidate => candidate.Id == untouchedFacet.Id);
        neighbour.KnownMissingCount.ShouldBe(0,
            customMessage: "a gap in the native-record plane is not a missing tool call; only the run-wide complete claim is suppressed across facets");
    }

    /// <summary>
    /// A recovered span stops blocking completeness, and that arm exists for a reason: a torn re-attach whose source
    /// still holds the lines is captured on the next pass, so a gap that could never close would make the manifest
    /// fail-ALWAYS rather than fail-closed — and a verdict nothing can reach is not a verdict. The fill happens once,
    /// in one direction, and only while citing what now covers the span.
    /// </summary>
    [Fact]
    public async Task A_recovered_span_stops_blocking_completeness_and_the_fill_happens_exactly_once()
    {
        var world = await SeedRunAsync();
        var gap = await SeedGapAsync(world, subjectKind: WorkflowRunDataOwnerKinds.NativeRecord, configure: candidate => candidate.Reason = CaptureGapReason.ReattachTorn);

        await RejectsRecoveryAsync(gap, "ck_workflow_run_capture_gap_resolution", stored => stored.RecoveredByKind = null);
        await RejectsRecoveryAsync(gap, "ck_workflow_run_capture_gap_resolution", stored => stored.RecoveredById = " ");
        await RejectsRecoveryAsync(gap, "ck_workflow_run_capture_gap_resolution", stored => stored.RecoveredAt = stored.NoticedAt.AddMinutes(-1));

        await RecoverAsync(gap);

        // The span is covered, so the run's record can be stated complete — the whole point of having a resolution axis.
        var completed = await SeedManifestAsync(world, WorkflowRunDataOwnerKinds.NativeRecord);

        using (var scope = _fixture.BeginScope())
        {
            (await Manifests(scope).SingleAsync(candidate => candidate.Id == completed.Id)).Verdict
                .ShouldBe(WorkflowRunCaptureCompleteness.Exact);
            var stored = await Gaps(scope).SingleAsync(candidate => candidate.Id == gap.Id);
            stored.Resolution.ShouldBe(CaptureGapResolution.Recovered);
            stored.RecoveredByKind.ShouldBe(WorkflowRunDataOwnerKinds.NativeRecord,
                customMessage: "an uncited recovery is an unattributable claim that silently unblocks a complete verdict");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunCaptureGap.SingleAsync(candidate => candidate.Id == gap.Id);
            stored.Resolution = CaptureGapResolution.Open;
            stored.RecoveredAt = null;
            stored.RecoveredByKind = null;
            stored.RecoveredById = null;
            var reopened = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            reopened.InnerException?.Message.ShouldContain("resolution is filled exactly once");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunCaptureGap.SingleAsync(candidate => candidate.Id == gap.Id);
            stored.RecoveredById = "some-other-record";
            var recited = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            recited.InnerException?.Message.ShouldContain("resolution is filled exactly once",
                customMessage: "a citation that can be swapped is a citation nobody can audit");
        }
    }

    /// <summary>
    /// A gap has to be a LOCATABLE span with a stated cause, or it is a shrug with columns. Every illegal coordinate
    /// shape is offered here, plus a reason outside the closed vocabulary — including the one a producer reaches for
    /// when it does not want to classify what happened.
    /// </summary>
    [Fact]
    public async Task A_malformed_span_or_an_unclassified_reason_is_refused()
    {
        var world = await SeedRunAsync();

        // A position with nothing to be a position IN is a coordinate nobody can locate.
        await RejectsGapAsync(world, Range, gap => gap.StreamId = null);
        await RejectsGapAsync(world, Range, gap => gap.RangeStart = null);
        await RejectsGapAsync(world, Range, gap => gap.RangeStart = -1);
        await RejectsGapAsync(world, Range, gap => gap.RangeEnd = 511);
        await RejectsGapAsync(world, Range, gap => gap.RangeStartedAt = DateTimeOffset.UtcNow);

        await RejectsGapAsync(world, Range, gap => Timed(gap, DateTimeOffset.UtcNow, ended: null, keepOrdinal: true));
        await RejectsGapAsync(world, Range, gap => Timed(gap, started: null, ended: DateTimeOffset.UtcNow));
        await RejectsGapAsync(world, Range, gap => Timed(gap, DateTimeOffset.UtcNow, ended: DateTimeOffset.UtcNow.AddMinutes(-5)));
        await RejectsGapAsync(world, Range, gap =>
        {
            gap.RangeKind = CaptureGapRangeKind.Unbounded;
            gap.RangeEnd = null;
        });

        await RejectsGapAsync(world, "ck_workflow_run_capture_gap_reason", gap => gap.Reason = (CaptureGapReason)99);
        await RejectsGapAsync(world, "ck_workflow_run_capture_gap_reason", gap => gap.ReasonDetail = "   ");
        await RejectsGapAsync(world, "ck_workflow_run_capture_gap_subject", gap => gap.SubjectKind = "unknown-plane");
        await RejectsGapAsync(world, "ck_workflow_run_capture_gap_subject", gap => gap.CaptureSource = " ");
        await RejectsGapAsync(world, "ck_workflow_run_capture_gap_channel", gap => gap.Channel = (NativeRecordChannel)77);
        await RejectsGapAsync(world, "ck_workflow_run_capture_gap_bounds", gap => gap.SchemaVersion = 0);

        // The four honest shapes, so the refusals above are not simply a plane that admits nothing. "I missed something
        // and cannot say where" is one of them, deliberately: a producer that had to fake a range would fake one.
        await SeedGapAsync(world, configure: gap => gap.RangeEnd = null);
        await SeedGapAsync(world, configure: gap => gap.RangeKind = CaptureGapRangeKind.ByteOffset);
        await SeedGapAsync(world, configure: gap => Timed(gap, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow));
        await SeedGapAsync(world, configure: Unbounded);

        using var scope = _fixture.BeginScope();
        (await Gaps(scope).CountAsync(candidate => candidate.WorkflowRunId == world.RunId)).ShouldBe(4);
    }

    /// <summary>
    /// A gap is never unnoticed. Nothing about the span may be restated, and no gap may be deleted — that refusal is
    /// load-bearing rather than austere, because a removable gap makes a complete manifest reachable by deleting the
    /// evidence for it. It is also why a gap must be BORN Open: a span recovered in the same breath as it is recorded
    /// would never have been visible as missing.
    /// </summary>
    [Fact]
    public async Task A_gap_is_born_open_never_restated_and_never_deleted()
    {
        var world = await SeedRunAsync();

        await RejectsGapAsync(world, "must be born Open", gap =>
        {
            gap.Resolution = CaptureGapResolution.Recovered;
            gap.RecoveredAt = gap.NoticedAt;
            gap.RecoveredByKind = WorkflowRunDataOwnerKinds.NativeRecord;
            gap.RecoveredById = "record-1";
        });

        var gap = await SeedGapAsync(world);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunCaptureGap.SingleAsync(candidate => candidate.Id == gap.Id);
            stored.RangeStart = 0;
            stored.Reason = CaptureGapReason.FrameUnreadable;
            var shrunk = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            shrunk.InnerException?.Message.ShouldContain("append-only apart from its resolution",
                customMessage: "a span that can be narrowed after the fact is a gap a producer can talk its way out of");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var erased = await Should.ThrowAsync<PostgresException>(
                db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM workflow_run_capture_gap WHERE id = {gap.Id}"));
            erased.Message.ShouldContain("is never unnoticed",
                customMessage: "a deletable gap makes a complete manifest reachable by deleting the evidence, which is the dishonesty this plane exists to remove");
        }

        using (var scope = _fixture.BeginScope())
        {
            (await Gaps(scope).CountAsync(candidate => candidate.Id == gap.Id)).ShouldBe(1);
        }
    }

    /// <summary>
    /// The manifest may not report LESS missing than the gap plane can already show. Above the floor is admitted: a
    /// producer that knows of more missing than it has rowed is erring toward incomplete, which is the safe direction.
    /// The floor is per FACET, so a gap in one plane does not silently inflate another's count.
    /// </summary>
    [Fact]
    public async Task A_known_missing_count_may_not_sit_below_its_facets_open_gaps()
    {
        var world = await SeedRunAsync();
        await SeedGapAsync(world);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var statement = Manifest(world, WorkflowRunDataOwnerKinds.NativeRecord);
            statement.Verdict = WorkflowRunCaptureCompleteness.Partial;
            db.WorkflowRunDataManifest.Add(statement);
            var underCount = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            underCount.InnerException?.Message.ShouldContain("known-missing count may not be below the open gaps recorded for this facet");
        }

        var stated = await SeedManifestAsync(world, WorkflowRunDataOwnerKinds.NativeRecord, statement =>
        {
            statement.KnownMissingCount = 4;
            statement.Verdict = WorkflowRunCaptureCompleteness.Partial;
        });

        using (var scope = _fixture.BeginScope())
        {
            (await Manifests(scope).SingleAsync(candidate => candidate.Id == stated.Id)).KnownMissingCount.ShouldBe(4);
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunDataManifest.SingleAsync(candidate => candidate.Id == stated.Id);
            stored.KnownMissingCount = 0;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            stored.Revision++;
            var walkedBack = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            walkedBack.InnerException?.Message.ShouldContain("known-missing count may not be below the open gaps recorded for this facet",
                customMessage: "a count a writer can walk back to zero un-counts a span the gap plane still shows as open");
        }

        // The floor belongs to the facet the gap happened in — a native-record gap is not a missing model call.
        var neighbour = await SeedManifestAsync(world, WorkflowRunDataOwnerKinds.ModelCall, statement =>
        {
            statement.ExpectedRecordCount = null;
            statement.Verdict = WorkflowRunCaptureCompleteness.LegacyUnknown;
        });

        using (var scope = _fixture.BeginScope())
        {
            (await Manifests(scope).SingleAsync(candidate => candidate.Id == neighbour.Id)).KnownMissingCount.ShouldBe(0);
        }
    }

    /// <summary>
    /// One statement per facet of a run, and its identity is immutable. Two rows stating different completeness for the
    /// same facet would make whoever asked pick one, which is exactly the choosing this plane exists to remove — and a
    /// statement that could change which facet it describes would carry its counts across to a plane they never counted.
    /// </summary>
    [Fact]
    public async Task One_statement_per_facet_and_its_identity_is_immutable()
    {
        var world = await SeedRunAsync();
        var statement = await SeedManifestAsync(world, WorkflowRunDataOwnerKinds.NativeRecord);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunDataManifest.Add(Manifest(world, WorkflowRunDataOwnerKinds.NativeRecord));
            var second = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            second.InnerException?.Message.ShouldContain("ux_workflow_run_data_manifest_facet");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunDataManifest.SingleAsync(candidate => candidate.Id == statement.Id);
            stored.Facet = WorkflowRunDataOwnerKinds.ToolCall;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            stored.Revision++;
            var rebranded = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            rebranded.InnerException?.Message.ShouldContain("stable statement identity is immutable");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunDataManifest.SingleAsync(candidate => candidate.Id == statement.Id);
            stored.PresentRecordCount = 9;
            stored.ExpectedRecordCount = 9;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var silent = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            silent.InnerException?.Message.ShouldContain("revision must advance exactly once",
                customMessage: "a restated count that does not advance the revision is a rewrite nobody can see happened");
        }
    }

    /// <summary>
    /// The unique and partial indexes are what make the plane hold up under CONCURRENCY and at SCALE. Asserting that a
    /// duplicate insert throws would prove the constraint, not the index; the counter-example here is the index's own
    /// existence, uniqueness and predicate — and for the open-gap probe, that it stays partial, since a verdict that
    /// scanned every span ever recovered is the "computable without a full scan" claim quietly failing.
    /// </summary>
    [Theory]
    [InlineData("ux_workflow_run_data_manifest_facet", "workflow_run_data_manifest", "(team_id, workflow_run_id, facet)", true, "")]
    [InlineData("ix_workflow_run_capture_gap_open", "workflow_run_capture_gap", "(team_id, workflow_run_id, subject_kind)", false, "WHERE (resolution")]
    [InlineData("ix_workflow_run_data_manifest_incomplete", "workflow_run_data_manifest", "(team_id, last_modified_at, id)", false, "WHERE (")]
    public async Task The_indexes_the_plane_depends_on_are_installed(string indexName, string tableName, string expectedColumns, bool unique, string expectedFilter)
    {
        var definitions = await IndexDefinitionsAsync(tableName, indexName);

        definitions.ShouldHaveSingleItem(
            customMessage: $"index '{indexName}' must exist after 0146 applies. Diagnose with: psql -c '\\di {indexName}'.");
        definitions[0].ShouldContain(expectedColumns,
            customMessage: $"index '{indexName}' covers the wrong columns, so the lookup it exists for is a scan. Diagnose with: psql -c '\\d {tableName}'.");
        if (unique)
            definitions[0].ShouldStartWith("CREATE UNIQUE", customMessage: $"index '{indexName}' exists but is not UNIQUE, so it rejects nothing.");
        if (expectedFilter.Length > 0)
            definitions[0].ShouldContain(expectedFilter, customMessage: $"index '{indexName}' must stay partial, or it grows with the rows it was never meant to see.");
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

    /// <summary>States a TIME-coordinate span, so one line offers a whole illegal shape instead of four assignments each time. <paramref name="keepOrdinal"/> leaves the positional columns in place, which is the mixed shape no arm admits.</summary>
    private static void Timed(WorkflowRunCaptureGap gap, DateTimeOffset? started, DateTimeOffset? ended, bool keepOrdinal = false)
    {
        gap.RangeKind = CaptureGapRangeKind.Time;
        gap.RangeStartedAt = started;
        gap.RangeEndedAt = ended;
        if (keepOrdinal) return;

        gap.RangeStart = null;
        gap.RangeEnd = null;
    }

    /// <summary>States the coordinate-less span — an honest gap whose extent nobody could establish.</summary>
    private static void Unbounded(WorkflowRunCaptureGap gap)
    {
        gap.RangeKind = CaptureGapRangeKind.Unbounded;
        gap.RangeStart = null;
        gap.RangeEnd = null;
        gap.StreamId = null;
    }

    private static IQueryable<WorkflowRunCaptureGap> Gaps(ILifetimeScope scope) => scope.Resolve<CodeSpaceDbContext>().WorkflowRunCaptureGap.AsNoTracking();
    private static IQueryable<WorkflowRunDataManifest> Manifests(ILifetimeScope scope) => scope.Resolve<CodeSpaceDbContext>().WorkflowRunDataManifest.AsNoTracking();

    private async Task RejectsGapAsync(RunWorld world, string expectedMessage, Action<WorkflowRunCaptureGap> forge)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var gap = Gap(world);
        forge(gap);
        db.WorkflowRunCaptureGap.Add(gap);

        var rejected = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();

        rejected.InnerException?.Message.ShouldContain(expectedMessage);
    }

    /// <summary>Offers one otherwise-legal COMPLETE statement with a single field forged, so a table of dishonest claims reads as one line each.</summary>
    private async Task RejectsManifestAsync(RunWorld world, string expectedMessage, Action<WorkflowRunDataManifest> forge)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var statement = Manifest(world, WorkflowRunDataOwnerKinds.HarnessExecution);
        forge(statement);
        db.WorkflowRunDataManifest.Add(statement);

        var rejected = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();

        rejected.InnerException?.Message.ShouldContain(expectedMessage);
    }

    /// <summary>Offers one otherwise-legal RECOVERY with a single field of the citation forged.</summary>
    private async Task RejectsRecoveryAsync(WorkflowRunCaptureGap gap, string expectedMessage, Action<WorkflowRunCaptureGap> forge)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var stored = await db.WorkflowRunCaptureGap.SingleAsync(candidate => candidate.Id == gap.Id);
        Recovery(stored);
        forge(stored);

        var rejected = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();

        rejected.InnerException?.Message.ShouldContain(expectedMessage);
    }

    private async Task RecoverAsync(WorkflowRunCaptureGap gap)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var stored = await db.WorkflowRunCaptureGap.SingleAsync(candidate => candidate.Id == gap.Id);
        Recovery(stored);
        await db.SaveChangesAsync();
    }

    private static void Recovery(WorkflowRunCaptureGap gap)
    {
        gap.Resolution = CaptureGapResolution.Recovered;
        gap.RecoveredAt = gap.NoticedAt.AddSeconds(30);
        gap.RecoveredByKind = WorkflowRunDataOwnerKinds.NativeRecord;
        gap.RecoveredById = "native-record-" + Guid.NewGuid().ToString("N")[..8];
    }

    private async Task<WorkflowRunCaptureGap> SeedGapAsync(RunWorld world, string subjectKind = WorkflowRunDataOwnerKinds.NativeRecord, Action<WorkflowRunCaptureGap>? configure = null)
    {
        var gap = Gap(world, subjectKind);
        configure?.Invoke(gap);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunCaptureGap.Add(gap);
        await db.SaveChangesAsync();
        return gap;
    }

    private async Task<WorkflowRunDataManifest> SeedManifestAsync(RunWorld world, string facet, Action<WorkflowRunDataManifest>? configure = null)
    {
        var statement = Manifest(world, facet);
        configure?.Invoke(statement);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunDataManifest.Add(statement);
        await db.SaveChangesAsync();
        return statement;
    }

    private async Task<RunWorld> SeedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "data-completeness-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return new RunWorld(await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId), teamId);
    }

    private static WorkflowRunCaptureGap Gap(RunWorld world, string subjectKind = WorkflowRunDataOwnerKinds.NativeRecord)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowRunCaptureGap
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, WorkflowRunId = world.RunId, SubjectKind = subjectKind,
            SubjectId = "execution-1", StreamId = Guid.NewGuid(), Channel = NativeRecordChannel.SessionState,
            RangeKind = CaptureGapRangeKind.Ordinal, RangeStart = 512, RangeEnd = 6144,
            Reason = CaptureGapReason.BoundExceeded,
            ReasonDetail = "the session-state channel passed its configured capture cap",
            CaptureSource = "harness-native", NoticedAt = now, Resolution = CaptureGapResolution.Open,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now,
        };
    }

    private static WorkflowRunDataManifest Manifest(RunWorld world, string facet)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowRunDataManifest
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, WorkflowRunId = world.RunId, Facet = facet,
            ExpectedRecordCount = 3, PresentRecordCount = 3, KnownMissingCount = 0,
            Verdict = WorkflowRunCaptureCompleteness.Exact, Revision = 1,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now, LastModifiedAt = now,
        };
    }

    private sealed record RunWorld(Guid RunId, Guid TeamId);
}
