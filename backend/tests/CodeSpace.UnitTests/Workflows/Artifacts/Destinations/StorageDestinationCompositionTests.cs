using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Destinations;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Destinations;

/// <summary>
/// The order and the branch, pinned without a database.
///
/// <para>Two things here are invisible to the integration tier because both paths end in the same rows: that an
/// already-Active route is NOT re-activated, and that a data class already routed is repointed rather than routed
/// again. Both matter for a reason that costs real time: activating a route writes and discards a real object at the
/// destination, so a redundant activation is a redundant round trip on a screen an operator is waiting on - and
/// creating a route for a class that already has one is refused outright, since a data class carries exactly one
/// route row for the life of the team.</para>
/// </summary>
[Trait("Category", "Unit")]
public class StorageDestinationCompositionTests
{
    private const string DataClass = "workflow-artifact/v1";
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    [Fact]
    public async Task A_new_destination_mints_the_key_before_the_profile_that_pins_it_and_routes_last()
    {
        var log = new List<string>();
        var routes = new RecordingRouteService(log, existing: null);

        var detail = await Service(log, routes).CreateAsync(TeamId, ActorId, Command([DataClass]), CancellationToken.None);

        log.ShouldBe(["credential.create", "profile.create", "profile.activate", "route.lookup", "route.create", "route.activate"]);
        detail.CredentialRevision.ShouldBe(1);
        detail.ProfileId.ShouldBe(RecordingProfileService.ProfileId);
    }

    [Fact]
    public async Task A_data_class_another_destination_already_holds_is_repointed_and_not_re_activated()
    {
        var log = new List<string>();
        var routes = new RecordingRouteService(log, existing: Summary(StorageRouteStateValue.Active, Guid.NewGuid()));

        await Service(log, routes).CreateAsync(TeamId, ActorId, Command([DataClass]), CancellationToken.None);

        log.ShouldBe(["credential.create", "profile.create", "profile.activate", "route.lookup", "route.append"]);
        log.ShouldNotContain("route.create", "a data class carries one route row for the life of the team, so a second create is refused outright");
        log.ShouldNotContain("route.activate", "the route is already Active; re-activating it would write and discard another real object for nothing");
    }

    /// <summary>A Draft route the operator abandoned earlier is repointed AND activated - it is inert until it is.</summary>
    [Fact]
    public async Task A_route_left_inert_by_an_earlier_attempt_is_repointed_and_then_activated()
    {
        var log = new List<string>();
        var routes = new RecordingRouteService(log, existing: Summary(StorageRouteStateValue.Draft, Guid.NewGuid()));

        await Service(log, routes).CreateAsync(TeamId, ActorId, Command([DataClass]), CancellationToken.None);

        log.ShouldBe(["credential.create", "profile.create", "profile.activate", "route.lookup", "route.append", "route.activate"]);
    }

    /// <summary>A route already pointing at this very profile needs no revision at all, only activation if it is inert.</summary>
    [Fact]
    public async Task A_route_already_pointing_here_is_left_alone()
    {
        var log = new List<string>();
        var routes = new RecordingRouteService(log, existing: Summary(StorageRouteStateValue.Active, RecordingProfileService.ProfileId));

        await Service(log, routes).CreateAsync(TeamId, ActorId, Command([DataClass]), CancellationToken.None);

        log.ShouldBe(["credential.create", "profile.create", "profile.activate", "route.lookup"]);
    }

    [Fact]
    public async Task A_destination_with_no_secret_mints_no_credential_and_pins_none()
    {
        var log = new List<string>();
        var profiles = new RecordingProfileService(log);

        var detail = await new StorageDestinationService(new RecordingCredentialService(log), profiles, new RecordingRouteService(log, existing: null))
            .CreateAsync(TeamId, ActorId, Command([]) with { Secret = null }, CancellationToken.None);

        log.ShouldNotContain("credential.create");
        detail.CredentialId.ShouldBeNull();
        profiles.LastCreate.ShouldNotBeNull().CredentialRef.ShouldBeNull("a profile that pinned a credential nobody minted would refuse to activate");
    }

    [Fact]
    public async Task The_same_data_class_named_twice_is_claimed_once()
    {
        var log = new List<string>();

        await Service(log, new RecordingRouteService(log, existing: null)).CreateAsync(TeamId, ActorId, Command([DataClass, DataClass]), CancellationToken.None);

        log.Count(entry => entry == "route.create").ShouldBe(1);
    }

    private static StorageDestinationService Service(List<string> log, IStorageRouteService routes) =>
        new(new RecordingCredentialService(log), new RecordingProfileService(log), routes);

    private static CreateStorageDestinationCommand Command(string[] dataClasses) => new()
    {
        Name = "codespace-artifacts",
        ProviderTypeKey = "local-rwx/v1",
        NonSecretConfig = JsonSerializer.SerializeToElement(new { rootPath = "/srv/artifacts" }),
        Secret = JsonSerializer.SerializeToElement(new { accessKeyId = "id", accessKeySecret = "secret" }),
        DataClassTypeKeys = dataClasses,
    };

    private static StorageRouteSummary Summary(StorageRouteStateValue state, Guid profileId) => new()
    {
        Id = Guid.NewGuid(),
        DataClassTypeKey = DataClass,
        State = state,
        CurrentRevision = 1,
        Xmin = 7,
        StorageProfileId = profileId,
        StorageProfileStableName = "elsewhere",
        ProfileRevisionMode = StorageProfileRevisionModeValue.CurrentAtWrite,
        CreatedDate = DateTimeOffset.UnixEpoch,
        LastModifiedDate = DateTimeOffset.UnixEpoch,
    };

