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

    [Fact]
    public void A_gap_is_one_known_missing_span_with_a_subject_a_coordinate_a_typed_reason_and_a_notice_time()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowRunCaptureGap>(db);

        entity.GetTableName().ShouldBe(WorkflowRunDataNames.CaptureGap);
        entity.GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "CaptureSource", "Channel", "CreatedAt", "Id", "NoticedAt", "RangeEnd", "RangeEndedAt", "RangeKind",
            "RangeStart", "RangeStartedAt", "Reason", "ReasonDetail", "RecoveredAt", "RecoveredById",
            "RecoveredByKind", "Resolution", "SchemaVersion", "StreamId", "SubjectId", "SubjectKind", "TeamId",
            "WorkflowRunId",
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

        ForeignKey(entity, typeof(WorkflowRun)).Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "WorkflowRunId" });
        entity.FindProperty(nameof(WorkflowRunCaptureGap.WorkflowRunId))!.IsNullable.ShouldBeFalse(
            customMessage: "the plane is keyed as the tool-call plane is, so one reader asks every run-owned plane the same question");

        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_workflow_run_capture_gap_bounds", "ck_workflow_run_capture_gap_channel",
            "ck_workflow_run_capture_gap_range", "ck_workflow_run_capture_gap_reason",
            "ck_workflow_run_capture_gap_resolution", "ck_workflow_run_capture_gap_subject",
            "ck_workflow_run_capture_gap_time",
        }, ignoreOrder: true);

        var probe = Index(entity, "ix_workflow_run_capture_gap_open");
        probe.GetFilter().ShouldBe("resolution = 'Open'",
            customMessage: "the manifest's open-gap probe must stay partial, or asking 'is anything still missing' scans every span ever recovered");
        probe.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "WorkflowRunId", "SubjectKind" });
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
    /// The isolation that still holds, checked rather than asserted in prose: TWO production producers, both in the
    /// capture plane, and STILL zero production readers. The only files in <c>backend/src</c> that may mention either
    /// table are the two entities, their two configurations, the DbContext that registers them, the shared completeness
    /// WRITER every facet's producer states through, and the capture plane's two completeness partials — the
    /// native-record facet and the harness-process-attempt facet, each of which states its own facet, records a gap for
    /// its own refused write, and reads neither table for any decision. In particular nothing in completion, terminal
    /// decision, planner, oracle, critic or routing may read the manifest: making terminal authority answer to it is a
    /// separate, later, deliberate cutover, and this test is what turns that step into a visible red rather than a quiet
    /// import.
    ///
    /// <para>Adding the second producer turned this list red, which is the list working: the count of producers in the
    /// message below is the number a reader can trust without grepping.</para>
    /// </summary>
    [Fact]
    public void Only_the_capture_planes_two_producers_touch_either_table()
    {
        var sourceRoot = ProductionSourceRoot();

        IEnumerable<string> mentions = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => Mentions(File.ReadAllText(path)))
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToList();

        mentions.ShouldBe(new[]
        {
            "CodeSpaceDbContext.cs",
            "IRunDataCompletenessWriter.cs",
            "NativeRecordPlane.Completeness.cs",
            "NativeRecordPlane.ProcessCompleteness.cs",
            "RunDataFacetAdvance.cs",
            "WorkflowRunCaptureGap.cs",
            "WorkflowRunCaptureGapConfiguration.cs",
            "WorkflowRunDataManifest.cs",
            "WorkflowRunDataManifestConfiguration.cs",
        }, customMessage: "a production file other than the shared completeness writer and the capture plane's two " +
                          "completeness partials now reads or writes the capture-gap / data-manifest plane. Exactly two " +
                          "producers exist — the native-record facet and the harness-process-attempt facet — and nothing " +
                          "reads either table: no reducer folds a gap and terminal authority does not consult the " +
                          "manifest. If a third producer or the FIRST READER is genuinely being added, it is a deliberate " +
                          "step that updates this list — not a silent one.");
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

    /// <summary>
    /// The DRIFT DETECTOR, and the reason every other assertion in this class is worth anything. Those read the EF
    /// model, but production runs 0146 — the model's check constraints are a MIRROR, never their source. Let the two
    /// diverge and this suite stays green describing a fail-closed rule the database does not have, which is precisely
    /// how a manifest that read complete over an indeterminate would ship with its own pin passing.
    /// </summary>
    [Fact]
    public void Every_modelled_check_constraint_is_spelled_identically_in_its_migration()
    {
        var migration = NormalizeWhitespace(File.ReadAllText(MigrationPath()) + Environment.NewLine + File.ReadAllText(BodyCaptureMigrationPath()));

        using var db = BuildContext();
        var modelled = new[] { Entity<WorkflowRunCaptureGap>(db), Entity<WorkflowRunDataManifest>(db) }
            .SelectMany(entity => entity.GetCheckConstraints())
            .ToList();

        modelled.ShouldNotBeEmpty();
        foreach (var constraint in modelled)
        {
            migration.ShouldContain(NormalizeWhitespace(constraint.Sql!),
                customMessage: $"'{constraint.Name}' differs from the effective 0146+0151 migration sequence. The database is what " +
                               "database actually enforces, so a mirror that drifts leaves this suite asserting a " +
                               "constraint production does not have. Reconcile the two spellings, not the test.");
        }
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
    /// the producer to it is a function NAME resolved at runtime — rename it in 0148 and the C# still compiles, still
    /// deploys, and fails at the first batch with <c>42883 function does not exist</c>, where the plane's containment
    /// turns it into a log line and a silently unstated run. Both names are therefore pinned on both sides.
    /// </summary>
    [Theory]
    [InlineData("workflow_run_data_manifest_advance")]
    [InlineData("workflow_run_data_manifest_unstate_expectation")]
    public void The_rendezvous_taking_functions_are_named_identically_in_0148_and_in_the_writer(string function)
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", RendezvousMigration));

        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith(RendezvousMigration, StringComparison.OrdinalIgnoreCase),
            customMessage: $"{RendezvousMigration} must be discoverable by DbUp, or a deployed image has neither function and the producer's every batch is a contained 42883.");
        migration.ShouldContain($"CREATE OR REPLACE FUNCTION {function}",
            customMessage: $"'{function}' is not defined by {RendezvousMigration}, so nothing creates the entry point the writer calls");

        File.ReadAllText(Path.Combine(ProductionSourceRoot(), "CodeSpace.Core", "Services", "RunData", "IRunDataCompletenessWriter.cs"))
            .ShouldContain(function,
                customMessage: $"the completeness writer no longer calls '{function}'. The lock discipline lives inside that " +
                               "function, so a producer reaching the manifest by any other route is back to choosing an order " +
                               "it can get wrong.");
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

    /// <summary>The contract's registered nouns, read off the registry so a plane added later cannot be forgotten by both tables at once.</summary>
    private static IReadOnlyList<string> AllRegisteredOwnerKinds() =>
        WorkflowRunDataNames.All.Select(name => name[WorkflowRunDataNames.Prefix.Length..].Replace('_', '-')).ToList();

    private static string MigrationPath() => Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", "0146_workflow_run_data_completeness.sql");
    private static string BodyCaptureMigrationPath() => Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", BodyCaptureMigration);

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
