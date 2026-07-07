using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class SupervisorTapeSummaryRecordConfiguration : IEntityTypeConfiguration<SupervisorTapeSummaryRecord>
{
    public void Configure(EntityTypeBuilder<SupervisorTapeSummaryRecord> builder)
    {
        // The entity is named …Record (the digest ROW), but the table is supervisor_tape_summary — map it explicitly
        // so EF's name convention doesn't default to supervisor_tape_summary_record (which 0095 never creates).
        builder.ToTable("supervisor_tape_summary");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.SupervisorRunId).HasColumnName("supervisor_run_id");
        builder.Property(r => r.UpToSequence).HasColumnName("up_to_sequence");

        // One rolling digest per run — the upsert's identity.
        builder.HasIndex(r => r.SupervisorRunId).IsUnique();
    }
}
