using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class TeamMembershipConfiguration : IEntityTypeConfiguration<TeamMembership>
{
    public void Configure(EntityTypeBuilder<TeamMembership> builder)
    {
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Role).HasConversion<string>();

        builder.HasOne(m => m.Team).WithMany(t => t.Memberships).HasForeignKey(m => m.TeamId);
        builder.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId);

        builder.HasIndex(m => new { m.TeamId, m.UserId }).IsUnique();
    }
}
