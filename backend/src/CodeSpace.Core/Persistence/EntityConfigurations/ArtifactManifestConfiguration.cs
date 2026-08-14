using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class ArtifactManifestConfiguration : IEntityTypeConfiguration<ArtifactManifest>
{
    public void Configure(EntityTypeBuilder<ArtifactManifest> builder)
    {
        builder.HasKey(m => m.Id);

        // Stored as its string name (matches CaptureIntent/PublishManifest); 20 covers "Document"/"Diagram"/"Dataset"/"Other".
        builder.Property(m => m.Kind).HasConversion<string>().HasMaxLength(20);

        // One row per (attempt, declared path) — a reclaimed re-attach (bumped epoch) captures its own rows.
        builder.HasIndex(m => new { m.AgentRunId, m.FenceEpoch, m.LogicalPath }).IsUnique();
    }
}
