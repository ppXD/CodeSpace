using System.Text;
using System.Text.RegularExpressions;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins the two tables that make a run's record provably complete AND make a gap in it visible — the last two names
/// the run data contract registers and the codebase did not have.
///
/// <para>The load-bearing assertion in this class is the FAIL-CLOSED one: a manifest may not read as complete when it
/// could not determine whether something is present. A manifest that reported complete because it could not check
/// would convert an unknown into a false assurance, which is strictly worse than having no manifest at all — so the
/// indeterminate case (an unstated expectation) is refused by the database rather than rounded up.</para>
///
/// <para>The other half is that a gap can be REPRESENTED at all. A completeness statement computed over a plane with
/// no way to say "I missed something here" would report complete because nothing said otherwise, which is exactly the
/// dishonesty this data plane exists to remove.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowRunDataCompletenessSchemaTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    /// <summary>The migration that moved the per-run rendezvous inside the two functions a producer calls.</summary>
    private const string RendezvousMigration = "0148_workflow_run_data_manifest_advance.sql";
    private const string BodyCaptureMigration = "0151_workflow_run_model_call_body_capture.sql";
    private const string AttemptAttributionMigration = "0155_workflow_run_capture_gap_attempt_attribution.sql";
    private const string InitializationMigration = "0171_workflow_run_data_manifest_initialization.sql";
    /// <summary>The migration that replaced initialization's determinate zero with an indeterminate statement, and taught the advance which NULL absorbs.</summary>
    private const string IndeterminateInitializationMigration = "0172_workflow_run_data_manifest_indeterminate_initialization.sql";
    /// <summary>The migration that gave the maintenance sweep an un-stating it must prove is still warranted, separate from the producer's unconditional one.</summary>
    private const string AbandonedExpectationMigration = "0182_workflow_run_data_manifest_abandoned_expectation.sql";

    private const string SubjectConstraint = "ck_workflow_run_capture_gap_subject";

    /// <summary>
    /// A constraint 0168 ADDs and 0170 DROPs on its way to a renamed replacement, with nothing re-stating it since. The
    /// database does not have it, and the corpus still carries the ADD — which is what makes it the fixture for the
    /// other way a "last writer wins" comparison can be fooled.
    /// </summary>
    private const string RevokedConstraint = "ck_workflow_run_sensitive_record_payload_ciphertext";

    /// <summary>The 0151 spelling of the subject constraint. A later migration supersedes it, and 0151 still carries it — which is exactly what makes it the fixture the detector has to refuse.</summary>
    private const string SupersededSubjectSpelling = "subject_kind IN ('model-call', 'model-call-attempt', 'model-call-body-capture', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'capture-gap', 'data-manifest') AND (subject_id IS NULL OR btrim(subject_id) <> '') AND btrim(capture_source) <> ''";

    [Fact]
    public void A_gap_is_one_known_missing_span_with_a_subject_a_coordinate_a_typed_reason_and_a_notice_time()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowRunCaptureGap>(db);

        entity.GetTableName().ShouldBe(WorkflowRunDataNames.CaptureGap);
        entity.GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "AgentRunId", "AttemptWorkerFenceEpoch", "CaptureSource", "Channel", "CreatedAt", "HarnessExecutionId",
            "HarnessProcessAttemptId", "Id", "NoticedAt", "RangeEnd", "RangeEndedAt", "RangeKind", "RangeStart",
            "RangeStartedAt", "Reason", "ReasonDetail", "RecoveredAt", "RecoveredById", "RecoveredByKind",
            "Resolution", "SchemaVersion", "StreamId", "SubjectId", "SubjectKind", "TeamId", "WorkflowRunId",
        }.Order());

        // The five facts the span has to carry to be worth anything: what was being captured, which stream/channel,
        // where the hole is, why, and when it was noticed.
        entity.FindProperty(nameof(WorkflowRunCaptureGap.SubjectKind))!.IsNullable.ShouldBeFalse();
        entity.FindProperty(nameof(WorkflowRunCaptureGap.RangeKind))!.ClrType.ShouldBe(typeof(CaptureGapRangeKind));
        entity.FindProperty(nameof(WorkflowRunCaptureGap.Reason))!.ClrType.ShouldBe(typeof(CaptureGapReason));
        entity.FindProperty(nameof(WorkflowRunCaptureGap.NoticedAt))!.IsNullable.ShouldBeFalse();
        entity.GetProperties().Where(property => property.ClrType == typeof(string))
            .Select(property => property.GetMaxLength()).ShouldAllBe(length => length != null,
                customMessage: "every text column here must be bounded — an unbounded one is where a producer's whole failed payload ends up inline");

        // No 'Unknown' member, and that is the point: a reason column with an escape hatch collects every gap nobody
        // wanted to classify, and the plane is back to a silence with extra columns.
        Enum.GetNames<CaptureGapReason>().ShouldBe(new[] { "BoundExceeded", "WriteRefused", "ReattachTorn", "FrameUnreadable" });
        Enum.GetNames<CaptureGapRangeKind>().ShouldBe(new[] { "Ordinal", "ByteOffset", "Time", "Unbounded" });
        Enum.GetNames<CaptureGapResolution>().ShouldBe(new[] { "Open", "Recovered" });

        ForeignKey(entity, typeof(WorkflowRunHarnessProcessAttempt)).Properties.Select(property => property.Name).ShouldBe(new[] { "HarnessProcessAttemptId" });

        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_workflow_run_capture_gap_bounds", "ck_workflow_run_capture_gap_channel",
            "ck_workflow_run_capture_gap_attempt_attribution", "ck_workflow_run_capture_gap_owner",
            "ck_workflow_run_capture_gap_range", "ck_workflow_run_capture_gap_reason",
            "ck_workflow_run_capture_gap_resolution", "ck_workflow_run_capture_gap_subject",
            "ck_workflow_run_capture_gap_time",
        }, ignoreOrder: true);

        var probe = Index(entity, "ix_workflow_run_capture_gap_open");
        probe.GetFilter().ShouldBe("resolution = 'Open'",
            customMessage: "the manifest's open-gap probe must stay partial, or asking 'is anything still missing' scans every span ever recovered");
        probe.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "WorkflowRunId", "SubjectKind" });

        var agentRun = Index(entity, "ix_workflow_run_capture_gap_agent_run_noticed");
        agentRun.GetFilter().ShouldBe("agent_run_id IS NOT NULL");
        agentRun.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "AgentRunId", "NoticedAt", "Id" });
    }

    [Fact]
    public void Attempt_attribution_is_an_all_or_none_exact_frozen_coordinate()
    {
        using var db = BuildContext();
        var attribution = Constraint(Entity<WorkflowRunCaptureGap>(db), "ck_workflow_run_capture_gap_attempt_attribution");

        attribution.ShouldContain("harness_execution_id IS NULL AND harness_process_attempt_id IS NULL AND attempt_worker_fence_epoch IS NULL",
            customMessage: "legacy and non-harness gaps stay representable only as a wholly unattributed arm, and OWNER identity is not part of it: the attempt columns hard-reference the very write a refused attempt could not make, so a gap that had to surrender its Agent Run to stay legal could name no run at all");
        attribution.ShouldContain("agent_run_id IS NOT NULL AND harness_execution_id IS NOT NULL AND harness_process_attempt_id IS NOT NULL AND attempt_worker_fence_epoch IS NOT NULL AND attempt_worker_fence_epoch > 0",
            customMessage: "a half-coordinate would make a reader guess which process the gap belongs to");
        attribution.ShouldNotContain("agent_run_id IS NULL",
            customMessage: "no arm may REQUIRE the owner to be absent. That is what forced a refused attempt's gap — whose subject is the attempt row itself — to drop the Agent Run it knew perfectly well");
    }

    /// <summary>
    /// EVERY gap names a run, and both keys it can name one by stay COMPOSITE with the team.
    ///
    /// <para>The workflow run key was NOT NULL, which made a STANDALONE Agent Run's gap unrepresentable — and a plane
    /// that cannot represent an absence is the silence this whole table exists to break, so its producer answered by
    /// recording nothing at all. Nullable alone would have gone one step too far the other way: a row with neither key
    /// is a hole with no address, which is no better than the one that was never written. The CHECK is what makes
    /// "every gap names a run" true; the doc-comment that said so before enforced nothing.</para>
    ///
    /// <para>The composite is the other half. A single-column key would let a gap become readable across teams the
    /// moment its other key went null, so the plane would have traded an unrepresentable gap for a leaked one.</para>
    ///
    /// <para>What this tier can and cannot say: it reads the MODEL, so it catches EF generating the wrong query shape
    /// and nothing else. The database is what refuses the cross-team row, and a migration that wrote either key
    /// single-column would leave every line below green — so the catalog is read back in
    /// <c>WorkflowRunDataCompletenessPersistenceTests.Each_run_key_a_gap_can_name_is_proved_composite_with_its_team</c>,
    /// and that assertion is the enforcing one.</para>
    /// </summary>
    [Fact]
    public void A_gap_names_at_least_one_run_and_each_key_it_can_name_is_team_scoped()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowRunCaptureGap>(db);

        entity.FindProperty(nameof(WorkflowRunCaptureGap.WorkflowRunId))!.IsNullable.ShouldBeTrue(
            customMessage: "a standalone Agent Run has no workflow run, so a NOT NULL key here makes its every known-missing span unrecordable");
        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldContain("ck_workflow_run_capture_gap_owner",
            customMessage: "with the workflow run nullable and nothing demanding the other key, a producer that forgot both writes a hole nobody can locate and the plane admits it");
        Constraint(entity, "ck_workflow_run_capture_gap_owner").ShouldBe("workflow_run_id IS NOT NULL OR agent_run_id IS NOT NULL",
            customMessage: "a gap that names no run is a hole nobody can locate; only the CHECK makes the alternative impossible");

        ForeignKey(entity, typeof(WorkflowRun)).Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "WorkflowRunId" });
        ForeignKey(entity, typeof(AgentRun)).Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "AgentRunId" },
            customMessage: "the Agent Run key carries the same tenant scope the workflow run key does, or a gap keyed only by its Agent Run is one nobody proved belongs to the team reading it");
    }

    /// <summary>
    /// The coordinate arms, stated as schema. Exactly one arm admits any row, so there is no combination of bounds a
    /// writer can land in that means nothing — and a position with no stream to be a position IN is refused, because a
    /// coordinate nobody can locate is not a coordinate.
    /// </summary>
    [Fact]
    public void A_malformed_span_has_no_legal_arm_and_a_positional_range_owes_its_stream()
    {
        using var db = BuildContext();
        var range = Constraint(Entity<WorkflowRunCaptureGap>(db), "ck_workflow_run_capture_gap_range");

        range.ShouldContain("range_kind IN ('Ordinal', 'ByteOffset') AND stream_id IS NOT NULL",
            customMessage: "an ordinal or byte offset with no stream is a position nobody can locate");
        range.ShouldContain("range_start IS NOT NULL AND range_start >= 0");
        range.ShouldContain("range_kind = 'Unbounded' AND range_start IS NULL",
            customMessage: "'I missed something and cannot say where' must be recordable, or a producer fakes a range to get a row in");

        // A PostgreSQL CHECK admits a row that evaluates to TRUE *or NULL*. Written as a bare `range_end >=
        // range_start`, a span with no start evaluates its arm to NULL, every other arm to FALSE, and the constraint to
        // NULL — which ADMITS exactly the malformed span it exists to refuse. This is not style.
        range.ShouldContain("(range_end IS NULL OR range_end >= range_start)",
            customMessage: "the end comparison must be reachable only under a non-null start, or the constraint evaluates to NULL and PASSES");
        range.ShouldContain("range_started_at IS NOT NULL AND (range_ended_at IS NULL OR range_ended_at >= range_started_at)",
            customMessage: "the time arm needs its own IS NOT NULL for the same three-valued-logic reason");
    }

    [Fact]
    public void A_manifest_is_one_completeness_statement_per_facet_of_a_run()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowRunDataManifest>(db);

        entity.GetTableName().ShouldBe(WorkflowRunDataNames.DataManifest);
        entity.GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "CreatedAt", "ExpectedRecordCount", "Facet", "Id", "KnownMissingCount", "LastModifiedAt",
            "PresentRecordCount", "Revision", "SchemaVersion", "TeamId", "Verdict", "WorkflowRunId", "Xmin",
        }.Order());

        // Expected is NULLABLE and that nullability IS the indeterminate state. Zero would be a determinate claim —
        // "this facet is expected to be empty" — and reading an unknown as that claim is the assurance this refuses.
        entity.FindProperty(nameof(WorkflowRunDataManifest.ExpectedRecordCount))!.ClrType.ShouldBe(typeof(long?),
            customMessage: "a non-nullable expectation forces a producer that cannot establish one to invent zero, which reads as 'expected to be empty'");
        entity.FindProperty(nameof(WorkflowRunDataManifest.Verdict))!.ClrType.ShouldBe(typeof(WorkflowRunCaptureCompleteness),
            customMessage: "the verdict reuses the existing capture vocabulary; a parallel one would drift from it");

        var facet = Index(entity, "ux_workflow_run_data_manifest_facet");
        facet.IsUnique.ShouldBeTrue(customMessage: "two rows stating different completeness for the same facet of the same run would make whoever asked pick one");
        facet.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "WorkflowRunId", "Facet" });

        ForeignKey(entity, typeof(WorkflowRun)).Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "WorkflowRunId" });
        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_workflow_run_data_manifest_bounds", "ck_workflow_run_data_manifest_completeness",
            "ck_workflow_run_data_manifest_facet", "ck_workflow_run_data_manifest_time",
            "ck_workflow_run_data_manifest_verdict",
        }, ignoreOrder: true);
    }

    /// <summary>
    /// THE decision this slice exists to make, pinned in the schema that enforces it: an indeterminate may never read
    /// as complete. Only the two states the capture vocabulary already calls strictly readable are complete verdicts,
    /// and each of them requires a determinate expectation, everything expected present, and nothing known-missing.
    /// The remaining four — including <see cref="WorkflowRunCaptureCompleteness.LegacyUnknown"/>, the arm an
    /// indeterminate lands on — are all not-complete, so the direction is enforced even though no constraint can pick
    /// WHICH honest not-complete answer a producer states.
    /// </summary>
    [Fact]
    public void An_indeterminate_expectation_can_never_read_as_a_complete_record()
    {
        using var db = BuildContext();
        var completeness = Constraint(Entity<WorkflowRunDataManifest>(db), "ck_workflow_run_data_manifest_completeness");

        completeness.ShouldContain("verdict NOT IN ('Exact', 'RedactedExact')",
            customMessage: "the two strictly readable states are the only complete verdicts, and they are the only ones this arm constrains");
        completeness.ShouldContain("expected_record_count IS NOT NULL",
            customMessage: "an unstated expectation is the INDETERMINATE case; without this the manifest reads complete because it could not check");
        completeness.ShouldContain("present_record_count >= expected_record_count",
            customMessage: "a shortfall against a stated expectation is not a complete record");
        completeness.ShouldContain("known_missing_count = 0",
            customMessage: "a record with a known-missing span is not complete however many records are present");

        // ...and the same decision in the C# default, because the enum's own default is Exact: a statement nobody
        // filled in must not read as a complete one.
        new WorkflowRunDataManifest().Verdict.ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown);
        new WorkflowRunDataManifest().ExpectedRecordCount.ShouldBeNull();
        new WorkflowRunDataManifest().Verdict.IsStrictlyReadable().ShouldBeFalse();

        WorkflowRunCaptureCompleteness.Exact.IsStrictlyReadable().ShouldBeTrue();
        WorkflowRunCaptureCompleteness.RedactedExact.IsStrictlyReadable().ShouldBeTrue();
        WorkflowRunCaptureCompleteness.Partial.IsStrictlyReadable().ShouldBeFalse();
        WorkflowRunCaptureCompleteness.Unavailable.IsStrictlyReadable().ShouldBeFalse();
        WorkflowRunCaptureCompleteness.Corrupt.IsStrictlyReadable().ShouldBeFalse();
        WorkflowRunCaptureCompleteness.LegacyUnknown.IsStrictlyReadable().ShouldBeFalse();
    }

    /// <summary>
    /// A gap can only be matched to the plane whose record is missing if both tables spell that plane's name the way
    /// the contract does. A registered owner noun with no place in these two constraints is a plane whose absences
    /// cannot be recorded or counted — an invisible gap by omission, which is the failure mode with no symptom.
    /// </summary>
    [Fact]
    public void Every_registered_owner_noun_can_be_named_as_a_subject_and_as_a_facet()
    {
        using var db = BuildContext();
        var subject = Constraint(Entity<WorkflowRunCaptureGap>(db), "ck_workflow_run_capture_gap_subject");
        var recovery = Constraint(Entity<WorkflowRunCaptureGap>(db), "ck_workflow_run_capture_gap_resolution");
        var facet = Constraint(Entity<WorkflowRunDataManifest>(db), "ck_workflow_run_data_manifest_facet");

        foreach (var ownerKind in AllRegisteredOwnerKinds())
        {
            subject.ShouldContain($"'{ownerKind}'", customMessage: $"a gap in the '{ownerKind}' plane has no legal subject_kind, so its absences cannot be recorded at all");
            recovery.ShouldContain($"'{ownerKind}'", customMessage: $"a span recovered by a '{ownerKind}' row cannot cite what covers it");
            facet.ShouldContain($"'{ownerKind}'", customMessage: $"the '{ownerKind}' plane has no legal facet, so its completeness can never be stated");
        }
    }

    /// <summary>
    /// The capture plane's own vocabulary, hard-pinned and shared by both of its facets' gaps — the plane is the
    /// producer that noticed, whichever facet the span belongs to. <c>capture_source</c> is how an auditor asks "which producer
    /// noticed this", so renaming this constant silently retires every filter written against the rows already stored
    /// under the old value — a rename that looks harmless is exactly the kind this pin makes a visible decision.
    /// </summary>
    [Fact]
    public void The_native_record_producers_capture_source_is_pinned()
    {
        NativeRecordPlane.CompletenessCaptureSource.ShouldBe("native-record-plane/v1");
    }

    /// <summary>
    /// The isolation that still holds, checked rather than asserted in prose: SIX production producers and TWO
    /// observation-only bounded readers. Four are in the capture plane; two more only WRITE a gap, on paths that
    /// swallow a storage failure and settle anyway, so the loss is accounted rather than silent. The only files in <c>backend/src</c> that may mention
    /// either table are the two entities, their two configurations, the DbContext that registers them, the shared
    /// completeness writer, the Workflow Run manifest reader, the Agent Run exact-gap reader, and the capture plane's
    /// three capture partials plus the in-process model-call recorder — the native-record, harness-process-attempt,
    /// harness-execution and model-call facets. Each
    /// producer states its own facet, records a gap for
    /// its own refused write, and reads neither table for any decision. The summary reader observes only exact,
    /// team-scoped attribution, orders it deterministically, and takes one more than its display bound to state
    /// truncation without a count or unbounded materialization. In particular nothing in completion, terminal
    /// decision, planner, oracle, critic or routing may read the manifest: making terminal authority answer to it is a
    /// separate, later, deliberate cutover, and this test is what turns that step into a visible red rather than a quiet
    /// import.
    ///
    /// <para>The one MAINTENANCE reader is listed for the same reason and is not an exception to any of it. The
    /// reconciler selects terminal runs whose declared expectation nobody ever met and un-states them through a
    /// conditional seam beside the producers' one; it decides nothing about a run and, decisively, it never advances a
    /// count — a sweep that closed a shortfall by counting would manufacture the complete verdict this whole plane
    /// refuses.</para>
    ///
    /// <para>Adding each producer turns this list red, which is the list working: the count of producers in the
    /// message below is the number a reader can trust without grepping.</para>
    /// </summary>
    [Fact]
    public void Only_the_six_producers_two_bounded_operator_readers_and_the_reconciler_touch_either_table()
    {
        var sourceRoot = ProductionSourceRoot();

        IEnumerable<string> mentions = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => Mentions(File.ReadAllText(path)))
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToList();

        mentions.ShouldBe(new[]
        {
            "AgentRunService.cs",
            "CodeSpaceDbContext.cs",
            "IRunDataCompletenessReader.cs",
            "IRunDataCompletenessWriter.cs",

            // The one MAINTENANCE reader: it selects the terminal runs whose declared expectation nobody ever met and
            // un-states each one through the conditional seam in the writer above -- conditional because it reads in
            // one transaction and writes in another, so the write has to prove the row still says what it said. It
            // reads the manifest to CHOOSE a row and for nothing else, no run's outcome answers to it, and it advances
            // no count -- closing a shortfall by counting is the complete-over-uncounted-data claim the plane exists
            // to refuse.
            "IRunDataManifestReconciler.cs",
            "NativeRecordPlane.Completeness.cs",
            "NativeRecordPlane.ExecutionCompleteness.cs",
            "NativeRecordPlane.ProcessCompleteness.cs",

            // Two producers that WRITE a gap and read neither table. Both sit on a path that deliberately swallows a
            // storage failure and settles anyway -- the engine because the node's side effect already fired, the node
            // observability seam because a completed command must not fail over a lost copy of its own output. Each
            // records the loss so the run cannot also report complete data. Neither consults the manifest for any
            // decision, so the isolation this list protects -- that nothing in completion, terminal decision, planner,
            // oracle, critic or routing may READ it -- is untouched.
            "NodeObservability.cs",
            "RecordingLLMClientDecorator.cs",
            "RunDataFacetAdvance.cs",
            "WorkflowEngine.cs",
            "WorkflowRunCaptureGap.cs",
            "WorkflowRunCaptureGapConfiguration.cs",
            "WorkflowRunDataManifest.cs",
            "WorkflowRunDataManifestConfiguration.cs",
        }, customMessage: "a production file other than the shared completeness writer, the Workflow Run manifest " +
                          "reader, the Agent Run exact-gap reader, and the capture plane's three completeness partials " +
                          "now touches the capture-gap / data-manifest plane. Exactly four producers exist — the " +
                          "native-record, harness-process-attempt, harness-execution and model-call facets — and exactly two bounded, " +
                          "observation-only readers plus one maintenance reconciler exist. No reducer folds a gap and terminal authority does not " +
                          "consult the manifest. Any new producer or reader is a deliberate step that updates this " +
                          "list — not a silent one.");
    }

    /// <summary>
    /// The schema only exists where DbUp can see it. A file that never ships is indistinguishable from one that was
    /// never written: <c>PerformUpgrade</c> reports success, and the first row a later slice writes is the only evidence
    /// neither table was ever created.
    /// </summary>
    [Fact]
    public void Its_migration_travels_with_the_build()
    {
        DbUpRunner.DiscoverScriptNames().ShouldContain(
            name => name.EndsWith("0146_workflow_run_data_completeness.sql", StringComparison.OrdinalIgnoreCase),
            customMessage: "0146_workflow_run_data_completeness.sql must be discoverable by DbUp. Migrations are copied " +
                           "next to the assembly by the Content item in CodeSpace.Core.csproj; if this one is not there, " +
                           "a deployed image creates neither table and still reports a successful upgrade.");
    }

    [Fact]
    public void Its_attempt_attribution_migration_travels_with_the_build()
    {
        DbUpRunner.DiscoverScriptNames().ShouldContain(
            name => name.EndsWith(AttemptAttributionMigration, StringComparison.OrdinalIgnoreCase),
            customMessage: $"{AttemptAttributionMigration} must be discoverable by DbUp or deployed gaps cannot carry the exact process identity the producer now writes.");
    }

    /// <summary>
    /// The DRIFT DETECTOR, and the reason every other assertion in this class is worth anything. Those read the EF
    /// model, but production runs the migrations — the model's check constraints are a MIRROR, never their source. Let
    /// the two diverge and this suite stays green describing a fail-closed rule the database does not have, which is
    /// precisely how a manifest that read complete over an indeterminate would ship with its own pin passing.
    ///
    /// <para>Two things make it able to see that. The corpus is DISCOVERED, so a migration written after this test is
    /// in it without being named here. And the comparison is against the LAST statement of each constraint in DbUp
    /// order, because that is the one the database ends up enforcing: a superseded spelling is still in the corpus, so
    /// "appears somewhere in the concatenated migrations" reads as agreement with a database that has moved on.</para>
    /// </summary>
    [Fact]
    public void Every_modelled_check_constraint_is_spelled_identically_in_its_migration()
    {
        var corpus = MigrationCorpus();

        using var db = BuildContext();
        var modelled = new[] { Entity<WorkflowRunCaptureGap>(db), Entity<WorkflowRunDataManifest>(db) }
            .SelectMany(entity => entity.GetCheckConstraints())
            .ToList();

        modelled.ShouldNotBeEmpty();
        foreach (var constraint in modelled)
            ShouldEqualTheLastMigratedSpelling(corpus, constraint.Name!, constraint.Sql!);
    }

    /// <summary>
    /// The detector's own pin, because a drift detector that cannot fail is worse than none: it is a green light with
    /// a reassuring name. The fixture is a REAL superseded spelling — the one an earlier migration still carries — so
    /// the assertion above it proves the old "appears anywhere in the concatenation" comparison accepted exactly this
    /// text, and the assertion below it proves the replacement refuses it.
    /// </summary>
    [Fact]
    public void A_mirror_left_on_a_superseded_spelling_is_refused_even_though_the_corpus_still_carries_it()
    {
        var corpus = MigrationCorpus();

        WholeCorpusText(corpus).ShouldContain(NormalizeWhitespace(SupersededSubjectSpelling),
            customMessage: "the fixture must be a spelling some migration really carries, or this test proves nothing: what it " +
                           "exists to demonstrate is that a mirror can agree with the corpus somewhere and still disagree " +
                           "with the database.");

        Should.Throw<ShouldAssertException>(() => ShouldEqualTheLastMigratedSpelling(corpus, SubjectConstraint, SupersededSubjectSpelling),
            customMessage: "a mirror one migration behind must be REFUSED. Anything that accepts it is the comparison this " +
                           "detector replaced, and it will be just as green over the next superseded constraint.");
    }

    /// <summary>
    /// The other way last-writer-wins can be fooled, and the one that reads GREEN. A DROP is a writer too: it is the
    /// database FORGETTING a constraint. A detector that only looks for statements skips over it, falls back to the ADD
    /// that DROP revoked, and reports a model in agreement with a rule production stopped enforcing — a mirror checked
    /// against a ghost. Refusing is the only honest answer, because there is no spelling in the database to agree with.
    /// </summary>
    [Fact]
    public void A_constraint_whose_last_word_is_a_bare_DROP_is_refused_rather_than_read_off_the_ADD_it_revoked()
    {
        var corpus = MigrationCorpus();
        var text = WholeCorpusText(corpus);

        var stated = text.LastIndexOf($"CONSTRAINT {RevokedConstraint} CHECK", StringComparison.Ordinal);
        var revoked = text.LastIndexOf($"DROP CONSTRAINT {RevokedConstraint}", StringComparison.Ordinal);

        stated.ShouldBeGreaterThanOrEqualTo(0,
            customMessage: "the fixture must be a constraint some migration really ADDs, or this test proves nothing: an older " +
                           "ADD to fall back ON is the whole hazard being demonstrated.");

        revoked.ShouldBeGreaterThan(stated,
            customMessage: $"'{RevokedConstraint}' must still be DROPped after its last ADD. If a migration has brought the name " +
                           "back, this fixture has stopped being one — pick another revoked constraint rather than deleting the test.");

        Should.Throw<InvalidOperationException>(() => LastStatementOf(corpus, RevokedConstraint),
            customMessage: "a constraint the database no longer has must be REFUSED. Reading its body off the ADD a later DROP " +
                           "revoked is how a model states a rule nothing enforces and this suite calls it agreement.");
    }

    /// <summary>
    /// A DROP revokes exactly the constraint it NAMES. This codebase names constraints by shared prefix, and two live
    /// pairs already differ only by a suffix — <c>ck_workflow_run_harness_execution_terminal</c> against
    /// <c>..._terminal_lease</c>, and the same shape on the process-attempt table. A matcher that stopped at the
    /// shorter name would read the longer one's DROP as the shorter one's, and report a constraint gone that the
    /// database still enforces.
    /// </summary>
    [Fact]
    public void Dropping_a_longer_name_does_not_revoke_the_shorter_one_it_starts_with()
    {
        var script = new MigrationScript("9999_neighbours.sql", "ALTER TABLE t ADD CONSTRAINT ck_terminal CHECK (x > 0);\nALTER TABLE t DROP CONSTRAINT ck_terminal_lease;");

        LastStatementOf(new[] { script }, "ck_terminal").Body.ShouldBe("x > 0",
            customMessage: "'ck_terminal_lease' is a different constraint. Revoking 'ck_terminal' because a longer name that " +
                           "starts with it was dropped is a false alarm about a rule production still has.");
    }

    /// <summary>
    /// Counting DROPs is only safe if a DROP has to be a STATEMENT. This corpus discusses its own DDL in prose — 0145
    /// and 0160 each spell an <c>ALTER TABLE ... DROP CONSTRAINT ck_...</c> inside a comment — so a detector that read
    /// comments would announce a live constraint revoked and send someone to reconcile a model that was already right.
    /// The mirror image matters just as much: <c>--</c> inside a literal is DATA, and cutting the line there would
    /// truncate a constraint body into a comparison that fails for no reason.
    /// </summary>
    [Fact]
    public void A_DROP_written_in_a_comment_is_prose_and_a_dash_dash_inside_a_literal_is_not_a_comment()
    {
        const string script = "-- ALTER TABLE t DROP CONSTRAINT ck_kept;\nALTER TABLE t ADD CONSTRAINT ck_kept CHECK (note <> 'a -- b' AND note <> 'it''s -- fine');";

        var stripped = WithoutLineComments("9999_prose.sql", script);

        stripped.ShouldNotContain("DROP CONSTRAINT",
            customMessage: "a DROP a migration only TALKS about has not revoked anything. Reading prose as DDL turns this " +
                           "detector into a source of false alarms about constraints that are perfectly current.");

        LastStatementOf(new[] { new MigrationScript("9999_prose.sql", stripped) }, "ck_kept").Body
            .ShouldBe("note <> 'a -- b' AND note <> 'it''s -- fine'",
                customMessage: "the literals must survive whole. A '--' inside one is part of the value, and a body cut off at " +
                               "it would be compared against a rule no migration wrote.");

        MigrationCorpus().ShouldAllBe(script => WithoutLineComments(script.Name, script.Text) == script.Text,
            customMessage: "the corpus must ARRIVE stripped — stripped text is a fixed point, unstripped text is not. A helper " +
                           "the read path skips protects nothing: the migrations reach the matcher with their prose intact.");
    }

    /// <summary>
    /// SQL escapes a quote by DOUBLING it, so <c>'it''s'</c> is one literal and not two. A scanner that takes every
    /// apostrophe as a delimiter is a scanner ordinary SQL can desynchronise, and the parentheses it counts afterwards
    /// decide where a constraint body ends — which is the entire comparison this class rests on. The first assertion is
    /// the one that discriminates: the body below survives naive pairing by luck, because an escape's two quotes are
    /// adjacent and the halves happen to re-cover the same span. Luck is not a foundation for a trust check.
    /// </summary>
    [Fact]
    public void A_quote_doubled_inside_a_literal_does_not_end_it()
    {
        const string literal = "'it''s (not) done'";

        ClosingQuote("9999_escaped_quote.sql", literal, 0).ShouldBe(literal.Length - 1,
            customMessage: "a doubled quote is an ESCAPE, not a close. Ending the literal at the first half leaves every " +
                           "parenthesis after it counted on the wrong side of the fence.");

        var script = new MigrationScript("9999_escaped_quote.sql", $"ALTER TABLE t ADD CONSTRAINT ck_escaped CHECK (note <> {literal} AND kind IN ('a', 'b'));");

        LastStatementOf(new[] { script }, "ck_escaped").Body.ShouldBe($"note <> {literal} AND kind IN ('a', 'b')",
            customMessage: "a CHECK body must be read to its OWN closing parenthesis. The parentheses inside the literal are " +
                           "data, and a body cut short at one of them would be compared against a truncated rule.");
    }

    /// <summary>
    /// The stripper understands <c>--</c> and NOTHING else, and this is the pin that keeps that admission honest. A
    /// <c>/* */</c> block survives stripping whole, so a DROP a migration merely TALKS about inside one is read as DDL
    /// — and an apostrophe inside one ("don't", the way prose is written) opens a literal that eats the rest of the
    /// file, the identical desynchronisation the doubled-quote test prevents, arriving through the one door the scanner
    /// does not watch. No migration writes a block comment today, so refusing one costs nothing; the day that changes,
    /// teach <see cref="WithoutLineComments"/> block comments and retire this together with its guard.
    /// </summary>
    [Fact]
    public void A_block_comment_is_refused_because_this_scanner_only_understands_dash_dash()
    {
        const string name = "9999_block_comment.sql";
        const string prose = "ALTER TABLE t ADD CONSTRAINT ck_live CHECK (kind IN ('a'));\n/* don't reinstate what 0170 dropped: ALTER TABLE t DROP CONSTRAINT ck_live; it can't come back */";

        Should.Throw<InvalidOperationException>(() => LastStatementOf(new[] { new MigrationScript(name, WithoutLineComments(name, prose)) }, "ck_live")).Message
            .ShouldContain("DROPped",
                customMessage: "the fixture must really be a hazard, or the refusal below is decoration: a block comment is not " +
                               "stripped at all, so a DROP this migration only TALKS about takes the last word and a live " +
                               "constraint reads as revoked.");

        Should.Throw<InvalidOperationException>(() => WithoutLineComments(name, "/* don't */ ALTER TABLE t ADD CONSTRAINT ck_live CHECK (kind IN ('a'));")).Message
            .ShouldStartWith(name,
                customMessage: "one apostrophe inside a block comment leaves every quote after it paired on the wrong side of " +
                               "the fence. Whatever that costs, it must cost it BY NAME.");

        Should.Throw<InvalidOperationException>(() => ScannableText(name, prose)).Message.ShouldStartWith(name,
            customMessage: "a migration this scanner cannot read must be REFUSED, not scanned anyway. A trust check that " +
                           "mis-parses is worse than none: it answers confidently about rules production may not have.");

        MigrationCorpus().ShouldAllBe(script => ScannableText(script.Name, script.Text) == script.Text,
            customMessage: "no migration writes /* */ today, which is what makes refusing one free — and the corpus must ARRIVE " +
                           "refused rather than merely be refusable, because a guard the read path can skip protects nothing. " +
                           "The day one lands, teach the stripper block comments — do not merely delete what caught it.");
    }

    /// <summary>
    /// The refusal above is scoped to what the scanner actually READS. A <c>/*</c> written inside a <c>--</c> comment is
    /// already handled — the line goes away whole — so refusing it would be a false alarm, and one whose remediation
    /// ("teach the stripper block comments") is advice for a hazard that is not present. A trust check that cries wolf
    /// about correct input gets its guard deleted the first time it blocks someone, which is how the real hazard the
    /// pin exists for walks in later.
    /// </summary>
    [Fact]
    public void A_block_comment_marker_written_inside_a_line_comment_is_read_not_refused()
    {
        const string name = "9999_marker_in_line_comment.sql";
        const string prose = "-- 0170 dropped it; do not /* reinstate */ it here\nALTER TABLE t ADD CONSTRAINT ck_kept CHECK (kind IN ('a'));";

        var scannable = ScannableText(name, prose);

        scannable.ShouldNotContain("/*",
            customMessage: "the stripper removes the whole line, marker included, so there is nothing left for the pin to " +
                           "refuse. A pin that reads the RAW text refuses input the scanner handles perfectly.");

        LastStatementOf(new[] { new MigrationScript(name, scannable) }, "ck_kept").Body.ShouldBe("kind IN ('a')",
            customMessage: "the migration must still be SCANNED after the marker is stripped — being accepted is worth " +
                           "nothing if the constraint it states is then unreadable.");
    }

    /// <summary>
    /// A parse failure has to say WHICH migration it choked on. This corpus is 184 files and discovered rather than
    /// listed, so a bare character offset — into the STRIPPED text at that, which no editor can show — is not a lead but
    /// a scavenger hunt. Both refusals are the detector giving up on one file, so both name it first (house rule 12.10).
    /// </summary>
    [Fact]
    public void A_migration_this_scanner_cannot_parse_is_named_by_the_failure_it_raises()
    {
        const string name = "9999_unparseable.sql";

        Should.Throw<InvalidOperationException>(() => WithoutLineComments(name, "ALTER TABLE t ADD CONSTRAINT ck_x CHECK (note <> 'unclosed);")).Message
            .ShouldStartWith(name,
                customMessage: "an unterminated literal makes a whole FILE unreadable. An offset without the file leaves the " +
                               "reader grepping 183 migrations for a character position.");

        var unclosedBody = new MigrationScript(name, "ALTER TABLE t ADD CONSTRAINT ck_x CHECK (kind IN ('a', 'b');");

        Should.Throw<InvalidOperationException>(() => LastStatementOf(new[] { unclosedBody }, "ck_x")).Message
            .ShouldStartWith(name,
                customMessage: "the same one level down: a CHECK body that never closes belongs to a file, and the message " +
                               "that omits it is the one nobody can act on.");
    }

    /// <summary>
    /// The corpus is discovered rather than listed, and this is the evidence: the migration with the LAST word on the
    /// subject constraint is one no list in this file names. The detector this replaced read three hardcoded files, so
    /// the migration that superseded them was not wrong to it — it was absent, and absence is what a hardcoded corpus
    /// turns into agreement.
    /// </summary>
    [Fact]
    public void The_last_word_on_the_subject_constraint_comes_from_a_migration_no_list_here_names()
    {
        var hardcoded = new[] { "0146_workflow_run_data_completeness.sql", BodyCaptureMigration, AttemptAttributionMigration };

        var last = LastStatementOf(MigrationCorpus(), SubjectConstraint).Script.Name;

        hardcoded.ShouldNotContain(last,
            customMessage: $"the subject constraint's last stater is '{last}', which the hardcoded corpus already named. " +
                           "Either a later migration was reverted, or discovery has stopped reaching past the three files " +
                           "the old detector listed — and a corpus that stops growing stops detecting.");
    }

    /// <summary>
    /// The LOCK DISCIPLINE, as a shape rather than a sentence. 0146's guards take the per-run rendezvous lock in a
    /// BEFORE ROW trigger, which fires after an INSERT's value expressions were already evaluated on the statement
    /// snapshot — so a producer that probes the run's open gaps and THEN writes has its whole statement refused when a
    /// gap commits in between, and the counts it carried are deltas, so a refused statement is a delta lost for good.
    ///
    /// <para>0148 removes the choice instead of documenting it: the lock, the gap probe and the manifest write are one
    /// function whose FIRST statement is the lock, so a caller with no transaction and no lock of its own is correct.
    /// This test is what keeps that true — no C# may take the lock, probe the gap count, or issue DML against the
    /// manifest table, because doing any of the three from C# is how the order becomes choosable again.</para>
    ///
    /// <para>It is a PIN, not a constraint: nothing in the database refuses a hand-written probing INSERT, so this
    /// assertion is the whole enforcement and its failure message has to say what to do instead.</para>
    /// </summary>
    [Fact]
    public void No_production_C_sharp_probes_the_gap_plane_takes_the_rendezvous_or_writes_the_manifest_table()
    {
        var offenders = Directory.EnumerateFiles(ProductionSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Select(path => (Name: Path.GetFileName(path)!, Tokens: RendezvousTokensIn(File.ReadAllText(path))))
            .Where(candidate => candidate.Tokens.Count > 0)
            .Select(candidate => $"{candidate.Name}: {string.Join(", ", candidate.Tokens)}")
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            customMessage: "a production C# file takes the completeness rendezvous lock, probes the open-gap count, or " +
                           "writes the manifest table directly. All three belong inside workflow_run_data_manifest_advance " +
                           "/ workflow_run_data_manifest_unstate_expectation (0148), which take the lock as their own first " +
                           "statement — that is what makes a probe-then-write race unreachable rather than merely " +
                           "documented. Call the function through IRunDataCompletenessWriter instead of restating its SQL.");
    }

    /// <summary>
    /// The DRIFT DETECTOR for the seam above. Moving the lock discipline into the database means the only thing holding
    /// the producer to it is a function NAME resolved at runtime — rename it in its migration and the C# still compiles,
    /// still deploys, and fails at the first batch with <c>42883 function does not exist</c>, where the plane's
    /// containment turns it into a log line and a silently unstated run. Every name is therefore pinned on both sides.
    ///
    /// <para>The sweep's conditional un-stating is pinned the same way and matters MORE, not less: it is the only
    /// caller with no producer behind it to notice, so a 42883 there is a reconciler that quietly stops reconciling
    /// while every tick still reports a clean pass.</para>
    /// </summary>
    [Theory]
    [InlineData(RendezvousMigration, "workflow_run_data_manifest_advance")]
    [InlineData(RendezvousMigration, "workflow_run_data_manifest_unstate_expectation")]
    [InlineData(AbandonedExpectationMigration, "workflow_run_data_manifest_unstate_abandoned_expectation")]
    public void The_rendezvous_taking_functions_are_named_identically_in_their_migration_and_in_the_writer(string migrationName, string function)
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", migrationName));

        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith(migrationName, StringComparison.OrdinalIgnoreCase),
            customMessage: $"{migrationName} must be discoverable by DbUp, or a deployed image has no such function and its every batch is a contained 42883.");
        migration.ShouldContain($"CREATE OR REPLACE FUNCTION {function}",
            customMessage: $"'{function}' is not defined by {migrationName}, so nothing creates the entry point the writer calls");
        // Presence FIRST, and not as ceremony: IndexOf answers -1 for a lock that is not there at all, which is less
        // than every position and would let a migration that took no lock pass the ordering check below.
        var rendezvous = migration.IndexOf("PERFORM workflow_run_data_completeness_lock(team, run)", StringComparison.Ordinal);

        rendezvous.ShouldBeGreaterThan(-1, customMessage: $"{migrationName} takes no per-run rendezvous at all");
        rendezvous.ShouldBeLessThan(migration.IndexOf("UPDATE workflow_run_data_manifest", StringComparison.Ordinal),
            customMessage: $"{migrationName} must rendezvous BEFORE it probes the gap plane or touches a statement. A conditional un-stating is not exempt: its own WHERE clause probes the open-gap count, so without the lock the probe and the write it feeds can be split by a committing gap — the split 0148 exists to make unreachable.");

        File.ReadAllText(Path.Combine(ProductionSourceRoot(), "CodeSpace.Core", "Services", "RunData", "IRunDataCompletenessWriter.cs"))
            .ShouldContain(function,
                customMessage: $"the completeness writer no longer calls '{function}'. The lock discipline lives inside that " +
                               "function, so a producer reaching the manifest by any other route is back to choosing an order " +
                               "it can get wrong.");
    }

    /// <summary>
    /// Both initialization migrations rendezvous before they touch the plane, and both leave an existing statement
    /// alone on replay. The lock ordering is the property neither of them may lose: the gap probe that decides the
    /// minted verdict runs under it, and the guard re-probes underneath the same lock.
    /// </summary>
    [Theory]
    [InlineData(InitializationMigration)]
    [InlineData(IndeterminateInitializationMigration)]
    public void Initialization_rendezvouses_before_it_states_anything_and_never_revises_an_existing_statement(string migrationName)
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, migrationName));

        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith(migrationName, StringComparison.OrdinalIgnoreCase));
        migration.IndexOf("PERFORM workflow_run_data_completeness_lock(team, run)", StringComparison.Ordinal)
            .ShouldBeLessThan(migration.IndexOf("INSERT INTO workflow_run_data_manifest", StringComparison.Ordinal),
                customMessage: "initialization must rendezvous before it probes gaps or states any facet");
        migration.ShouldContain("ON CONFLICT (team_id, workflow_run_id, facet) DO NOTHING",
            customMessage: "replay must preserve both the upstream masked_observed latch and the existing statement revision");
    }

    /// <summary>
    /// What the LIVE initializer states, which is the whole of this defect: a facet it mints is INDETERMINATE, never a
    /// determinate zero under a complete verdict. 0171 shipped the second, and a run that terminalized in bootstrap
    /// read back as a complete and verbatim record for four planes that had counted nothing.
    ///
    /// <para>The latch is pinned beside it because it is what keeps the indeterminate row establishable: without
    /// expectation_declared, 0148's rule that a NULL expectation absorbs would swallow every producer's delta and the
    /// plane would report LegacyUnknown for every run forever.</para>
    /// </summary>
    [Fact]
    public void The_live_initializer_states_an_indeterminate_facet_and_only_an_unstated_expectation_absorbs()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, IndeterminateInitializationMigration));

        migration.ShouldContain("known_missing_count, verdict, masked_observed, expectation_declared, revision");
        migration.ShouldContain("SELECT gen_random_uuid(), team, run, facet_name, NULL::BIGINT, 0, gaps.open_here,",
            customMessage: "a minted statement declares that the facet EXISTS; zero would be the determinate claim that it is expected to be empty");
        migration.ShouldContain("CASE WHEN gaps.open_here > 0 THEN 'Partial' ELSE 'LegacyUnknown' END, FALSE, FALSE,",
            customMessage: "a minted statement has observed no masked bytes and carries no declared expectation; later advances own both monotonic true transitions");
        migration.ShouldContain("expectation_declared = statement.expectation_declared OR expected_delta > 0",
            customMessage: "the latch is what separates an expectation nobody has declared from one that was un-stated, and only the second may absorb");
        migration.ShouldContain("WHEN statement.expected_record_count IS NULL AND (statement.expectation_declared OR expected_delta = 0) THEN statement.verdict",
            customMessage: "an un-stated expectation must still carry its verdict rather than be recomputed from a later partial count");
    }

    /// <summary>The three things a producer must not be able to spell in C#: the rendezvous, the probe the rendezvous protects, and direct DML against the statement it produces.</summary>
    private static IReadOnlyList<string> RendezvousTokensIn(string source) => new[]
        {
            "workflow_run_data_completeness_lock", "workflow_run_capture_gap_open_count",
            $"INSERT INTO {WorkflowRunDataNames.DataManifest}", $"UPDATE {WorkflowRunDataNames.DataManifest}",
        }
        .Where(token => source.Contains(token, StringComparison.OrdinalIgnoreCase))
        .ToList();

    private static bool Mentions(string source) =>
        source.Contains(nameof(WorkflowRunCaptureGap), StringComparison.Ordinal)
        || source.Contains(nameof(WorkflowRunDataManifest), StringComparison.Ordinal)
        || source.Contains(WorkflowRunDataNames.CaptureGap, StringComparison.Ordinal)
        || source.Contains(WorkflowRunDataNames.DataManifest, StringComparison.Ordinal);

    /// <summary>
    /// Walks out of the test assembly's bin directory to <c>backend/src</c>. It FAILS rather than skips when the tree
    /// is not there: a source-isolation check that quietly passes without reading any source is the shape of a test
    /// that cannot fail.
    /// </summary>
    private static string ProductionSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "src");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException($"'backend/src' was not found above '{AppContext.BaseDirectory}', so the isolation of this slice was never checked. Run the unit suite from the repository checkout.");
    }

    /// <summary>
    /// The contract's registered owner NOUNS, read off the noun registry itself rather than derived from the table
    /// names. The two lists agree today, which is exactly what made the derivation look harmless: it left this check
    /// reading a DIFFERENT registry from the one the constraints are written against, so the day a noun and a table
    /// stop being one-to-one the loop would be checking the wrong list without saying so. The swap corrects the source
    /// of truth going forward; it closes no hole that is open today.
    ///
    /// <para>The two run-produced-file nouns are inside its scope now — the registry admits them, so this loop demands
    /// all three constraints name them. What it demands is that a facet can be SPELLED, not that anything states one:
    /// no producer advances either noun and the initializer mints no row for a noun outside its required set.</para>
    /// </summary>
    private static IReadOnlyCollection<string> AllRegisteredOwnerKinds() => WorkflowRunDataOwnerKinds.All;

    /// <summary>One migration as DbUp would apply it: the name it journals, and the text it runs.</summary>
    private sealed record MigrationScript(string Name, string Text);

    /// <summary>
    /// Every migration DbUp would apply, in the order it would apply them, DISCOVERED rather than listed. A hardcoded
    /// corpus is one that silently stops growing: the migration that supersedes a constraint is exactly the one nobody
    /// remembers to add, and the detector then reads a superseded database as the current one.
    /// </summary>
    private static IReadOnlyList<MigrationScript> MigrationCorpus() => DbUpRunner.DiscoverScriptNames()
        .Select(JournalledFileName)
        .Order(StringComparer.Ordinal)
        .Select(name => new MigrationScript(name, ScannableText(name, ReadScript(name))))
        .ToList();

    private static string ReadScript(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, name));

    /// <summary>
    /// One migration as this scanner is ALLOWED to read it. The stripper below understands <c>--</c> and nothing else,
    /// so a <c>/* */</c> block is refused by name rather than scanned around: it survives stripping whole, which turns
    /// any DROP it merely discusses into DDL, and an apostrophe inside one opens a literal that eats the rest of the
    /// file. No migration writes one today, so the refusal costs nothing and buys a loud failure the day that changes —
    /// at which point the answer is to teach <see cref="WithoutLineComments"/> block comments, not to delete this.
    ///
    /// <para>The refusal reads the STRIPPED text, which is the text the scanner goes on to read. A <c>/*</c> written
    /// inside a <c>--</c> comment is already gone by then and was never a hazard, so refusing it would be a false alarm
    /// carrying remediation advice for a problem the migration does not have.</para>
    /// </summary>
    private static string ScannableText(string scriptName, string text)
    {
        // Stripping reads literals to know which `--` is a comment, so a block comment holding an odd number of
        // apostrophes desynchronises it and it dies BEFORE the refusal below is reached. Its message would then name
        // an unterminated literal and send the reader hunting for a quote — the block comment is the real cause, and
        // saying so is the whole value of refusing it.
        string stripped;
        try { stripped = WithoutLineComments(scriptName, text); }
        catch (InvalidOperationException) when (text.Contains("/*", StringComparison.Ordinal)) { throw BlockComment(scriptName); }

        if (stripped.Contains("/*", StringComparison.Ordinal)) throw BlockComment(scriptName);

        return stripped;
    }

    private static InvalidOperationException BlockComment(string scriptName)
    {
        return new InvalidOperationException($"{scriptName}: writes a /* */ block comment, which this scanner does not understand — it would read the block's prose as DDL, and an apostrophe inside it would desynchronise every literal after it. Teach WithoutLineComments block comments before adding this migration.");
    }

    /// <summary>
    /// A migration's text with its line comments removed, because this corpus writes PROSE about dropping constraints:
    /// 0145 and 0160 each spell an <c>ALTER TABLE ... DROP CONSTRAINT ck_...</c> inside a <c>--</c> block, and 0136's
    /// header discusses one it never issues. Once a DROP counts as a word on a constraint, reading those would call a
    /// live constraint revoked. Quoted text is kept whole — a <c>--</c> inside a literal is data, and 0121 and 0173 are
    /// full of it.
    /// </summary>
    private static string WithoutLineComments(string scriptName, string text)
    {
        var kept = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\'')
            {
                var closing = ClosingQuote(scriptName, text, index);
                kept.Append(text[index..(closing + 1)]);
                index = closing;
                continue;
            }

            if (!StartsComment(text, index))
            {
                kept.Append(text[index]);
                continue;
            }

            var newline = text.IndexOf('\n', index);
            if (newline < 0) break;

            index = newline - 1;
        }

        return kept.ToString();
    }

    /// <summary>A <c>--</c> outside a literal runs to the end of its line; the caller has already skipped every literal.</summary>
    private static bool StartsComment(string text, int index) => text[index] == '-' && index + 1 < text.Length && text[index + 1] == '-';

    /// <summary>DbUp journals a file-system script under its bare file name; a provider that ever prefixed one still resolves to the same file here.</summary>
    private static string JournalledFileName(string scriptName) => scriptName.Split('.', '/', '\\')[^2] + ".sql";

    /// <summary>The old comparison's haystack — every migration concatenated — kept only so the fixture can be shown to be in it.</summary>
    private static string WholeCorpusText(IReadOnlyList<MigrationScript> corpus) =>
        NormalizeWhitespace(string.Join(Environment.NewLine, corpus.Select(script => script.Text)));

    private static void ShouldEqualTheLastMigratedSpelling(IReadOnlyList<MigrationScript> corpus, string constraintName, string modelledSql)
    {
        var last = LastStatementOf(corpus, constraintName);

        last.Body.ShouldBe(NormalizeWhitespace(modelledSql),
            customMessage: $"'{constraintName}' in the EF model is not what {last.Script.Name} — the LAST migration to state it — " +
                           "gives the database. The database is what actually enforces this, so a mirror that drifts leaves " +
                           "this suite asserting a constraint production does not have. Reconcile the two spellings, not the test.");
    }

    /// <summary>One migration's word on a constraint, and where it says it: a body when it states one, NULL when it revokes it.</summary>
    private sealed record ConstraintWord(MigrationScript Script, int Index, string? Body);

    /// <summary>
    /// The last migration in DbUp order to have a word on this check constraint, and the body it leaves the database
    /// with. Last writer wins is what the database ends up with, and it is the whole difference between a mirror that
    /// is checked and one that is merely mentioned somewhere in the history.
    ///
    /// <para>A DROP is a writer. When it has the last word the database has NO such constraint, so there is nothing to
    /// compare a mirror against and falling back to the ADD it revoked would manufacture agreement with a ghost.</para>
    /// </summary>
    private static (MigrationScript Script, string Body) LastStatementOf(IReadOnlyList<MigrationScript> corpus, string constraintName)
    {
        var words = corpus.SelectMany(script => WordsOn(script, constraintName)).ToList();

        if (words.Count == 0)
            throw new InvalidOperationException($"no migration states a check constraint named '{constraintName}', so the EF model mirrors something no database was ever given.");

        var last = words[^1];

        if (last.Body is null)
            throw new InvalidOperationException($"'{constraintName}' is DROPped by {last.Script.Name} and no later migration re-states it, so the model states a constraint the database no longer has. Drop it from the model too, or add the migration that brings it back.");

        return (last.Script, last.Body);
    }

    /// <summary>
    /// Every word one migration has on a constraint, in the order it says them. A statement is the inline
    /// <c>CONSTRAINT x CHECK</c> of a CREATE TABLE or the <c>ADD CONSTRAINT x CHECK</c> of an ALTER alike; a DROP is
    /// counted too, because revoking is the other way the database's answer changes. A COMMENT names it without either.
    /// </summary>
    private static IEnumerable<ConstraintWord> WordsOn(MigrationScript script, string constraintName)
    {
        var stated = new Regex($@"\bCONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\(", RegexOptions.IgnoreCase);
        var revoked = new Regex($@"\bDROP\s+CONSTRAINT\s+(IF\s+EXISTS\s+)?{Regex.Escape(constraintName)}\b", RegexOptions.IgnoreCase);

        var statements = stated.Matches(script.Text).Select(match => new ConstraintWord(script, match.Index, BodyAt(script, match)));
        var revocations = revoked.Matches(script.Text).Select(match => new ConstraintWord(script, match.Index, null));

        return statements.Concat(revocations).OrderBy(word => word.Index).ToList();
    }

    /// <summary>The CHECK body the matched statement opens, normalized the way the model's own spelling is.</summary>
    private static string BodyAt(MigrationScript script, Match match) => NormalizeWhitespace(BalancedBody(script.Name, script.Text, match.Index + match.Length - 1));

    /// <summary>
    /// The text between the CHECK's own parentheses. Depth is tracked because a nested <c>IN (...)</c> would otherwise
    /// end the body early, and quoted literals are skipped because a parenthesis inside one is not a parenthesis.
    /// </summary>
    private static string BalancedBody(string scriptName, string text, int openIndex)
    {
        var depth = 0;

        for (var index = openIndex; index < text.Length; index++)
        {
            if (text[index] == '\'')
            {
                index = ClosingQuote(scriptName, text, index);
                continue;
            }

            if (text[index] == '(') depth++;
            if (text[index] == ')' && --depth == 0) return text[(openIndex + 1)..index];
        }

        throw new InvalidOperationException($"{scriptName}: a CHECK body opening at offset {openIndex} of its comment-stripped text is never closed, so this migration does not parse as SQL. Read the file with its comments removed — the offset counts nothing else.");
    }

    /// <summary>
    /// The index of the quote that ENDS this literal. SQL escapes a quote by doubling it, so <c>'it''s'</c> is one
    /// literal and not two, and a scan that stopped at the first half would resume reading the literal's own text as
    /// SQL — parentheses included.
    /// </summary>
    private static int ClosingQuote(string scriptName, string text, int openIndex)
    {
        for (var index = openIndex + 1; index < text.Length; index++)
        {
            if (text[index] != '\'') continue;

            if (index + 1 < text.Length && text[index + 1] == '\'')
            {
                index++;
                continue;
            }

            return index;
        }

        throw new InvalidOperationException($"{scriptName}: a string literal opening at offset {openIndex} is never closed, so this migration does not parse as SQL. Count quotes from there — a doubled '' is one escaped quote, not two.");
    }

    /// <summary>The migration wraps its constraints over several indented lines; the model states them on one. Only the whitespace may differ.</summary>
    private static string NormalizeWhitespace(string sql) => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static CodeSpaceDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options;
        return new CodeSpaceDbContext(options);
    }

    private static IEntityType Entity<TEntity>(CodeSpaceDbContext db) => db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TEntity)).ShouldNotBeNull();
    private static IIndex Index(IEntityType entity, string name) => entity.GetIndexes().Single(index => index.GetDatabaseName() == name);
    private static IForeignKey ForeignKey(IEntityType entity, Type principal) => entity.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == principal);
    private static string Constraint(IEntityType entity, string name) => entity.GetCheckConstraints().Single(constraint => constraint.Name == name).Sql;
}
