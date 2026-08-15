using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Queries.Storage;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Routing;

public sealed class StorageRouteContractTests
{
    [Fact]
    public void Every_route_request_requires_storage_manage()
    {
        object[] requests =
        [
            new ListStorageRoutePageQuery(), new GetStorageRouteQuery(),
            new CreateStorageRouteCommand { DataClassTypeKey = "artifact-cas/v1", StorageProfileId = Guid.NewGuid() },
            new AppendStorageRouteRevisionCommand { ExpectedXmin = 1, ExpectedCurrentRevision = 1, StorageProfileId = Guid.NewGuid() },
            new SetStorageRouteStateCommand { ExpectedXmin = 1, ExpectedCurrentRevision = 1, State = StorageRouteStateValue.Active },
        ];

        requests.Cast<IRequireTeamPermission>().Select(value => value.RequiredPermission).Distinct().ShouldBe([TeamPermissions.StorageManage]);
    }

    [Fact]
    public void Route_dtos_expose_only_profile_identity_and_revision_references()
    {
        var names = typeof(StorageRouteRevisionDetail).GetProperties().Select(property => property.Name).Order().ToArray();

        names.ShouldBe(new[]
        {
            "CreatedBy", "CreatedDate", "Id", "PinnedProfileRevision", "ProfileRevisionMode", "Revision",
            "StorageProfileId", "StorageProfileStableName",
        }.Order().ToArray());
        names.ShouldNotContain(value => value.Contains("Config", StringComparison.OrdinalIgnoreCase));
        names.ShouldNotContain(value => value.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        names.ShouldNotContain(value => value.Contains("Secret", StringComparison.OrdinalIgnoreCase));

        typeof(StorageRouteDetail).GetProperties().Select(property => property.Name).Order().ShouldBe(new[]
        {
            "CreatedBy", "CreatedDate", "CurrentRevision", "CurrentTarget", "DataClassTypeKey", "Id",
            "LastModifiedBy", "LastModifiedDate", "RevisionPage", "State", "Xmin",
        }.Order().ToArray());
        StorageRouteRevisionPageLimits.DefaultPageSize.ShouldBeLessThanOrEqualTo(25);
        StorageRouteRevisionPageLimits.MaxPageSize.ShouldBe(100);
    }
}
