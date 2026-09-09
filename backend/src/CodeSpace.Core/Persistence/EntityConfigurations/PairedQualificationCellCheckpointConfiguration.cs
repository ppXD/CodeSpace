using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class PairedQualificationCellCheckpointConfiguration : IEntityTypeConfiguration<PairedQualificationCellCheckpoint>
{
    public void Configure(EntityTypeBuilder<PairedQualificationCellCheckpoint> builder)
    {
        builder.ToTable("paired_qualification_cell_checkpoint");
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.Kind).HasMaxLength(100);
        builder.Property(value => value.PayloadJson).HasColumnType("jsonb");
        builder.HasIndex(value => new { value.AdmissionId, value.Kind }).IsUnique().HasDatabaseName("uq_paired_qualification_cell_checkpoint_kind");
    }
}
