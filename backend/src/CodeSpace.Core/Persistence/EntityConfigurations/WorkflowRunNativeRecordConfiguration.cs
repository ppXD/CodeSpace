using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class WorkflowRunNativeRecordConfiguration : IEntityTypeConfiguration<WorkflowRunNativeRecord>
{
    public void Configure(EntityTypeBuilder<WorkflowRunNativeRecord> builder)
    {
        builder.ToTable(WorkflowRunDataNames.NativeRecord, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_native_record_bounds", "ordinal >= 0 AND source_offset_bytes >= 0 AND source_length_bytes >= 0 AND size_bytes >= 0 AND contract_version > 0 AND btrim(native_type) <> '' AND (native_schema IS NULL OR btrim(native_schema) <> '') AND (native_schema_version IS NULL OR btrim(native_schema_version) <> '')");
            table.HasCheckConstraint("ck_workflow_run_native_record_channel", "channel IN ('Stdout', 'Stderr', 'Protocol', 'Control', 'SessionState', 'ModelWire', 'ToolWire', 'Hook', 'Metric', 'Debug')");
            table.HasCheckConstraint("ck_workflow_run_native_record_digest", "digest_algorithm = 'sha256/v1' AND digest ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("ck_workflow_run_native_record_encoding", "payload_encoding IN ('Utf8', 'Base64')");
            table.HasCheckConstraint("ck_workflow_run_native_record_payload", "(inline_payload IS NULL) <> (payload_ref_jsonb IS NULL) AND (payload_ref_jsonb IS NULL OR jsonb_typeof(payload_ref_jsonb) = 'object')");
            table.HasCheckConstraint("ck_workflow_run_native_record_redaction", "redaction IN ('None', 'Masked', 'Withheld') AND (redaction <> 'Withheld' OR inline_payload IS NULL)");
            table.HasCheckConstraint("ck_workflow_run_native_record_normalization", "normalization IN ('Projected', 'Unrecognized', 'NotParsed', 'Failed') AND ((normalization = 'Failed' AND normalization_error_code IS NOT NULL AND btrim(normalization_error_code) <> '') OR (normalization <> 'Failed' AND normalization_error_code IS NULL AND normalization_error_message IS NULL))");
            table.HasCheckConstraint("ck_workflow_run_native_record_time", "created_at >= ingested_at");
        });
        builder.HasKey(record => record.Id);

        builder.Property(record => record.Channel).HasConversion<string>().HasMaxLength(24);
        builder.Property(record => record.NativeType).HasMaxLength(255);
        builder.Property(record => record.NativeSchema).HasMaxLength(255);
        builder.Property(record => record.NativeSchemaVersion).HasMaxLength(64);
        builder.Property(record => record.PayloadRefJson).HasColumnName("payload_ref_jsonb").HasColumnType("jsonb");
        builder.Property(record => record.DigestAlgorithm).HasMaxLength(32);
        builder.Property(record => record.Digest).HasMaxLength(64);
        builder.Property(record => record.PayloadEncoding).HasConversion<string>().HasMaxLength(16);
        builder.Property(record => record.Redaction).HasConversion<string>().HasMaxLength(16);
        builder.Property(record => record.Normalization).HasConversion<string>().HasMaxLength(24);
        builder.Property(record => record.NormalizationErrorCode).HasMaxLength(128);
        builder.Property(record => record.NormalizationErrorMessage).HasMaxLength(2048);

        // Hangs off the EXECUTION's tenant-and-run scope key, so a record can never be attributed to an execution of
        // another team's run. The attempt is a guard-proved soft correlation instead: the attempt table exposes no
        // tenant-scoped key to reference, and widening another slice's table as a rider is not this one's business.
        builder.HasOne(record => record.Execution).WithMany()
            .HasForeignKey(record => new { record.TeamId, record.ExecutionId, record.AgentRunId })
            .HasPrincipalKey(execution => new { execution.TeamId, execution.Id, execution.AgentRunId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(record => new { record.TeamId, record.StreamId, record.Ordinal }).IsUnique()
            .HasDatabaseName("ux_workflow_run_native_record_ordinal");
        // CHANNEL sits ahead of the ingest order because the plane's one read is "how far do this attempt's records on
        // this CHANNEL already reach" — the head an opening resumes above, now asked once per round rather than only on
        // a re-attach. Without it that question is a full walk of the attempt's records with a heap fetch each, i.e.
        // linear in the stdout frames the round recorded; with it, an opening of a channel that has recorded nothing
        // yet reads nothing at all. An attempt's records in ingest order stay answerable on the leading pair.
        builder.HasIndex(record => new { record.TeamId, record.AttemptId, record.Channel, record.IngestedAt, record.Id })
            .HasDatabaseName("ix_workflow_run_native_record_attempt");
        builder.HasIndex(record => new { record.TeamId, record.ExecutionId, record.IngestedAt, record.Id })
            .HasDatabaseName("ix_workflow_run_native_record_execution");

        // The whole point of the plane, as a query: which frames could not be interpreted. Partial, because the answer
        // is rare and an index over every projected frame would grow with the run. It names the two states that ARE the
        // question rather than "not Projected": a frame nobody attempted to interpret (NotParsed — every diagnostic
        // line of every run) is not a frame that could not be interpreted, and indexing those would bury the answer.
        builder.HasIndex(record => new { record.TeamId, record.ExecutionId, record.Id })
            .HasDatabaseName("ix_workflow_run_native_record_unprojected").HasFilter("normalization IN ('Unrecognized', 'Failed')");
    }
}

public sealed class WorkflowRunSemanticEventConfiguration : IEntityTypeConfiguration<WorkflowRunSemanticEvent>
{
    public void Configure(EntityTypeBuilder<WorkflowRunSemanticEvent> builder)
    {
        builder.ToTable(WorkflowRunDataNames.SemanticEvent, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_semantic_event_bounds", "event_schema_version > 0 AND contract_version > 0 AND event_type ~ '^[a-zA-Z][a-zA-Z0-9+.-]*:' AND (payload_ref_jsonb IS NULL OR jsonb_typeof(payload_ref_jsonb) = 'object') AND created_at >= projected_at");

            // COALESCE is load-bearing: array_length('{}'::UUID[], 1) is NULL, and a CHECK that evaluates to NULL is
            // SATISFIED — so the naive `array_length(...) >= 1` accepts exactly the empty array it exists to refuse.
            table.HasCheckConstraint("ck_workflow_run_semantic_event_grounding", "COALESCE(array_length(source_native_record_ids, 1), 0) >= 1 AND array_ndims(source_native_record_ids) = 1 AND array_position(source_native_record_ids, NULL::UUID) IS NULL AND array_position(source_native_record_ids, '00000000-0000-0000-0000-000000000000'::UUID) IS NULL");
            table.HasCheckConstraint("ck_workflow_run_semantic_event_vocabulary", "necessity IN ('Required', 'Ignorable') AND projection_quality IN ('Exact', 'RedactedExact', 'Derived', 'Heuristic', 'Unknown')");
        });
        builder.HasKey(@event => @event.Id);

        builder.Property(@event => @event.EventType).HasMaxLength(512);
        builder.Property(@event => @event.Necessity).HasConversion<string>().HasMaxLength(16);
        builder.Property(@event => @event.ProjectionQuality).HasConversion<string>().HasMaxLength(24);
        builder.Property(@event => @event.PayloadRefJson).HasColumnName("payload_ref_jsonb").HasColumnType("jsonb");

        builder.HasOne(@event => @event.Execution).WithMany()
            .HasForeignKey(@event => new { @event.TeamId, @event.ExecutionId, @event.AgentRunId })
            .HasPrincipalKey(execution => new { execution.TeamId, execution.Id, execution.AgentRunId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(@event => new { @event.TeamId, @event.ExecutionId, @event.ProjectedAt, @event.Id })
            .HasDatabaseName("ix_workflow_run_semantic_event_execution");

        // Reading an event's grounding back is a containment question over the array, which only an inverted index answers.
        builder.HasIndex(@event => @event.SourceNativeRecordIds)
            .HasDatabaseName("ix_workflow_run_semantic_event_sources").HasMethod("gin");
        builder.HasIndex(@event => new { @event.TeamId, @event.ExecutionId, @event.Id })
            .HasDatabaseName("ix_workflow_run_semantic_event_qualified").HasFilter("projection_quality NOT IN ('Exact', 'RedactedExact')");
    }
}
