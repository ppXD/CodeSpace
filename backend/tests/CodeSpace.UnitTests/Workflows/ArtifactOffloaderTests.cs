using System.Text;
using CodeSpace.Core.Services.Workflows.Artifacts;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The generic field-level offload primitive (<see cref="IArtifactOffloader"/>): the single size-routing policy
/// every producer shares. Driven against a fake <see cref="IArtifactStore"/> (records Puts, serves Gets) so the
/// routing logic is pinned without a DB: small/empty stay inline, large offload + clear inline, and resolve
/// round-trips inline-or-fetched.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArtifactOffloaderTests
{
    private static string Big() => new('+', ArtifactStoreConfig.DefaultInlineThresholdBytes + 1);

    [Fact]
    public async Task Small_text_stays_inline_with_no_put()
    {
        var store = new FakeStore();
        var offloader = new ArtifactOffloader(store);

        var result = await offloader.OffloadIfLargeAsync(Guid.NewGuid(), "small", "text/plain", CancellationToken.None);

        result.Inline.ShouldBe("small");
        result.ArtifactId.ShouldBeNull();
        store.Puts.ShouldBeEmpty("a sub-threshold field is never offloaded");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Null_or_empty_is_a_noop(string? text)
    {
        var store = new FakeStore();
        var result = await new ArtifactOffloader(store).OffloadIfLargeAsync(Guid.NewGuid(), text, "text/plain", CancellationToken.None);

        result.Inline.ShouldBe("");
        result.ArtifactId.ShouldBeNull();
        store.Puts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Large_text_offloads_clears_inline_and_puts_the_bytes()
    {
        var store = new FakeStore();
        var offloader = new ArtifactOffloader(store);
        var teamId = Guid.NewGuid();
        var big = Big();

        var result = await offloader.OffloadIfLargeAsync(teamId, big, "text/x-diff", CancellationToken.None);

        result.Inline.ShouldBe("", "the inline field is cleared once offloaded");
        result.ArtifactId.ShouldNotBeNull();
        store.Puts.ShouldHaveSingleItem();
        store.Puts[0].TeamId.ShouldBe(teamId);
        store.Puts[0].ContentType.ShouldBe("text/x-diff");
        Encoding.UTF8.GetString(store.Puts[0].Bytes).ShouldBe(big, "the exact bytes are stored");
    }

    [Fact]
    public async Task Resolve_returns_inline_when_present_without_touching_the_store()
    {
        var store = new FakeStore();
        var resolved = await new ArtifactOffloader(store).ResolveAsync(Guid.NewGuid(), "inline value", Guid.NewGuid(), CancellationToken.None);

        resolved.ShouldBe("inline value", "an inline field is returned as-is (the ref is ignored when inline is set)");
        store.Gets.ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolve_fetches_from_the_store_when_only_a_ref_is_set()
    {
        var store = new FakeStore();
        var teamId = Guid.NewGuid();
        var big = Big();
        var (_, id) = await new ArtifactOffloader(store).OffloadIfLargeAsync(teamId, big, "text/x-diff", CancellationToken.None);

        var resolved = await new ArtifactOffloader(store).ResolveAsync(teamId, "", id, CancellationToken.None);

        resolved.ShouldBe(big, "an offloaded field is fetched back in full from the store");
    }

    [Fact]
    public async Task Resolve_is_empty_when_neither_inline_nor_a_resolvable_ref()
    {
        var store = new FakeStore();
        var offloader = new ArtifactOffloader(store);

        (await offloader.ResolveAsync(Guid.NewGuid(), null, null, CancellationToken.None)).ShouldBe("");
        (await offloader.ResolveAsync(Guid.NewGuid(), "", Guid.NewGuid(), CancellationToken.None)).ShouldBe("", "a missing / cross-team artifact resolves to empty, not a crash");
    }

    /// <summary>An in-memory <see cref="IArtifactStore"/> — records Puts (returns a stable id per sha) + serves Gets; unknown id → null (the cross-team/missing case).</summary>
    private sealed class FakeStore : IArtifactStore
    {
        public List<(Guid TeamId, byte[] Bytes, string ContentType)> Puts { get; } = new();
        public List<Guid> Gets { get; } = new();
        private readonly Dictionary<Guid, (string Sha, string ContentType, byte[] Bytes)> _byId = new();

        public Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken)
        {
            var arr = bytes.ToArray();
            Puts.Add((teamId, arr, contentType));
            var sha = ArtifactStore.ComputeSha256Hex(arr);
            var id = _byId.FirstOrDefault(kv => kv.Value.Sha == sha).Key;
            if (id == Guid.Empty) { id = Guid.NewGuid(); _byId[id] = (sha, contentType, arr); }
            return Task.FromResult(id);
        }

        public Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken)
        {
            Gets.Add(artifactId);
            if (!_byId.TryGetValue(artifactId, out var v)) return Task.FromResult<ArtifactBytes?>(null);
            return Task.FromResult<ArtifactBytes?>(new ArtifactBytes { Id = artifactId, Sha256 = v.Sha, ContentType = v.ContentType, Bytes = v.Bytes });
        }

        public Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactMetadata?>(null);
    }
}
