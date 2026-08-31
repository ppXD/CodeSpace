using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Backends;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local.Legacy;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Dtos.Storage;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.Local.Legacy;

/// <summary>
/// The pre-CAS local layout, and the refusals that keep the tier from ever removing a byte.
///
/// <para>High fidelity (Rule 12): the layout is pinned against the REAL
/// <see cref="LocalFileArtifactBlobBackend"/> writing REAL files into a temp root, because a layout is the one thing
/// here that inspection cannot verify — it either reproduces a url the deployment wrote years ago, or it does not.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class LegacyLocalPlacementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-legacy-layout-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", "00/00/0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "ff/ff/ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("abcd000000000000000000000000000000000000000000000000000000000000", "ab/cd/abcd000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("0a0b0c0000000000000000000000000000000000000000000000000000000000", "0a/0b/0a0b0c0000000000000000000000000000000000000000000000000000000000")]
    // Upper case survives verbatim: the backend derives its directories from the digest as given, so folding the case
    // here would name a path that does not exist on a case-sensitive filesystem.
    [InlineData("ABCD000000000000000000000000000000000000000000000000000000000000", "AB/CD/ABCD000000000000000000000000000000000000000000000000000000000000")]
    public void The_layout_shards_a_digest_two_levels_deep_without_touching_it(string sha256, string expectedKey)
    {
        LegacyLocalObjectKeys.For(sha256).ShouldBe(expectedKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000")]   // 63 — one short of a digest
    [InlineData("00000000000000000000000000000000000000000000000000000000000000000")] // 65 — one long
    [InlineData("00000000000000000000000000000000000000000000000000000000000000g0")]  // not hex
    [InlineData("../../etc/passwd000000000000000000000000000000000000000000000000")]
    public void A_value_the_backend_could_never_have_placed_names_nothing(string? sha256)
    {
        LegacyLocalObjectKeys.For(sha256).ShouldBeNull("a key invented for a digest the backend never wrote would report a healthy destination as empty");
    }

    [Fact]
    public async Task The_layout_reproduces_the_url_the_production_backend_actually_wrote()
    {
        // The drift detector. Nothing else in this suite can tell a plausible fan-out from the real one, and a
        // layout that has quietly diverged resolves zero rows — which is exactly the signal phase two is gated on.
        var backend = new LocalFileArtifactBlobBackend(_root);
        var payload = Encoding.UTF8.GetBytes("bytes this deployment wrote before the CAS plane existed");
        var sha = ArtifactStore.ComputeSha256Hex(payload);

        var storageUrl = await backend.WriteAsync(sha, payload, CancellationToken.None);

        var key = LegacyLocalObjectKeys.For(sha);
        key.ShouldNotBeNull();
        LegacyLocalObjectKeys.NamesTheSameFile(DurableRoots.ArtifactStore(_root), key, storageUrl)
            .ShouldBeTrue($"the legacy layout must resolve to the very file the backend wrote at {storageUrl}");
    }

    [Fact]
    public async Task A_layout_pointed_at_another_root_names_none_of_that_root_s_files()
    {
        // The key-mapping bug, staged. It has to read as "unresolved", never as "the destination lost the bytes":
        // one of those is a configuration mistake and the other is data loss.
        var backend = new LocalFileArtifactBlobBackend(_root);
        var payload = Encoding.UTF8.GetBytes("bytes under one root, asked about under another");
        var sha = ArtifactStore.ComputeSha256Hex(payload);

        var storageUrl = await backend.WriteAsync(sha, payload, CancellationToken.None);

        LegacyLocalObjectKeys.NamesTheSameFile(_root + "-elsewhere", LegacyLocalObjectKeys.For(sha)!, storageUrl).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("https://example.invalid/aa/bb/cc")]
    public void A_locator_this_tier_cannot_vouch_for_names_nothing(string? recordedLocator)
    {
        var sha = new string('a', 64);

        LegacyLocalObjectKeys.NamesTheSameFile(_root, LegacyLocalObjectKeys.For(sha)!, recordedLocator).ShouldBeFalse();
    }

    [Fact]
    public async Task The_module_resolves_a_row_only_when_its_own_configured_root_names_it()
    {
        var backend = new LocalFileArtifactBlobBackend(_root);
        var payload = Encoding.UTF8.GetBytes("a module-level resolution");
        var sha = ArtifactStore.ComputeSha256Hex(payload);
        var storageUrl = await backend.WriteAsync(sha, payload, CancellationToken.None);
        var module = new LocalLegacyStorageProviderModule();

        module.ResolveLegacyObjectKey(Configuration(DurableRoots.ArtifactStore(_root)), sha, storageUrl).ShouldBe(LegacyLocalObjectKeys.For(sha));
        module.ResolveLegacyObjectKey(Configuration(_root + "-elsewhere"), sha, storageUrl).ShouldBeNull();
    }

    [Fact]
    public void The_tier_declares_no_delete_and_its_driver_refuses_one_outright()
    {
        // Two independent refusals, because one of them is only a declaration. The capability is what every delete
        // path in the plane checks before it asks the destination anything; the driver's refusal is what answers a
        // caller that got past it some other way.
        var module = new LocalLegacyStorageProviderModule();

        module.Capabilities.HasFlag(StorageProviderCapabilities.Delete).ShouldBeFalse("these keys carry no team segment, so one unlink takes bytes from every team that stored them");
        module.ShouldBeAssignableTo<IStorageProviderTenantSharedObjectKeys>();
        module.ShouldNotBeAssignableTo<IStorageProviderTeamNamespace>("one root with no team segment is the shared namespace a deployment default may not have");
    }

    [Fact]
    public void The_tier_declares_that_it_takes_no_bytes_at_all_rather_than_none_today()
    {
        // Route binding refuses on this marker alone, before a driver is ever opened. The driver's own Put refusal
        // cannot stand in for it: that one arrives once a write is already being attempted, at runtime, long after
        // the operator who chose this destination stopped watching.
        var module = new LocalLegacyStorageProviderModule();

        module.ShouldBeAssignableTo<IStorageProviderAcceptsNoNewBytes>("nothing places bytes in this layout any more, and no probe of a destination can report a permanent fact about a provider type");
    }

    [Fact]
    public async Task The_driver_refuses_to_remove_bytes_and_says_the_keys_are_shared()
    {
        await using var driver = await new LocalLegacyArtifactStorageDriverFactory()
            .CreateAsync(new ArtifactStorageDriverCreateRequest(Snapshot(_root)), CancellationToken.None);

        var deletion = await driver.DeleteAsync(new ArtifactStorageDeleteRequest("aa/bb/" + new string('a', 64)), CancellationToken.None);

        deletion.IsSuccess.ShouldBeFalse();
        deletion.Error!.Code.ShouldBe(ArtifactStorageErrorCode.Unsupported);
        deletion.Error.Message.ShouldContain("cross-team");
    }

    [Fact]
    public void A_module_that_shares_its_keys_may_not_also_declare_delete()
    {
        // Startup, not review, is where this has to fail: a later edit adding Delete beside the marker would make
        // every delete path in the plane admissible again for a tier whose bytes belong to teams it never heard of.
        var error = Should.Throw<InvalidOperationException>(() => new StorageProviderModuleCatalog([new SharedKeyDeletingModule()]));

        error.Message.ShouldContain("shared-keys/v1");
        error.Message.ShouldContain(nameof(IStorageProviderTenantSharedObjectKeys));
    }

    [Fact]
    public void The_legacy_module_is_admitted_by_the_production_catalog_alongside_the_rwx_tier()
    {
        var catalog = new StorageProviderModuleCatalog([new LocalLegacyStorageProviderModule(), new LocalRwxStorageProviderModule()]);

        catalog.Require(LocalLegacyArtifactStorageDriverFactory.TypeKey).FactoryType.ShouldBe(typeof(LocalLegacyArtifactStorageDriverFactory));
        catalog.Require(LocalRwxArtifactStorageDriverFactory.TypeKey).FactoryType.ShouldBe(typeof(LocalRwxArtifactStorageDriverFactory));
    }

    [Theory]
    [InlineData(LegacyPlacementSurveyRefusalValue.None, 100, 99, true)]
    [InlineData(LegacyPlacementSurveyRefusalValue.None, 0, 0, false)]   // resolves nothing: a key-mapping bug far more often than a lost destination
    [InlineData(LegacyPlacementSurveyRefusalValue.None, 100, 0, false)] // resolves everything, confirms nothing: an unmounted or emptied root
    [InlineData(LegacyPlacementSurveyRefusalValue.ProviderHasNoLegacyLayout, 0, 0, false)]
    [InlineData(LegacyPlacementSurveyRefusalValue.DestinationUnavailable, 0, 0, false)]
    [InlineData(LegacyPlacementSurveyRefusalValue.ProfileMissing, 0, 0, false)]
    public void Adoption_is_admitted_only_when_the_layout_named_rows_and_the_destination_answered(LegacyPlacementSurveyRefusalValue refusal, int resolved, int confirmed, bool expected)
    {
        LegacyAdoptionRules.AdmitsAdoption(refusal, resolved, confirmed).ShouldBe(expected);
    }

    [Fact]
    public void The_pass_cap_is_pinned_so_a_change_to_it_is_a_decision()
    {
        LegacyPlacementSurveyLimits.MaxRowsPerPass.ShouldBe(1000);
    }

    private static JsonElement Configuration(string rootPath)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath }));
        return document.RootElement.Clone();
    }

    private static StorageProfileSnapshot Snapshot(string rootPath) => new()
    {
        ProfileId = Guid.NewGuid(),
        ProfileRevision = 1,
        ProviderTypeKey = LocalLegacyArtifactStorageDriverFactory.TypeKey,
        Configuration = Configuration(rootPath),
    };

    /// <summary>A provider that claims both at once — the pairing the catalog exists to refuse.</summary>
    private sealed class SharedKeyDeletingModule : IStorageProviderModule, IStorageProviderTenantSharedObjectKeys
    {
        public string TypeKey => "shared-keys/v1";
        public string DisplayName => "Shared keys that also delete";
        public JsonElement ConfigSchema => EmptyObject;
        public JsonElement SecretSchema => EmptyObject;
        public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.Delete;
        public Type FactoryType => typeof(LocalLegacyArtifactStorageDriverFactory);

        private static JsonElement EmptyObject
        {
            get
            {
                using var document = JsonDocument.Parse("""{"type":"object"}""");
                return document.RootElement.Clone();
            }
        }
    }
}
