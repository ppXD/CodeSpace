using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class WorkflowRunDataManifestConfiguration : IEntityTypeConfiguration<WorkflowRunDataManifest>
{
    public void Configure(EntityTypeBuilder<WorkflowRunDataManifest> builder)
    {
        builder.ToTable(WorkflowRunDataNames.DataManifest, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_data_manifest_bounds", "present_record_count >= 0 AND known_missing_count >= 0 AND (expected_record_count IS NULL OR expected_record_count >= 0) AND revision > 0 AND schema_version > 0");

            // The fail-closed arm. A complete verdict requires a determinate expectation, everything expected present,
            // and nothing known-missing. An unstated expectation (NULL) lands on the refusing side — a manifest that
            // read complete because it could not check would have turned an unknown into a false assurance. The
            // IS NOT NULL is what keeps that true: without it the comparison over a NULL expectation evaluates the
            // constraint to NULL, and PostgreSQL admits exactly the unverifiable claim this line refuses.
            table.HasCheckConstraint("ck_workflow_run_data_manifest_completeness", "verdict NOT IN ('Exact', 'RedactedExact') OR (expected_record_count IS NOT NULL AND present_record_count >= expected_record_count AND known_missing_count = 0)");
            table.HasCheckConstraint("ck_workflow_run_data_manifest_facet", "facet IN ('model-call', 'model-call-attempt', 'model-call-body-capture', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'node-output', 'deliverable', 'capture-gap', 'data-manifest')");
            table.HasCheckConstraint("ck_workflow_run_data_manifest_time", "last_modified_at >= created_at");
            table.HasCheckConstraint("ck_workflow_run_data_manifest_verdict", "verdict IN ('Exact', 'RedactedExact', 'Partial', 'Unavailable', 'Corrupt', 'LegacyUnknown')");
        });
        builder.HasKey(manifest => manifest.Id);

        builder.Property(manifest => manifest.Facet).HasMaxLength(48);
        builder.Property(manifest => manifest.Verdict).HasConversion<string>().HasMaxLength(20);
        builder.Property(manifest => manifest.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasOne<Team>().WithMany().HasForeignKey(manifest => manifest.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WorkflowRun>().WithMany().HasForeignKey(manifest => new { manifest.TeamId, manifest.WorkflowRunId })
            .HasPrincipalKey(run => new { run.TeamId, run.Id }).OnDelete(DeleteBehavior.Restrict);
        // Two rows stating different completeness for the same facet of the same run is the one shape that would make
        // the table unreadable: whoever asked would have to pick, and picking is what this plane exists to stop.
        builder.HasIndex(manifest => new { manifest.TeamId, manifest.WorkflowRunId, manifest.Facet }).IsUnique()
            .HasDatabaseName("ux_workflow_run_data_manifest_facet");
        builder.HasIndex(manifest => new { manifest.WorkflowRunId, manifest.Facet }).HasDatabaseName("ix_workflow_run_data_manifest_run");

        // "Whose record is not complete" is the audit's question, and it must not grow with the runs that are fine.
        builder.HasIndex(manifest => new { manifest.TeamId, manifest.LastModifiedAt, manifest.Id })
            .HasDatabaseName("ix_workflow_run_data_manifest_incomplete").HasFilter("verdict NOT IN ('Exact', 'RedactedExact')");
    }
}
