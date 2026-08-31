using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class LegacyPlacementAdoptionArcConfiguration : IEntityTypeConfiguration<LegacyPlacementAdoptionArc>
{
    public void Configure(EntityTypeBuilder<LegacyPlacementAdoptionArc> builder)
    {
        builder.ToTable("legacy_placement_adoption_arc", table =>
        {
            table.HasCheckConstraint("ck_legacy_adoption_arc_phase", "phase IN ('Evidence', 'Minting', 'Cleaning')");
            table.HasCheckConstraint("ck_legacy_adoption_arc_state", "state IN ('Active', 'Cleaning', 'Completed', 'Expired', 'Stale')");
            table.HasCheckConstraint("ck_legacy_adoption_arc_phase_state", "(state = 'Active' AND phase IN ('Evidence', 'Minting')) OR (state = 'Cleaning' AND phase = 'Cleaning') OR state IN ('Completed', 'Expired', 'Stale')");
            table.HasCheckConstraint("ck_legacy_adoption_arc_final_summary_object", "final_summary_jsonb IS NULL OR jsonb_typeof(final_summary_jsonb) = 'object'");
        });
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Phase).HasConversion<string>().HasMaxLength(16);
        builder.Property(value => value.State).HasConversion<string>().HasMaxLength(16);
        builder.Property(value => value.TerminalState).HasConversion<string>().HasMaxLength(16);
        builder.Property(value => value.FinalSummaryJson).HasColumnName("final_summary_jsonb").HasColumnType("jsonb");
        builder.Property(value => value.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        builder.HasOne(value => value.Team).WithMany().HasForeignKey(value => value.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(value => value.StorageProfile).WithMany()
            .HasForeignKey(value => new { value.TeamId, value.StorageProfileId })
            .HasPrincipalKey(value => new { value.TeamId, value.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(value => value.StorageProfileRevision).WithMany()
            .HasForeignKey(value => new { value.TeamId, value.StorageProfileRevisionId })
            .HasPrincipalKey(value => new { value.TeamId, value.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(value => value.TeamId).IsUnique().HasDatabaseName("ux_legacy_placement_adoption_arc_team_live")
            .HasFilter("state IN ('Active', 'Cleaning')");
        builder.HasIndex(value => new { value.State, value.CompletedAt, value.Id }).HasDatabaseName("ix_legacy_placement_adoption_arc_terminal_cleanup")
            .HasFilter("state IN ('Completed', 'Expired', 'Stale')");
    }
}

public sealed class LegacyPlacementAdoptionMemberConfiguration : IEntityTypeConfiguration<LegacyPlacementAdoptionMember>
{
    public void Configure(EntityTypeBuilder<LegacyPlacementAdoptionMember> builder)
    {
        builder.ToTable("legacy_placement_adoption_member");
        builder.HasKey(value => new { value.ArcId, value.Position });
        builder.Property(value => value.Position).UseIdentityAlwaysColumn();
        builder.Property(value => value.Sha256).HasMaxLength(64);
        builder.HasOne(value => value.Arc).WithMany(value => value.Members).HasForeignKey(value => value.ArcId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(value => new { value.ArcId, value.SourceWorkflowRowId }).IsUnique().HasDatabaseName("ux_legacy_placement_adoption_member_source");
    }
}
