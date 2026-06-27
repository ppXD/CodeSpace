using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class AgentSkillBindingConfiguration : IEntityTypeConfiguration<AgentSkillBinding>
{
    public void Configure(EntityTypeBuilder<AgentSkillBinding> builder)
    {
        builder.HasKey(b => b.Id);

        // Column names + FKs + indexes live in DbUp 0080. Declare the two relationships (no navigation
        // properties — the entities stay thin) so EF knows the binding DEPENDS on its agent + skill and
        // orders a same-SaveChanges insert AFTER them; without this, importing an agent + its skills + the
        // binding in one transaction can flush the binding first and trip the FK. Restrict (never cascade):
        // both definitions soft-delete, so a binding is never orphaned by a hard delete.
        builder.HasOne<AgentDefinition>().WithMany().HasForeignKey(b => b.AgentDefinitionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SkillDefinition>().WithMany().HasForeignKey(b => b.SkillDefinitionId).OnDelete(DeleteBehavior.Restrict);
    }
}
