using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Runtime;

/// <summary>
/// The probe against a real destination, and what it leaves behind. Every case here exercises the PRODUCTION probe
/// service through the registered decorator, so the recording is proved on the path an operator actually takes.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RecordingStorageProfileProbeDecoratorTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public RecordingStorageProfileProbeDecoratorTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_writable_destination_is_recorded_as_verified_by_a_real_write()
    {
        var world = await SeedAsync(NewRoot());

        var result = await ProbeAsync(world, verifyWrite: true);

        result.Status.ShouldBe(StorageProfileProbeStatusValue.Available);

        var health = await HealthAsync(world);
        health.ShouldNotBeNull("without a recorded observation the probe is an HTTP response and nothing else — the next page load looks identical to a healthy one");
        health.Status.ShouldBe(StorageProfileProbeStatusValue.Available);
        health.WriteVerified.ShouldBeTrue();
        health.ProfileRevision.ShouldBe(1, "health that cannot say WHICH revision it describes cannot be compared against the profile's current one");
        health.FailureStage.ShouldBeNull();
        health.FailureCode.ShouldBeNull();
    }

    [Fact]
    public async Task A_destination_that_cannot_be_written_is_recorded_with_the_reason()
    {
        // The whole point of keeping the answer: an operator must be able to see WHICH end to fix without
        // re-running the probe, and long after the tab that ran it is closed.
        var world = await SeedAsync("/dev/null/codespace-cannot-write-here");

        var result = await ProbeAsync(world, verifyWrite: true);

        result.Status.ShouldNotBe(StorageProfileProbeStatusValue.Available);

        var health = (await HealthAsync(world)).ShouldNotBeNull();
        health.Status.ShouldBe(result.Status);
        health.WriteVerified.ShouldBeFalse();
        health.FailureCode.ShouldNotBeNull("a failing status with no code tells an operator the destination is broken and nothing about which end to fix");
        health.FailureStage.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_read_only_probe_that_passes_does_not_claim_writes_were_verified()
    {
        // Listing proves the credential can reach the destination. It does not prove a run's bytes will land, and a
        // screen that rendered the two the same would report a read-only bucket as ready.
        var world = await SeedAsync(NewRoot());

        await ProbeAsync(world, verifyWrite: false);

        (await HealthAsync(world)).ShouldNotBeNull().WriteVerified.ShouldBeFalse();
    }

    [Fact]
    public async Task Recording_a_probe_never_advances_the_profile_the_operator_is_editing()
    {
        // The reason health is its own row. storage_profile carries the concurrency token every operator edit checks,
        // so a background probe writing into it would fail an operator's next save with a conflict nobody caused.
        var world = await SeedAsync(NewRoot());
        var before = await ProfileTokenAsync(world);

        await ProbeAsync(world, verifyWrite: true);

        (await ProfileTokenAsync(world)).ShouldBe(before, "a probe must be invisible to optimistic concurrency");

        using var scope = _fixture.BeginScope();
        var edited = await scope.Resolve<IStorageProfileService>().SetStateAsync(world.TeamId, world.ActorId, new SetStorageProfileStateCommand
        {
            ProfileId = world.ProfileId, ExpectedXmin = before.Xmin, ExpectedCurrentRevision = before.Revision,
            State = StorageProfileStateValue.Disabled,
        }, CancellationToken.None);

        edited.ShouldNotBeNull("an edit holding the token it read BEFORE the probe must still be accepted");
    }

    [Fact]
    public async Task A_second_probe_overwrites_the_first_rather_than_accumulating()
    {
        // One row per profile: a settings screen asks "does my storage work right now", and a history would need a
        // retention policy nobody has written.
        var world = await SeedAsync(NewRoot());

        await ProbeAsync(world, verifyWrite: true);
        await ProbeAsync(world, verifyWrite: true);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().StorageProfileHealth.AsNoTracking()
            .CountAsync(row => row.StorageProfileId == world.ProfileId)).ShouldBe(1);
    }


    [Fact]
    public async Task A_never_probed_destination_reports_no_health_rather_than_a_neutral_looking_pass()
    {
        // "Nobody has checked" and "checked and working" are different facts, and only one of them is a reason to
        // trust the destination. Smoothing the first into a neutral status is how a screen tells a comforting lie.
        var world = await SeedAsync(NewRoot());

        (await SummaryAsync(world)).Health.ShouldBeNull();
    }

    [Fact]
    public async Task The_profile_list_carries_what_the_probe_saw()
    {
        // The list is what an operator actually opens. Health that only a direct query can reach is not observable.
        var world = await SeedAsync("/dev/null/codespace-cannot-write-here");

        await ProbeAsync(world, verifyWrite: true);

        var health = (await SummaryAsync(world)).Health.ShouldNotBeNull();
        health.Status.ShouldNotBe(StorageProfileProbeStatusValue.Available);
        health.FailureCode.ShouldNotBeNull();
        health.WriteVerified.ShouldBeFalse();
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    private async Task<StorageProfileProbeResult> ProbeAsync(World world, bool verifyWrite)
    {
        using var scope = _fixture.BeginScope();
        var probe = scope.Resolve<IStorageProfileProbeService>();

        probe.ShouldBeOfType<RecordingStorageProfileProbeDecorator>("the recorder must be the registered decorator, or nothing on the operator's path keeps the answer");

        return await probe.ProbeAsync(new StorageProfileProbeRequest(world.TeamId, world.ProfileId, null, verifyWrite), CancellationToken.None);
    }

private async Task<StorageProfileSummary> SummaryAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        return (await scope.Resolve<IStorageProfileService>().ListAsync(world.TeamId, CancellationToken.None))
            .Single(profile => profile.Id == world.ProfileId);
    }

    private async Task<StorageProfileHealth?> HealthAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().StorageProfileHealth.AsNoTracking()
            .SingleOrDefaultAsync(row => row.TeamId == world.TeamId && row.StorageProfileId == world.ProfileId);
    }

    private async Task<ProfileToken> ProfileTokenAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var profile = await scope.Resolve<CodeSpaceDbContext>().StorageProfile.AsNoTracking()
            .SingleAsync(row => row.Id == world.ProfileId);

        return new ProfileToken(profile.Xmin, profile.CurrentRevision);
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codespace-probe-health", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private async Task<World> SeedAsync(string rootPath)
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"probe-{actorId:N}@test.local", Name = "Probe" });
        db.Team.Add(new Team { Id = teamId, Slug = $"probe-{teamId:N}", Name = "Probe", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });

        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"probe-{profileId:N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
            NonSecretConfigJson = JsonSerializer.Serialize(new { rootPath }), CredentialRef = null,
            NamespaceFingerprint = $"sha256:{new string('e', 64)}", CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();

        return new World(teamId, actorId, profileId);
    }

    public void Dispose()
    {
        foreach (var root in _roots.Where(Directory.Exists))
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid ProfileId);
    private sealed record ProfileToken(uint Xmin, int Revision);
}
