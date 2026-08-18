using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins the TOOL CALL plane's shape: one logical invocation row and one row per physical try, split along exactly the
/// seam the model-call plane uses, under the two table names the run data contract already registers.
///
/// <para>Everything asserted here is a fact the audit could not answer from data before: tool identity and kind,
/// whether the tool was read-only or side-effecting, the redacted arguments and result as artifact REFERENCES rather
/// than inline blobs, per-attempt timing and outcome, and the retry lineage.</para>
///
/// <para>What is deliberately absent is as load-bearing as what is present: no unique key here dedups an invocation,
/// because <c>tool_call_ledger</c> owns exactly-once, and this plane must not become a second mechanism that also
/// believes it does.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowRunToolCallSchemaTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Tool_call_is_one_logical_invocation_carrying_identity_effect_class_and_referenced_arguments()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowRunToolCall>(db);

        entity.GetTableName().ShouldBe(WorkflowRunDataNames.ToolCall);
        entity.GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "ArgumentsArtifactId", "ArgumentsDigest", "ArgumentsRedaction", "AttemptCount", "CallOrdinal",
            "CaptureCompleteness", "CaptureSource", "CreatedAt", "EffectClass", "ErrorCode", "ErrorMessage",
            "ExecutionAttemptId", "ExecutionAttemptOrdinal", "ExecutionGeneration", "Id", "IterationKey",
            "LastModifiedAt", "ModelCallId", "NextAttemptOrdinal", "NodeId", "PlanVersion", "Purpose",
            "RedactionPolicy", "Revision", "SchemaVersion", "SourceCorrelationId", "SourceKind", "State", "TeamId",
            "TerminalAt", "ToolKind", "ToolName", "ToolNamespace", "WorkPlanId", "WorkUnitContractHash", "WorkUnitId",
            "WorkflowRunId", "Xmin",
        }.Order());

        // Arguments ride as a REFERENCE plus a digest, never as an inline payload column: unbounded content in a hot
        // row is the audit's standing finding about how this plane falls over.
        entity.FindProperty(nameof(WorkflowRunToolCall.ArgumentsArtifactId))!.ClrType.ShouldBe(typeof(Guid?));
        entity.FindProperty(nameof(WorkflowRunToolCall.ArgumentsDigest))!.GetMaxLength().ShouldBe(64);
        entity.GetProperties().Where(property => property.ClrType == typeof(string))
            .Select(property => property.GetMaxLength()).ShouldAllBe(length => length != null,
                customMessage: "every text column on this plane must be bounded — an unbounded one is where an argument blob ends up inline");

        // Three-valued on purpose: a boolean forces an unobserved effect class to lie in one direction or the other.
        entity.FindProperty(nameof(WorkflowRunToolCall.EffectClass))!.ClrType.ShouldBe(typeof(ToolCallEffectClass));
        Enum.GetNames<ToolCallEffectClass>().ShouldBe(new[] { "ReadOnly", "SideEffecting", "Unknown" });

        AlternateKey(entity, "ak_workflow_run_tool_call_scope").Properties.Select(property => property.Name)
            .ShouldBe(new[] { "Id", "TeamId", "WorkflowRunId" });
        ForeignKey(entity, typeof(WorkflowRun)).Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "WorkflowRunId" });
        entity.FindProperty(nameof(WorkflowRunToolCall.WorkflowRunId))!.IsNullable.ShouldBeFalse(
            customMessage: "the plane is keyed as the model-call plane is, which is what lets one reader ask both the same question");

        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_workflow_run_tool_call_capture_completeness", "ck_workflow_run_tool_call_effect_class",
            "ck_workflow_run_tool_call_error", "ck_workflow_run_tool_call_execution_identity",
            "ck_workflow_run_tool_call_head", "ck_workflow_run_tool_call_identity",
            "ck_workflow_run_tool_call_redaction", "ck_workflow_run_tool_call_source_identity",
            "ck_workflow_run_tool_call_state", "ck_workflow_run_tool_call_terminal",
            "ck_workflow_run_tool_call_time", "ck_workflow_run_tool_call_work_unit_identity",
        }, ignoreOrder: true);
        Constraint(entity, "ck_workflow_run_tool_call_head").ShouldContain("next_attempt_ordinal = attempt_count + 1");
        Constraint(entity, "ck_workflow_run_tool_call_identity").ShouldContain("v[1-9][0-9]*$",
            customMessage: "tool_kind must be versioned, or a row read a year from now is interpreted against whatever that tool name has since come to mean");
        Constraint(entity, "ck_workflow_run_tool_call_terminal").ShouldContain("attempt_count > 0",
            customMessage: "an invocation that never ran an attempt must not be closable as a clean Completed");
    }

    /// <summary>
    /// The redaction discipline, stated as schema rather than as a convention a writer may forget. A tool argument can
    /// carry a credential, so referenced bytes must NAME the pass that cleared them — which makes the unredacted path
    /// a failed INSERT rather than a silent success — and absent content may never be claimed as exact content.
    /// </summary>
    [Theory]
    [InlineData(typeof(WorkflowRunToolCall), "ck_workflow_run_tool_call_redaction", "arguments_digest")]
    [InlineData(typeof(WorkflowRunToolCallAttempt), "ck_workflow_run_tool_call_attempt_redaction", "result_digest")]
    [InlineData(typeof(WorkflowRunToolCallAttempt), "ck_workflow_run_tool_call_attempt_redaction", "error_digest")]
    public void Referenced_content_must_name_its_redaction_pass_and_may_never_be_claimed_when_absent(Type clrType, string constraintName, string digestColumn)
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(clrType).ShouldNotBeNull();
        var redaction = Constraint(entity, constraintName);

        redaction.ShouldContain("redaction_policy IS NOT NULL",
            customMessage: "captured bytes must name the pass that cleared them; without it a writer that skipped redaction has a legal row to write");
        redaction.ShouldContain("'Withheld'",
            customMessage: "deliberately-not-captured must be its own state, or it is indistinguishable from not-captured-yet");
        redaction.ShouldContain("capture_completeness NOT IN ('Exact', 'RedactedExact')",
            customMessage: "an Exact claim over content that was never referenced is how missing content gets read as empty content");
        redaction.ShouldContain("capture_completeness <> 'Exact'",
            customMessage: "masked bytes differ from the wire, so RedactedExact is the strongest completeness they support");
        redaction.ShouldContain($"{digestColumn} ~ '^[0-9a-f]{{64}}$'",
            customMessage: "a referenced artifact whose bytes cannot be verified is a reference no audit can trust; the audit's finding was that only an INPUT hash was ever kept");

        // A PostgreSQL CHECK admits a row that evaluates to TRUE *or NULL*. Written without its own IS NOT NULL, the
        // digest comparison on a NULL digest makes the arm NULL, every other arm FALSE, and the constraint NULL — so
        // the constraint ADMITS exactly the unverifiable reference it exists to refuse. This is not style.
        redaction.ShouldContain($"{digestColumn} IS NOT NULL AND {digestColumn} ~",
            customMessage: $"the {digestColumn} comparison must carry its own IS NOT NULL, or a reference with no digest evaluates the constraint to NULL and PASSES");
    }

    /// <summary>
    /// The same three-valued-logic trap on the redaction column itself, which is the one that decides which arm of the
    /// constraint applies at all: with a NULL redaction and a referenced artifact, an unguarded <c>= 'Withheld'</c> or
    /// <c>IN ('None','Masked')</c> leaves the whole constraint NULL, and a reference nobody declared a redaction for is
    /// admitted. Pinned separately because it fails open in the exact case the plane exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(typeof(WorkflowRunToolCall), "ck_workflow_run_tool_call_redaction", "arguments_redaction")]
    [InlineData(typeof(WorkflowRunToolCallAttempt), "ck_workflow_run_tool_call_attempt_redaction", "result_redaction")]
    public void Every_redaction_arm_is_null_safe_because_a_check_that_evaluates_to_null_admits_the_row(Type clrType, string constraintName, string redactionColumn)
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(clrType).ShouldNotBeNull();
        var redaction = Constraint(entity, constraintName);

        redaction.ShouldContain($"{redactionColumn} IS NOT NULL AND {redactionColumn} = 'Withheld'",
            customMessage: $"the Withheld arm must guard {redactionColumn} against NULL, or the arm evaluates to NULL and the constraint admits the row");
        redaction.ShouldContain($"{redactionColumn} IS NOT NULL AND {redactionColumn} IN ('None', 'Masked')",
            customMessage: $"the captured arm must guard {redactionColumn} against NULL, or an artifact referenced with no declared redaction is admitted — the exact leak this plane exists to prevent");
    }

    [Fact]
    public void Attempt_is_one_physical_try_with_contiguous_ordinals_one_in_flight_and_a_retry_lineage()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowRunToolCallAttempt>(db);

        entity.GetTableName().ShouldBe(WorkflowRunDataNames.ToolCallAttempt);
        entity.GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "AttemptOrdinal", "CaptureCompleteness", "CaptureSource", "CompletedAt", "CreatedAt",
            "EndpointFingerprint", "ErrorArtifactId", "ErrorCode", "ErrorDigest", "ErrorMessage", "Id", "InvocationId",
            "LastModifiedAt", "RedactionPolicy", "ResultArtifactId", "ResultDigest", "ResultRedaction",
            "RetryOfAttemptId", "RetryReason", "Revision", "SchemaVersion", "StartedAt", "Status", "TeamId",
            "ToolCallId", "TransportKind", "WorkflowRunId", "Xmin",
        }.Order());

        // Per-attempt timing and outcome, which is what makes a retry auditable instead of a last-write-wins summary.
        entity.FindProperty(nameof(WorkflowRunToolCallAttempt.CompletedAt))!.ClrType.ShouldBe(typeof(DateTimeOffset?));
        entity.FindProperty(nameof(WorkflowRunToolCallAttempt.Status))!.ClrType.ShouldBe(typeof(ToolCallAttemptStatus));

        // The approval states stay in tool_call_ledger. Mirroring them here would make this plane a second governance
        // mechanism, and two mechanisms both believing they own exactly-once is worse than one.
        Enum.GetNames<ToolCallAttemptStatus>().ShouldNotContain("AwaitingApproval");
        Enum.GetNames<ToolCallAttemptStatus>().ShouldNotContain("Expired");
        Enum.GetNames<ToolCallAttemptStatus>().ShouldContain("Indeterminate",
            customMessage: "a try whose effect may or may not have landed must not be collapsible into Failed");

        ForeignKey(entity, typeof(WorkflowRunToolCall)).Properties.Select(property => property.Name)
            .ShouldBe(new[] { "ToolCallId", "TeamId", "WorkflowRunId" });
        AlternateKey(entity, "ak_workflow_run_tool_call_attempt_scope").Properties.Select(property => property.Name)
            .ShouldBe(new[] { "Id", "TeamId", "ToolCallId" });
        entity.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(WorkflowRunToolCallAttempt))
            .Properties.Select(property => property.Name).ShouldBe(new[] { "RetryOfAttemptId", "TeamId", "ToolCallId" },
                customMessage: "a retry must be provably of the SAME logical call, or the lineage can point anywhere");

        var ordinal = Index(entity, "ux_workflow_run_tool_call_attempt_ordinal");
        ordinal.IsUnique.ShouldBeTrue();
        ordinal.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "ToolCallId", "AttemptOrdinal" });

        var inFlight = Index(entity, "ux_workflow_run_tool_call_attempt_in_flight");
        inFlight.IsUnique.ShouldBeTrue(customMessage: "the one-in-flight invariant has no concurrency backstop unless this index is unique");
        inFlight.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "ToolCallId" });
        inFlight.GetFilter().ShouldBe("status IN ('Pending', 'Running')");

        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_workflow_run_tool_call_attempt_bounds", "ck_workflow_run_tool_call_attempt_capture_completeness",
            "ck_workflow_run_tool_call_attempt_error", "ck_workflow_run_tool_call_attempt_identity",
            "ck_workflow_run_tool_call_attempt_redaction", "ck_workflow_run_tool_call_attempt_retry",
            "ck_workflow_run_tool_call_attempt_status", "ck_workflow_run_tool_call_attempt_terminal",
            "ck_workflow_run_tool_call_attempt_time",
        }, ignoreOrder: true);
        Constraint(entity, "ck_workflow_run_tool_call_attempt_retry").ShouldContain("attempt_ordinal > 1",
            customMessage: "the first try retries nothing, so a self-declared retry at ordinal one is a forged lineage");
        Constraint(entity, "ck_workflow_run_tool_call_attempt_terminal").ShouldContain("error_code IS NOT NULL",
            customMessage: "a non-succeeded terminal owes a typed reason, or an unknown outcome reads as a clean one");
    }

    /// <summary>
    /// The plane must NOT carry an invocation-level uniqueness of its own. <c>ux_tool_call_ledger_run_key</c> is the
    /// single exactly-once authority for a side-effecting call; a second unique key over tool identity or arguments
    /// here would be a rival dedup that silently disagrees with it. Every unique key this plane does hold is scoped to
    /// a row's own position (its ordinal, its liveness, its source or fabric id) — never to what the tool was asked.
    /// </summary>
    [Fact]
    public void Nothing_here_deduplicates_an_invocation_because_the_ledger_owns_exactly_once()
    {
        using var db = BuildContext();
        var call = Entity<WorkflowRunToolCall>(db);
        var attempt = Entity<WorkflowRunToolCallAttempt>(db);

        var uniqueColumns = call.GetIndexes().Where(index => index.IsUnique)
            .Concat(attempt.GetIndexes().Where(index => index.IsUnique))
            .SelectMany(index => index.Properties.Select(property => property.Name))
            .Distinct()
            .ToList();

        uniqueColumns.ShouldNotContain(nameof(WorkflowRunToolCall.ArgumentsDigest),
            customMessage: "a unique key over the arguments digest would be a second exactly-once mechanism racing the ledger's");
        uniqueColumns.ShouldNotContain(nameof(WorkflowRunToolCall.ToolKind));
        uniqueColumns.ShouldNotContain(nameof(WorkflowRunToolCall.ToolName));
        uniqueColumns.ShouldNotContain(nameof(WorkflowRunToolCall.CallOrdinal),
            customMessage: "the call ordinal is ordering evidence, not an admission key — pinning it unique would make a re-observed call unwritable");

        // The projection HAS an idempotent admission key, which is a different thing: it deduplicates the row, never
        // the side effect. Its source is the ledger row itself when the ledger is what was projected.
        var source = Index(call, "ux_workflow_run_tool_call_source_identity");
        source.IsUnique.ShouldBeTrue();
        source.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "WorkflowRunId", "SourceKind", "SourceCorrelationId" });
        source.GetFilter().ShouldBe("source_correlation_id IS NOT NULL");
    }

    /// <summary>
    /// The schema only exists where DbUp can see it. A file that never ships is indistinguishable from a file that was
    /// never written: <c>PerformUpgrade</c> reports success, and the first row a later slice writes is the only
    /// evidence neither table was ever created.
    /// </summary>
    [Fact]
    public void Its_migration_travels_with_the_build()
    {
        DbUpRunner.DiscoverScriptNames().ShouldContain(
            name => name.EndsWith("0141_workflow_run_tool_call.sql", StringComparison.OrdinalIgnoreCase),
            customMessage: "0141_workflow_run_tool_call.sql must be discoverable by DbUp. Migrations are copied next to " +
                           "the assembly by the Content item in CodeSpace.Core.csproj; if this one is not there, a " +
                           "deployed image creates neither table and still reports a successful upgrade.");
    }

    /// <summary>
    /// The DRIFT DETECTOR, and the reason every other test in this class is worth anything. Those tests read the EF
    /// model, but production runs 0141 — the model's check constraints are a MIRROR of the migration's, never their
    /// source. Let the two diverge and this suite stays green describing constraints the database does not have, which
    /// is precisely how a leaky redaction check would ship while its pin still passed.
    /// </summary>
    [Fact]
    public void Every_modelled_check_constraint_is_spelled_identically_in_its_migration()
    {
        var migration = NormalizeWhitespace(File.ReadAllText(MigrationPath()));

        using var db = BuildContext();
        var modelled = new[] { Entity<WorkflowRunToolCall>(db), Entity<WorkflowRunToolCallAttempt>(db) }
            .SelectMany(entity => entity.GetCheckConstraints())
            .ToList();

        modelled.ShouldNotBeEmpty();
        foreach (var constraint in modelled)
        {
            migration.ShouldContain(NormalizeWhitespace(constraint.Sql!),
                customMessage: $"'{constraint.Name}' differs between the EF model and 0141. The migration is what the " +
                               "database actually enforces, so a mirror that drifts leaves this suite asserting a " +
                               "constraint production does not have. Reconcile the two spellings, not the test.");
        }
    }

    private static string MigrationPath() => Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", "0141_workflow_run_tool_call.sql");

    /// <summary>The migration wraps its constraints over several indented lines; the model states them on one. Only the whitespace may differ.</summary>
    private static string NormalizeWhitespace(string sql) => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static CodeSpaceDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options;
        return new CodeSpaceDbContext(options);
    }

    private static IEntityType Entity<TEntity>(CodeSpaceDbContext db) => db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TEntity)).ShouldNotBeNull();
    private static IIndex Index(IEntityType entity, string name) => entity.GetIndexes().Single(index => index.GetDatabaseName() == name);
    private static IKey AlternateKey(IEntityType entity, string name) => entity.GetKeys().Single(key => key.GetName() == name);
    private static IForeignKey ForeignKey(IEntityType entity, Type principal) => entity.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == principal);
    private static string Constraint(IEntityType entity, string name) => entity.GetCheckConstraints().Single(constraint => constraint.Name == name).Sql;
}
