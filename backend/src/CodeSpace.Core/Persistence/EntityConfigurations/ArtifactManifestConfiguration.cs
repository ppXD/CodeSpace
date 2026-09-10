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

        // At most one CURRENT row per (attempt, epoch, declared path) is DB-enforced here — a changed re-capture
        // within the same epoch appends and retires the prior via the supersession pointer, so uniqueness binds
        // only unsuperseded rows. The stronger invariant this store actually maintains — at most one current row
        // per (attempt, declared path) ACROSS epochs, so a reclaimed re-attach's fresh capture supersedes the
        // epoch it replaced — is store-enforced (ArtifactManifestStore.UpsertAsync), not by this index.
        builder.HasIndex(m => new { m.AgentRunId, m.FenceEpoch, m.LogicalPath }).IsUnique().HasFilter("superseded_by_manifest_id IS NULL");
    }
}
