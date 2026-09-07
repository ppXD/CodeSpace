using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class TaskRouteSnapshotConfiguration : IEntityTypeConfiguration<TaskRouteSnapshot>
{
    public void Configure(EntityTypeBuilder<TaskRouteSnapshot> builder)
    {
        builder.ToTable("task_route_snapshot");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.RouteJson).HasColumnType("jsonb");
        builder.Property(r => r.ResultJson).HasColumnType("jsonb");
        builder.HasIndex(r => r.ConsumedRunId).IsUnique();
        builder.HasIndex(r => r.ExpiresAt);
    }
}
