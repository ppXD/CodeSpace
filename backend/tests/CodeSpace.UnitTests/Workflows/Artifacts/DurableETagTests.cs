using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts;

/// <summary>
/// Which providers' ETags may be believed months after they were recorded.
///
/// <para>The rule exists because an ETag is provider-defined: for an object store it identifies the bytes, and for a
/// filesystem it can be a modification time wearing the same name. Persisting the second kind reports intact data as
/// corrupt — and since the recorded value also gates deletion, leaves the object unreadable and unpurgeable at once.</para>
/// </summary>
public sealed class DurableETagTests
{
    private const StorageProviderCapabilities Filesystem = StorageProviderCapabilities.StreamingRead | StorageProviderCapabilities.Delete;
    private const StorageProviderCapabilities ObjectStore = Filesystem | StorageProviderCapabilities.StableETag;

    [Fact]
    public void A_provider_that_does_not_promise_a_content_derived_etag_has_its_recorded_one_ignored()
    {
        ArtifactCasRuntimeCoordinator.DurableETag("W/\"local-12-8dc4f1a\"", Filesystem)
            .ShouldBeNull("a value that changes when the bytes do not is not an identity, whatever it was recorded as");
    }

    [Fact]
    public void A_provider_whose_etag_identifies_the_bytes_keeps_its_protection()
    {
        // Dropping the comparison everywhere would be the easy fix and the wrong one: on an object store the ETag is
        // the one cheap way to notice that the key now holds something nobody here wrote.
        ArtifactCasRuntimeCoordinator.DurableETag("\"9a0364b9e99bb480dd25e1f0284c8555\"", ObjectStore)
            .ShouldBe("\"9a0364b9e99bb480dd25e1f0284c8555\"");
    }

    [Fact]
    public void The_capability_is_opt_in_so_a_new_provider_cannot_acquire_the_promise_by_accident()
    {
        ArtifactCasRuntimeCoordinator.DurableETag("anything", StorageProviderCapabilities.None).ShouldBeNull();
    }

    [Fact]
    public void The_local_filesystem_driver_does_not_claim_one()
    {
        // Pinned rather than left implicit: its ETag embeds the file's modification time, so anything that rewrites
        // identical bytes — a restore, an rsync migration, a copy to a larger volume — produces a new value. That is
        // a usable same-session conditional token and a false identity once persisted. A future change that makes the
        // local ETag content-derived may add the flag; nothing else may.
        new LocalRwxStorageProviderModule().Capabilities.HasFlag(StorageProviderCapabilities.StableETag)
            .ShouldBeFalse("a modification time is not a content identity");
    }
}
