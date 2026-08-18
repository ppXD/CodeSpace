using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class WorkflowArtifactConfiguration : IEntityTypeConfiguration<WorkflowArtifact>
{
    public void Configure(EntityTypeBuilder<WorkflowArtifact> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.InlineBytes).HasColumnType("bytea");

        builder.HasOne(a => a.Team).WithMany().HasForeignKey(a => a.TeamId);

        // A routed row's bytes live under an artifact_object owned by the same team. No navigation: the row is a
        // pointer, and the profile revision that a read must resolve through hangs off the object's location, not here.
        builder.HasOne<ArtifactObject>().WithMany()
            .HasForeignKey(a => new { a.TeamId, a.CasArtifactObjectId })
            .HasPrincipalKey(o => new { o.TeamId, o.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // (team_id, sha256) uniqueness mirrors the DB constraint; EF uses this for
        // change-tracker conflict detection.
        builder.HasIndex(a => new { a.TeamId, a.Sha256 }).IsUnique();
    }
}
