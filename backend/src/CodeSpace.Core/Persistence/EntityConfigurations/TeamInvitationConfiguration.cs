using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class TeamInvitationConfiguration : IEntityTypeConfiguration<TeamInvitation>
{
    public void Configure(EntityTypeBuilder<TeamInvitation> builder)
    {
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Role).HasConversion<string>();
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(16);

        builder.HasOne(i => i.Team).WithMany().HasForeignKey(i => i.TeamId);
        builder.HasOne(i => i.InvitedBy).WithMany().HasForeignKey(i => i.InvitedByUserId);

        builder.HasIndex(i => i.TokenHash).IsUnique();
    }
}
