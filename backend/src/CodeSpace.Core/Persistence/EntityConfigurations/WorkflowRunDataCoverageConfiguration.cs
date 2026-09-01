using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class WorkflowRunDataCoverageConfiguration : IEntityTypeConfiguration<WorkflowRunDataCoverage>
{
    public void Configure(EntityTypeBuilder<WorkflowRunDataCoverage> builder)
    {
        builder.ToTable(WorkflowRunDataNames.DataCoverage, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_data_coverage_state", "state IN ('Open', 'Sealed') AND ((state = 'Open' AND sealed_at IS NULL) OR (state = 'Sealed' AND sealed_at IS NOT NULL))");
            table.HasCheckConstraint("ck_workflow_run_data_coverage_bounds", "generation > 0 AND revision > 0 AND cardinality(baseline_facets) > 0 AND cardinality(baseline_facets) <= 100 AND array_position(baseline_facets, NULL) IS NULL AND schema_version > 0");
            table.HasCheckConstraint("ck_workflow_run_data_coverage_time", "last_modified_at >= created_at AND (sealed_at IS NULL OR sealed_at >= created_at)");
        });
        builder.HasKey(coverage => coverage.Id);
        builder.Property(coverage => coverage.State).HasMaxLength(12);
        builder.Property(coverage => coverage.BaselineFacets).HasColumnType("character varying(48)[]");
        builder.Property(coverage => coverage.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        builder.HasOne<Team>().WithMany().HasForeignKey(coverage => coverage.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WorkflowRun>().WithMany().HasForeignKey(coverage => new { coverage.TeamId, coverage.WorkflowRunId })
            .HasPrincipalKey(run => new { run.TeamId, run.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(coverage => new { coverage.TeamId, coverage.WorkflowRunId }).IsUnique().HasDatabaseName("ux_workflow_run_data_coverage_run");
    }
}

public sealed class WorkflowRunDataCoverageFacetConfiguration : IEntityTypeConfiguration<WorkflowRunDataCoverageFacet>
{
    public void Configure(EntityTypeBuilder<WorkflowRunDataCoverageFacet> builder)
    {
        builder.ToTable(WorkflowRunDataNames.DataCoverageFacet, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_data_coverage_facet_bounds", "ordinal > 0 AND ordinal <= 100 AND declared_generation > 0 AND schema_version > 0");
            table.HasCheckConstraint("ck_workflow_run_data_coverage_facet_name", "facet IN ('model-call', 'model-call-attempt', 'model-call-body-capture', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'node-output', 'deliverable', 'capture-gap', 'data-manifest')");
        });
        builder.HasKey(member => member.Id);
        builder.Property(member => member.Facet).HasMaxLength(48);
        builder.HasOne<WorkflowRunDataCoverage>().WithMany().HasForeignKey(member => new { member.TeamId, member.WorkflowRunId })
            .HasPrincipalKey(coverage => new { coverage.TeamId, coverage.WorkflowRunId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasAlternateKey(member => new { member.TeamId, member.WorkflowRunId, member.Facet }).HasName("ux_workflow_run_data_coverage_facet");
        builder.HasIndex(member => new { member.TeamId, member.WorkflowRunId, member.Ordinal }).IsUnique().HasDatabaseName("ux_workflow_run_data_coverage_ordinal");
    }
}
