using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Profiles;

/// <summary>
/// Revisions a build that stored the PHYSICAL namespace identity already wrote, put back to the value the previous
/// build stores — so that build's reaper, after a rollback or beside a pod not yet replaced, compares like with like.
///
/// <para>The backfill sweeps the whole table, and other suites seed revisions with fingerprints no admission ever
/// produced, so every test here waits on the rows it owns rather than on a pass's tally.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageNamespaceFingerprintBackfillFlowTests
{
    /// <summary>Enough passes to drain every leftover revision of a full suite run ahead of this test's own rows; a pass that never reaches them fails instead of hanging.</summary>
    private const int MaxPasses = 500;

    private readonly PostgresFixture _fixture;

    public StorageNamespaceFingerprintBackfillFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_revision_stamped_with_the_physical_identity_is_restored_to_what_the_previous_build_stores()
    {
        var typed = RootConfig($"/srv/codespace-backfill-{Guid.NewGuid():N}/");
        var physical = await SeedRevisionAsync(typed, PhysicalFingerprint(typed));
        var previousBuild = await SeedRevisionAsync(typed, LegacyFingerprint(typed));

        PhysicalFingerprint(typed).ShouldNotBe(LegacyFingerprint(typed), "the precondition: a trailing separator is where the two identities part");

        await BackfillUntilAsync(physical, LegacyFingerprint(typed));

        (await StoredAsync(physical)).ShouldBe(new Stored(typed, LegacyFingerprint(typed)), "only the fingerprint moves; the configuration is the revision's record and stays as written");
        (await StoredAsync(physical)).Fingerprint.ShouldBe((await StoredAsync(previousBuild)).Fingerprint, "one root typed alike is one namespace to the previous build's reaper again");
        (await StoredAsync(previousBuild)).ShouldBe(new Stored(typed, LegacyFingerprint(typed)), "a revision the previous build wrote is already right and is never rewritten");

        await BackfillPassAsync();

        (await StoredAsync(physical)).Fingerprint.ShouldBe(LegacyFingerprint(typed), "a restored revision is no longer a candidate: the pass is idempotent");
    }

    [Fact]
    public async Task The_revision_table_is_immutable_again_once_a_pass_has_restored_rows()
    {
        var typed = RootConfig($"/srv/codespace-backfill-{Guid.NewGuid():N}/.");
        var revisionId = await SeedRevisionAsync(typed, PhysicalFingerprint(typed));

        await BackfillUntilAsync(revisionId, LegacyFingerprint(typed));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var refused = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE storage_profile_revision SET namespace_fingerprint = {PhysicalFingerprint(typed)} WHERE id = {revisionId}"));

        refused.MessageText.ShouldContain("storage_profile_revision is immutable", Case.Sensitive, "the pass switches the immutability trigger off only inside its own transaction");
    }

    [Fact]
    public async Task Passes_on_several_workers_at_once_restore_a_revision_once_and_all_finish()
    {
        var typed = RootConfig($"/srv/codespace-backfill-{Guid.NewGuid():N}//");
        var revisionId = await SeedRevisionAsync(typed, PhysicalFingerprint(typed));

        for (var pass = 0; pass < MaxPasses && (await StoredAsync(revisionId)).Fingerprint != LegacyFingerprint(typed); pass++)
            await Task.WhenAll(BackfillPassAsync(), BackfillPassAsync());

        (await StoredAsync(revisionId)).Fingerprint.ShouldBe(LegacyFingerprint(typed), $"two concurrent workers never restored revision {revisionId}; query storage_profile_revision for it and check pg_locks for a stuck pass");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private sealed record Stored(string Config, string Fingerprint);

    /// <summary>Runs passes until <paramref name="revisionId"/> holds <paramref name="expected"/>, bounded so a pass that never reaches it fails with the row named.</summary>
    private async Task BackfillUntilAsync(Guid revisionId, string expected)
    {
        for (var pass = 0; pass < MaxPasses && (await StoredAsync(revisionId)).Fingerprint != expected; pass++) await BackfillPassAsync();

        (await StoredAsync(revisionId)).Fingerprint.ShouldBe(expected, $"{MaxPasses} passes never restored revision {revisionId}; run the backfill command by hand and read its log");
    }

    private async Task BackfillPassAsync()
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IStorageNamespaceFingerprintBackfillService>().BackfillAsync(200, CancellationToken.None);
    }

    /// <summary>A revision exactly as admission wrote it, stamped with <paramref name="fingerprint"/>: inserting is what the immutability trigger allows.</summary>
    private async Task<Guid> SeedRevisionAsync(string config, string fingerprint)
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"fingerprint-{actorId:N}@test.local", Name = $"fingerprint-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"fingerprint-{teamId:N}", Name = "Fingerprint Backfill Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"fingerprint-{profileId:N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = revisionId, TeamId = teamId, StorageProfileId = profileId, Revision = 1, ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
            NonSecretConfigJson = config, NamespaceFingerprint = fingerprint, CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();

        return revisionId;
    }

    private async Task<Stored> StoredAsync(Guid revisionId)
    {
        using var scope = _fixture.BeginScope();

        var row = await scope.Resolve<CodeSpaceDbContext>().StorageProfileRevision.AsNoTracking()
            .Where(revision => revision.Id == revisionId).Select(revision => new { revision.NonSecretConfigJson, revision.NamespaceFingerprint }).SingleAsync();

        using var config = JsonDocument.Parse(row.NonSecretConfigJson);
        return new Stored(StorageProfileRules.CanonicalJson(config.RootElement), row.NamespaceFingerprint);
    }

    /// <summary>The canonical text admission persists for a local root.</summary>
    private static string RootConfig(string rootPath)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath }));
        return StorageProfileRules.CanonicalJson(document.RootElement);
    }

    /// <summary>What a build that stored the physical identity wrote for this configuration.</summary>
    private static string PhysicalFingerprint(string config)
    {
        using var document = JsonDocument.Parse(config);
        return StorageProfileRules.NamespaceFingerprint(new LocalRwxStorageProviderModule(), document.RootElement);
    }

    private static string LegacyFingerprint(string config)
    {
        using var document = JsonDocument.Parse(config);
        return StorageProfileRules.LegacyNamespaceFingerprint(LocalRwxArtifactStorageDriverFactory.TypeKey, document.RootElement);
    }
}
