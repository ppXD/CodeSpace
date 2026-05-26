using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class RoleUserConfiguration : IEntityTypeConfiguration<RoleUser>
{
    public void Configure(EntityTypeBuilder<RoleUser> builder)
    {
        builder.HasKey(ru => ru.Id);
        builder.HasIndex(ru => new { ru.RoleId, ru.UserId }).IsUnique();
        builder.HasIndex(ru => ru.UserId);
    }
}
