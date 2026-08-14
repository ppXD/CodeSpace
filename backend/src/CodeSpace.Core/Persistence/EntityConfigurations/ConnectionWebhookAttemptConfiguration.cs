using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class ConnectionWebhookAttemptConfiguration : IEntityTypeConfiguration<ConnectionWebhookAttempt>
{
    public void Configure(EntityTypeBuilder<ConnectionWebhookAttempt> builder)
    {
        builder.HasKey(a => a.Id);

        // Cascade matches the FK in DbUp 0121, for the same reason its repository twin does: the
        // attempt log is diagnostics FOR the hook row, and retiring a hook must not trip an FK.
        builder.HasOne(a => a.Webhook).WithMany().HasForeignKey(a => a.ConnectionWebhookId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(a => a.RequestHeadersJson).HasColumnName("request_headers_json").HasColumnType("jsonb");
    }
}
