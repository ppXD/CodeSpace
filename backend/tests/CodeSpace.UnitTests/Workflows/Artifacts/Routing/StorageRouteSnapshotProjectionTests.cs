using System.Runtime.CompilerServices;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Routing;

[Trait("Category", "Unit")]
public sealed class StorageRouteSnapshotProjectionTests
{
    [Fact]
    public void Contract_is_team_and_data_class_scoped_closed_and_secret_free()
    {
        typeof(IStorageRouteSnapshotResolver).GetMethods().Select(method => method.Name).ShouldBe(["ResolveAsync"]);
        typeof(IStorageRouteSnapshotResolver).GetMethod("ResolveAsync")!.GetParameters().Length.ShouldBe(2);
        typeof(StorageRouteSnapshotRequest).GetProperties().Select(property => property.Name).ShouldBe(["TeamId", "DataClassTypeKey"]);
        typeof(StorageRouteSnapshot).GetProperties().Select(property => property.Name).ShouldBe([
            "RouteId", "RouteRevision", "DataClassTypeKey", "StorageProfileId", "StorageProfileRevision", "ProviderTypeKey", "NamespaceFingerprint",
        ]);

        var resolutionNames = typeof(StorageRouteSnapshotResolution).GetNestedTypes().Select(type => type.Name).OrderBy(name => name).ToArray();
        resolutionNames.ShouldBe([
            "Cancelled", "Invalid", "Missing", "ProfileMissing", "ProfileNotActive", "ProfileRevisionMissing", "Ready", "RouteNotActive", "RouteRevisionMissing",
        ]);
        typeof(StorageRouteSnapshot).GetProperties().Select(property => property.Name).ShouldNotContain(name =>
            name.Contains("Config", StringComparison.OrdinalIgnoreCase) || name.Contains("Credential", StringComparison.OrdinalIgnoreCase) || name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        typeof(StorageRouteSnapshot).GetProperties().ShouldAllBe(property =>
            property.SetMethod != null && property.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)),
            "snapshot coordinates may be assigned only while creating the value and cannot be mutated afterwards");
    }

    [Fact]
    public void Ready_projection_freezes_exact_route_and_profile_revisions()
    {
        var routeId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var row = ReadyRow(routeId, profileId);

        var result = StorageRouteSnapshotProjection.Resolve(row);

        var ready = result.ShouldBeOfType<StorageRouteSnapshotResolution.Ready>();
        ready.Snapshot.ShouldBe(new StorageRouteSnapshot
        {
            RouteId = routeId,
            RouteRevision = 3,
            DataClassTypeKey = "agent-run-log/v1",
            StorageProfileId = profileId,
            StorageProfileRevision = 7,
            ProviderTypeKey = "local-rwx/v1",
            NamespaceFingerprint = Fingerprint('a'),
        });
    }

    [Fact]
    public void Missing_layers_and_inactive_identities_remain_distinct()
    {
        StorageRouteSnapshotProjection.Resolve(null).ShouldBeOfType<StorageRouteSnapshotResolution.Missing>();
        StorageRouteSnapshotProjection.Resolve(ReadyRow() with { RouteIsActive = false }).ShouldBeOfType<StorageRouteSnapshotResolution.RouteNotActive>();
        StorageRouteSnapshotProjection.Resolve(ReadyRow() with { RouteRevisionExists = false }).ShouldBeOfType<StorageRouteSnapshotResolution.RouteRevisionMissing>();
        StorageRouteSnapshotProjection.Resolve(ReadyRow() with { ProfileExists = false }).ShouldBeOfType<StorageRouteSnapshotResolution.ProfileMissing>();
        StorageRouteSnapshotProjection.Resolve(ReadyRow() with { ProfileIsActive = false }).ShouldBeOfType<StorageRouteSnapshotResolution.ProfileNotActive>();
        StorageRouteSnapshotProjection.Resolve(ReadyRow() with { ProfileRevisionExists = false }).ShouldBeOfType<StorageRouteSnapshotResolution.ProfileRevisionMissing>();
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, true, null)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, null)]
    [InlineData(false, true, 0)]
    public void Unknown_or_internally_inconsistent_modes_fail_closed(bool currentAtWrite, bool pinned, int? pinnedRevision)
    {
        var result = StorageRouteSnapshotProjection.Resolve(ReadyRow() with
        {
            ModeIsCurrentAtWrite = currentAtWrite,
            ModeIsPinned = pinned,
            PinnedProfileRevision = pinnedRevision,
        });

        result.ShouldBe(new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.ProfileRevisionMode));
    }

    [Fact]
    public void Unknown_states_provider_keys_and_fingerprints_fail_closed()
    {
        StorageRouteSnapshotProjection.Resolve(ReadyRow() with { RouteStateIsKnown = false })
            .ShouldBe(new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.RouteState));
        StorageRouteSnapshotProjection.Resolve(ReadyRow() with { ProfileStateIsKnown = false })
            .ShouldBe(new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.ProfileState));
        StorageRouteSnapshotProjection.Resolve(ReadyRow() with { ProviderTypeKey = "future-provider" })
            .ShouldBe(new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.ProviderTypeKey));
        StorageRouteSnapshotProjection.Resolve(ReadyRow() with { NamespaceFingerprint = "/srv/plaintext" })
            .ShouldBe(new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.NamespaceFingerprint));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AGENT-RUN-LOG/v1")]
    [InlineData("agent-run-log")]
    [InlineData("agent-run-log/v0")]
    public void Invalid_request_type_keys_are_rejected_before_storage(string typeKey)
    {
        StorageRouteSnapshotProjection.IsValidTypeKey(typeKey).ShouldBeFalse();
    }

    private static StorageRouteSnapshotRow ReadyRow(Guid? routeId = null, Guid? profileId = null) => new()
    {
        RouteId = routeId ?? Guid.NewGuid(),
        RouteRevision = 3,
        DataClassTypeKey = "agent-run-log/v1",
        RouteStateIsKnown = true,
        RouteIsActive = true,
        RouteRevisionExists = true,
        StorageProfileId = profileId ?? Guid.NewGuid(),
        ModeIsCurrentAtWrite = true,
        ModeIsPinned = false,
        PinnedProfileRevision = null,
        ProfileExists = true,
        ProfileStateIsKnown = true,
        ProfileIsActive = true,
        StorageProfileRevision = 7,
        ProfileRevisionExists = true,
        ProviderTypeKey = "local-rwx/v1",
        NamespaceFingerprint = Fingerprint('a'),
    };

    private static string Fingerprint(char value) => $"sha256:{new string(value, 64)}";
}
