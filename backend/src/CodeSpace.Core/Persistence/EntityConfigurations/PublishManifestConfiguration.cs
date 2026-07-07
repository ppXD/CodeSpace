using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class PublishManifestConfiguration : IEntityTypeConfiguration<PublishManifest>
{
    public void Configure(EntityTypeBuilder<PublishManifest> builder)
    {
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.AcceptanceState).HasConversion<string>().HasMaxLength(20);

        // The property is named PublishStateValue (PublishState is already the enum type's own name) but the
        // column stays "publish_state" — matches the DbUp column and every consumer's raw-SQL projection.
        builder.Property(m => m.PublishStateValue).HasColumnName("publish_state").HasConversion<string>().HasMaxLength(20);

        builder.Property(m => m.ChangedFilesJson).HasColumnName("changed_files_jsonb").HasColumnType("jsonb");

        // The idempotency lock for an agent-scoped row (see PublishManifest's doc comment): a retry / reattach /
        // reconciler re-run upserts this SAME row rather than minting a duplicate branch record.
        builder.HasIndex(m => new { m.AgentRunId, m.RepositoryAlias }).IsUnique().HasFilter("agent_run_id IS NOT NULL");

        // The run-level counterpart for an Integration row (no AgentRunId).
        builder.HasIndex(m => new { m.WorkflowRunId, m.RepositoryAlias }).IsUnique().HasDatabaseName("ux_publish_manifest_integration").HasFilter("kind = 'Integration'");

        // Npgsql xmin concurrency token — see WorkflowRunConfiguration for the rationale.
        builder.Property(m => m.Xmin)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
