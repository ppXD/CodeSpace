using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class OAuthPendingStateConfiguration : IEntityTypeConfiguration<OAuthPendingState>
{
    public void Configure(EntityTypeBuilder<OAuthPendingState> builder)
    {
        // Override the snake_case convention: it would render "OAuthPendingState" as
        // "o_auth_pending_state". The migration uses the readable "oauth_pending_state".
        builder.ToTable("oauth_pending_state");

        builder.HasKey(s => s.State);

        builder.HasOne(s => s.ProviderInstance).WithMany().HasForeignKey(s => s.ProviderInstanceId);
        builder.HasOne(s => s.Team).WithMany().HasForeignKey(s => s.TeamId);
        builder.HasOne(s => s.Initiator).WithMany().HasForeignKey(s => s.InitiatorUserId);
    }
}