    private sealed class RecordingCredentialService(List<string> log) : IStorageCredentialService
    {
        public static readonly Guid CredentialId = Guid.NewGuid();

        public Task<StorageCredentialMetadata> CreateAsync(Guid teamId, Guid actorId, CreateStorageCredentialCommand command, CancellationToken cancellationToken)
        {
            log.Add("credential.create");
            return Task.FromResult(new StorageCredentialMetadata
            {
                Id = CredentialId, StableName = command.StableName, State = StorageCredentialStateValue.Active,
                CurrentRevision = 1, ProviderTypeKey = command.ProviderTypeKey, CredentialRef = $"db:{CredentialId:D}:1",
                SafeHint = command.SafeHint, CreatedDate = DateTimeOffset.UnixEpoch,
                CurrentRevisionCreatedDate = DateTimeOffset.UnixEpoch, Xmin = 1,
            });
        }

        public Task<IReadOnlyList<StorageCredentialMetadata>> ListAsync(Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoragePage<StorageCredentialMetadata>> ListPageAsync(Guid teamId, string? cursor, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageCredentialMetadata?> GetAsync(Guid teamId, Guid credentialId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageCredentialMetadata?> AppendRevisionAsync(Guid teamId, Guid actorId, AppendStorageCredentialRevisionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageCredentialMetadata?> RevokeAsync(Guid teamId, Guid actorId, RevokeStorageCredentialCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingProfileService(List<string> log) : IStorageProfileService
    {
        public static readonly Guid ProfileId = Guid.NewGuid();

        public CreateStorageProfileCommand? LastCreate { get; private set; }

        public Task<StorageProfileDetail> CreateAsync(Guid teamId, Guid actorId, CreateStorageProfileCommand command, CancellationToken cancellationToken)
        {
            log.Add("profile.create");
            LastCreate = command;
            return Task.FromResult(Detail(command.StableName, StorageProfileStateValue.Draft));
        }

        public Task<StorageProfileDetail?> SetStateAsync(Guid teamId, Guid actorId, SetStorageProfileStateCommand command, CancellationToken cancellationToken)
        {
            log.Add("profile.activate");
            return Task.FromResult<StorageProfileDetail?>(Detail(LastCreate!.StableName, command.State));
        }

        private static StorageProfileDetail Detail(string stableName, StorageProfileStateValue state) => new()
        {
            Id = ProfileId, StableName = stableName, State = state, CurrentRevision = 1, Xmin = 3,
            CreatedDate = DateTimeOffset.UnixEpoch, CreatedBy = ActorId,
            LastModifiedDate = DateTimeOffset.UnixEpoch, LastModifiedBy = ActorId, Revisions = [],
        };

        public Task<IReadOnlyList<StorageProfileSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoragePage<StorageProfileSummary>> ListPageAsync(Guid teamId, string? cursor, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageProfileDetail?> GetAsync(Guid teamId, Guid profileId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageProfileDetail?> AppendRevisionAsync(Guid teamId, Guid actorId, AppendStorageProfileRevisionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingRouteService(List<string> log, StorageRouteSummary? existing) : IStorageRouteService
    {
        public Task<StorageRouteSummary?> GetByDataClassAsync(Guid teamId, string dataClassTypeKey, CancellationToken cancellationToken)
        {
            log.Add("route.lookup");
            return Task.FromResult(existing);
        }

        public Task<StorageRouteDetail> CreateAsync(Guid teamId, Guid actorId, CreateStorageRouteCommand command, CancellationToken cancellationToken)
        {
            log.Add("route.create");
            return Task.FromResult(Detail(command.DataClassTypeKey, StorageRouteStateValue.Draft, command.StorageProfileId));
        }

        public Task<StorageRouteDetail?> AppendRevisionAsync(Guid teamId, Guid actorId, AppendStorageRouteRevisionCommand command, CancellationToken cancellationToken)
        {
            log.Add("route.append");
            return Task.FromResult<StorageRouteDetail?>(Detail(existing!.DataClassTypeKey, existing.State, command.StorageProfileId));
        }

        public Task<StorageRouteDetail?> SetStateAsync(Guid teamId, Guid actorId, SetStorageRouteStateCommand command, CancellationToken cancellationToken)
        {
            log.Add("route.activate");
            return Task.FromResult<StorageRouteDetail?>(Detail(DataClass, command.State, Guid.NewGuid()));
        }

        private static StorageRouteDetail Detail(string dataClassTypeKey, StorageRouteStateValue state, Guid profileId) => new()
        {
            Id = Guid.NewGuid(), DataClassTypeKey = dataClassTypeKey, State = state, CurrentRevision = 1, Xmin = 5,
            CreatedDate = DateTimeOffset.UnixEpoch, CreatedBy = ActorId,
            LastModifiedDate = DateTimeOffset.UnixEpoch, LastModifiedBy = ActorId,
            CurrentTarget = new StorageRouteRevisionDetail
            {
                Id = Guid.NewGuid(), Revision = 1, StorageProfileId = profileId, StorageProfileStableName = "target",
                ProfileRevisionMode = StorageProfileRevisionModeValue.CurrentAtWrite, CreatedDate = DateTimeOffset.UnixEpoch, CreatedBy = ActorId,
            },
            RevisionPage = new StoragePage<StorageRouteRevisionDetail> { Items = [] },
        };

        public Task<StoragePage<StorageRouteSummary>> ListPageAsync(Guid teamId, string? cursor, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageRouteDetail?> GetAsync(Guid teamId, Guid routeId, string? revisionCursor, int revisionLimit, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
