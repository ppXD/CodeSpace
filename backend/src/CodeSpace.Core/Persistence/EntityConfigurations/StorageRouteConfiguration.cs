using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class StorageRouteConfiguration : IEntityTypeConfiguration<StorageRoute>
{
    public void Configure(EntityTypeBuilder<StorageRoute> builder)
    {
        builder.ToTable("storage_route", table =>
        {
            table.HasCheckConstraint("ck_storage_route_current_revision", "current_revision > 0");
            table.HasCheckConstraint("ck_storage_route_data_class_type_key", "data_class_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'");
            table.HasCheckConstraint("ck_storage_route_state", "state IN ('Draft', 'Active', 'Disabled', 'Retired')");
        });

        builder.HasKey(route => route.Id);
        builder.HasAlternateKey(route => new { route.TeamId, route.Id }).HasName("ak_storage_route_team_id");
        builder.Property(route => route.DataClassTypeKey).HasMaxLength(128);
        builder.Property(route => route.State).HasConversion<string>().HasMaxLength(16);
        builder.Property(route => route.Xmin)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasOne(route => route.Team).WithMany().HasForeignKey(route => route.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(route => new { route.TeamId, route.DataClassTypeKey }).IsUnique().HasDatabaseName("ux_storage_route_team_data_class");
        builder.HasIndex(route => new { route.TeamId, route.State, route.DataClassTypeKey }).HasDatabaseName("ix_storage_route_team_state_data_class");
    }
}

public sealed class StorageRouteRevisionConfiguration : IEntityTypeConfiguration<StorageRouteRevision>
{
    public void Configure(EntityTypeBuilder<StorageRouteRevision> builder)
    {
        builder.ToTable("storage_route_revision", table =>
        {
            table.HasCheckConstraint("ck_storage_route_revision_number", "revision > 0");
            table.HasCheckConstraint("ck_storage_route_revision_profile_selection", "(profile_revision_mode = 'CurrentAtWrite' AND pinned_profile_revision IS NULL) OR (profile_revision_mode = 'Pinned' AND pinned_profile_revision IS NOT NULL AND pinned_profile_revision > 0)");
        });

        builder.HasKey(revision => revision.Id);
        builder.Property(revision => revision.ProfileRevisionMode).HasConversion<string>().HasMaxLength(24);

        builder.HasOne(revision => revision.Route)
            .WithMany(route => route.Revisions)
            .HasForeignKey(revision => new { revision.TeamId, revision.StorageRouteId })
            .HasPrincipalKey(route => new { route.TeamId, route.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(revision => revision.Profile)
            .WithMany()
            .HasForeignKey(revision => new { revision.TeamId, revision.StorageProfileId })
            .HasPrincipalKey(profile => new { profile.TeamId, profile.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // DbUp is authoritative for the deferred route-current pointer and the nullable exact pinned-profile FK.
        // Mapping either as an EF navigation would create a circular insert graph or weaken CurrentAtWrite semantics.

        builder.HasIndex(revision => new { revision.TeamId, revision.StorageRouteId, revision.Revision }).IsUnique().HasDatabaseName("ux_storage_route_revision_number");
        builder.HasIndex(revision => new { revision.TeamId, revision.StorageProfileId, revision.CreatedDate, revision.Id }).HasDatabaseName("ix_storage_route_revision_team_profile_created");
    }
}
