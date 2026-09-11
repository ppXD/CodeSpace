using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>Column names come from the global <c>UseSnakeCaseNamingConvention()</c>; only the table, the key, the two enum-as-string conversions and the team-scoped FK are declared here. The resource-uniqueness index is a functional one (it folds a nullable key through COALESCE) and therefore lives only in the migration.</summary>
public sealed class AgentRunCleanupReceiptRecordConfiguration : IEntityTypeConfiguration<AgentRunCleanupReceiptRecord>
{
    public void Configure(EntityTypeBuilder<AgentRunCleanupReceiptRecord> builder)
    {
        builder.ToTable("agent_run_cleanup_receipt");
        builder.HasKey(receipt => receipt.Id);
        builder.Property(receipt => receipt.Id).ValueGeneratedNever();
        builder.Property(receipt => receipt.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(receipt => receipt.Outcome).HasConversion<string>().HasMaxLength(16);

        builder.HasOne(receipt => receipt.AgentRun).WithMany()
            .HasForeignKey(receipt => new { receipt.TeamId, receipt.AgentRunId })
            .HasPrincipalKey(run => new { run.TeamId, run.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(receipt => new { receipt.TeamId, receipt.AgentRunId, receipt.Kind }).HasDatabaseName("ix_agent_run_cleanup_receipt_run");
    }
}
