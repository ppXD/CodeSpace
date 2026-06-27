using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class AgentSkillBindingConfiguration : IEntityTypeConfiguration<AgentSkillBinding>
{
    public void Configure(EntityTypeBuilder<AgentSkillBinding> builder)
    {
        builder.HasKey(b => b.Id);

        // Column names + FKs + indexes are declared in DbUp 0080; nothing non-conventional to map here
        // (no enum-as-string, no jsonb, no xmin concurrency token — bindings are insert/delete only).
    }
}
