using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class RepositoryWebhookAttemptConfiguration : IEntityTypeConfiguration<RepositoryWebhookAttempt>
{
    public void Configure(EntityTypeBuilder<RepositoryWebhookAttempt> builder)
    {
        builder.HasKey(a => a.Id);

        // Cascade matches the FK in DbUp 0120: the attempt log is diagnostics FOR the webhook row,
        // and unbind hard-deletes Registered rows — a restrict would break that delete.
        builder.HasOne(a => a.Webhook).WithMany().HasForeignKey(a => a.RepositoryWebhookId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(a => a.RequestHeadersJson).HasColumnName("request_headers_json").HasColumnType("jsonb");
    }
}
