using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class PairedQualificationCellAdmissionConfiguration : IEntityTypeConfiguration<PairedQualificationCellAdmission>
{
    public void Configure(EntityTypeBuilder<PairedQualificationCellAdmission> builder)
    {
        builder.ToTable("paired_qualification_cell_admission");
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.ObservationArm).HasMaxLength(20);
        builder.Property(value => value.TaskId).HasMaxLength(200);
        builder.Property(value => value.Mode).HasMaxLength(40);
        builder.Property(value => value.ResultJson).HasColumnType("jsonb");
        builder.HasIndex(value => new { value.ObservationGroupId, value.ObservationSession, value.ObservationArm, value.TaskId, value.Mode }).IsUnique().HasDatabaseName("uq_paired_qualification_cell_admission_key");
    }
}
