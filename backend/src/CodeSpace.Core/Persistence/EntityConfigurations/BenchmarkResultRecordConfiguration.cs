using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>Column names come from the global <c>UseSnakeCaseNamingConvention()</c>; only the table, the key, the lengths and the two read paths are declared here.</summary>
public class BenchmarkResultRecordConfiguration : IEntityTypeConfiguration<BenchmarkResultRecord>
{
    public void Configure(EntityTypeBuilder<BenchmarkResultRecord> builder)
    {
        builder.ToTable("benchmark_result");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.SuiteVersion).HasMaxLength(120);
        builder.Property(r => r.TaskId).HasMaxLength(200);
        builder.Property(r => r.Mode).HasMaxLength(40);
        builder.Property(r => r.Harness).HasMaxLength(60);
        builder.Property(r => r.Model).HasMaxLength(200);
        builder.Property(r => r.RunStatus).HasMaxLength(20);
        builder.Property(r => r.ExitReason).HasMaxLength(60);
        builder.Property(r => r.GitSha).HasMaxLength(60);
        builder.Property(r => r.CiRunId).HasMaxLength(40);
        builder.HasIndex(r => new { r.TeamId, r.SuiteVersion, r.CreatedDate });
        builder.HasIndex(r => new { r.TeamId, r.TaskId, r.Mode });
    }
}
