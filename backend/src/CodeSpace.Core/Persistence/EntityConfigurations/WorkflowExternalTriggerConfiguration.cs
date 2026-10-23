using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class WorkflowExternalTriggerConfiguration : IEntityTypeConfiguration<WorkflowExternalTrigger>
{
    public void Configure(EntityTypeBuilder<WorkflowExternalTrigger> builder)
    {
        builder.ToTable("workflow_external_trigger");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.TeamId).HasColumnName("team_id");
        builder.Property(t => t.WorkflowId).HasColumnName("workflow_id");
        builder.Property(t => t.NodeId).HasColumnName("node_id").HasMaxLength(64);
        builder.Property(t => t.Slug).HasMaxLength(64);
        builder.Property(t => t.KeyDigest).HasColumnName("key_digest");
        builder.Property(t => t.KeyCiphertext).HasColumnName("key_ciphertext");
        builder.Property(t => t.CreatedDate).HasColumnName("created_date");
        builder.Property(t => t.CreatedBy).HasColumnName("created_by");
        builder.Property(t => t.LastModifiedDate).HasColumnName("last_modified_date");
        builder.Property(t => t.LastModifiedBy).HasColumnName("last_modified_by");
        builder.Property(t => t.DeletedDate).HasColumnName("deleted_date");

        builder.HasIndex(t => t.Slug).IsUnique().HasFilter("deleted_date IS NULL").HasDatabaseName("uq_wet_slug");
        builder.HasIndex(t => t.KeyDigest).HasDatabaseName("idx_wet_key_digest");
        builder.HasIndex(t => new { t.WorkflowId, t.NodeId }).HasDatabaseName("idx_wet_workflow_node");
        builder.HasIndex(t => t.TeamId).HasDatabaseName("idx_wet_team");
    }
}
