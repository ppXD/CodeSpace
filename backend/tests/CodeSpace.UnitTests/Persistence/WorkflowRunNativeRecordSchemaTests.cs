using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins the LOSSLESS native-record plane and its semantic projection: one row per native frame a harness produced, and
/// one row per normalized event projected FROM those frames. The two tables carry the names the data contract already
/// registers, and both hang off the harness execution identity rather than inventing a second set of nouns.
///
/// <para>The load-bearing shapes are the payload XOR (an absent payload can never be read as an empty frame), the
/// grounding rule (a projection with no source frame is a claim about nothing), and the normalization marker (a parse
/// that dropped or threw is a durable, countable fact instead of a missing row).</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowRunNativeRecordSchemaTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void A_native_record_is_one_frame_of_one_attempt_with_exactly_one_payload_arm()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowRunNativeRecord>(db);

        entity.GetTableName().ShouldBe(WorkflowRunDataNames.NativeRecord);
        entity.GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "AgentRunId", "AttemptId", "Channel", "ContractVersion", "CreatedAt", "Digest", "DigestAlgorithm",
            "ExecutionId", "Id", "IngestedAt", "InlinePayload", "IsFinal", "NativeSchema", "NativeSchemaVersion",
            "NativeType", "Normalization", "NormalizationErrorCode", "NormalizationErrorMessage", "OccurredAt",
            "Ordinal", "PayloadEncoding", "PayloadRefJson", "Redaction", "SizeBytes", "SourceLengthBytes",
            "SourceOffsetBytes", "StreamId", "TeamId",
        }.Order());
        entity.FindProperty(nameof(WorkflowRunNativeRecord.PayloadRefJson))!.GetColumnName().ShouldBe("payload_ref_jsonb");
        entity.FindProperty(nameof(WorkflowRunNativeRecord.OccurredAt))!.IsNullable.ShouldBeTrue(
            customMessage: "the harness's own clock is absent for most frames, and back-filling it from ingestion would invent precision the harness never gave");

        ForeignKey(entity, typeof(WorkflowRunHarnessExecution)).Properties.Select(property => property.Name)
            .ShouldBe(new[] { "TeamId", "ExecutionId", "AgentRunId" },
                customMessage: "the execution's scope key carries the tenant and the Agent Run, so a frame can never be attributed to another team's run");

        var ordinal = Index(entity, "ux_workflow_run_native_record_ordinal");
        ordinal.IsUnique.ShouldBeTrue();
        ordinal.Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "StreamId", "Ordinal" });
        Index(entity, "ix_workflow_run_native_record_unprojected").GetFilter().ShouldBe("normalization <> 'Projected'",
            customMessage: "'which frames could we not interpret' is the question this plane exists to answer, and a full index over every projected frame would grow with the run");

        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_workflow_run_native_record_bounds", "ck_workflow_run_native_record_channel",
            "ck_workflow_run_native_record_digest", "ck_workflow_run_native_record_encoding",
            "ck_workflow_run_native_record_normalization", "ck_workflow_run_native_record_payload",
            "ck_workflow_run_native_record_redaction", "ck_workflow_run_native_record_time",
        }, ignoreOrder: true);
        Constraint(entity, "ck_workflow_run_native_record_payload").ShouldContain("(inline_payload IS NULL) <> (payload_ref_jsonb IS NULL)",
            customMessage: "written as an inequality because that is the only spelling that refuses BOTH payload arms and NEITHER");
        Constraint(entity, "ck_workflow_run_native_record_redaction").ShouldContain("redaction <> 'Withheld' OR inline_payload IS NULL",
            customMessage: "a frame that was deliberately never captured has metadata only, so its payload can never be inline bytes");
        Constraint(entity, "ck_workflow_run_native_record_normalization").ShouldContain("normalization = 'Failed' AND normalization_error_code IS NOT NULL",
            customMessage: "'the parser threw' without a reason is a marker nobody can act on");
    }

    [Fact]
    public void A_semantic_event_must_name_the_frames_it_was_folded_from()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowRunSemanticEvent>(db);

        entity.GetTableName().ShouldBe(WorkflowRunDataNames.SemanticEvent);
        entity.GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "AgentRunId", "CausationId", "ContractVersion", "CorrelationId", "CreatedAt", "EventSchemaVersion",
            "EventType", "ExecutionId", "Id", "ModelCallId", "Necessity", "PayloadRefJson", "ProjectedAt",
            "ProjectionQuality", "SessionId", "SourceNativeRecordIds", "StepId", "TeamId", "ToolCallId", "TurnId",
        }.Order(), customMessage: "every correlation the projection contract carries needs a column, or writing an event silently drops it");

        ForeignKey(entity, typeof(WorkflowRunHarnessExecution)).Properties.Select(property => property.Name)
            .ShouldBe(new[] { "TeamId", "ExecutionId", "AgentRunId" });

        Index(entity, "ix_workflow_run_semantic_event_sources").GetMethod().ShouldBe("gin",
            customMessage: "reading an event's grounding back is a containment question over the array, which only an inverted index answers");
        Index(entity, "ix_workflow_run_semantic_event_qualified").GetFilter().ShouldBe("projection_quality NOT IN ('Exact', 'RedactedExact')");

        entity.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_workflow_run_semantic_event_bounds", "ck_workflow_run_semantic_event_grounding",
            "ck_workflow_run_semantic_event_vocabulary",
        }, ignoreOrder: true);

        var grounding = Constraint(entity, "ck_workflow_run_semantic_event_grounding");
        grounding.ShouldContain("COALESCE(array_length(source_native_record_ids, 1), 0) >= 1",
            customMessage: "array_length of an empty array is NULL and a CHECK that evaluates to NULL is SATISFIED, so without COALESCE this constraint accepts exactly the ungrounded event it exists to refuse");
        grounding.ShouldContain("array_position(source_native_record_ids, NULL::UUID) IS NULL",
            customMessage: "the cast is not decoration — array_position needs the element type to resolve the overload, so an uncast NULL would not compile as a constraint at all");
        Constraint(entity, "ck_workflow_run_semantic_event_bounds").ShouldContain("event_type ~ '^[a-zA-Z][a-zA-Z0-9+.-]*:'",
            customMessage: "an absolute URI is what keeps a harness-specific or operator-defined event from colliding with a first-party one");
    }

    /// <summary>
    /// The schema only exists where DbUp can see it. A file that never ships is indistinguishable from a file that was
    /// never written: <c>PerformUpgrade</c> reports success, and the first frame the executor tries to record is the
    /// only evidence the tables were never created.
    /// </summary>
    [Fact]
    public void Its_migration_travels_with_the_build()
    {
        DbUpRunner.DiscoverScriptNames().ShouldContain(
            name => name.EndsWith("0139_workflow_run_native_record.sql", StringComparison.OrdinalIgnoreCase),
            customMessage: "0139_workflow_run_native_record.sql must be discoverable by DbUp. Migrations are copied next " +
                           "to the assembly by the Content item in CodeSpace.Core.csproj; if this one is not there, a " +
                           "deployed image creates neither table and still reports a successful upgrade.");
    }

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
