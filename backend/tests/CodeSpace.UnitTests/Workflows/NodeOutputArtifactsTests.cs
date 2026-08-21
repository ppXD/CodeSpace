using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pure-logic tests for the selective leaf-value offload of node outputs (<see cref="NodeOutputArtifacts"/>):
/// oversize property values move to the artifact store and become a compact ref; small values and the output
/// STRUCTURE are preserved so <c>{{nodes.X.outputs.foo}}</c> resolution still navigates the keys; offload is
/// idempotent; and resolution is fail-safe (a missing artifact leaves the ref rather than dropping the value).
/// A hermetic in-memory store stands in for the real content-addressed <c>IArtifactStore</c>.
/// </summary>
[Trait("Category", "Unit")]
public class NodeOutputArtifactsTests
{
    private const int Threshold = 1024;

    [Fact]
    public async Task Oversize_value_is_offloaded_to_a_ref_and_small_values_pass_through()
    {
        var store = new InMemoryArtifactStore();
        var teamId = Guid.NewGuid();
        var big = new string('x', Threshold * 4);

        var outputs = Outputs(("body", JsonString(big)), ("status", JsonNumber(200)));

        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, outputs, Threshold, CancellationToken.None);

        NodeOutputArtifacts.IsRef(offloaded["body"]).ShouldBeTrue("the 4 KiB value exceeds the threshold — offloaded to a ref");
        offloaded["body"].GetRawText().Contains(big).ShouldBeFalse("the blob is no longer inline");
        offloaded.ShouldContainKey("status");
        NodeOutputArtifacts.IsRef(offloaded["status"]).ShouldBeFalse("a small value is left inline");
        store.Count.ShouldBe(1, "only the oversize value was stored");
    }

    [Fact]
    public async Task Offload_then_resolve_round_trips_to_the_original_outputs()
    {
        var store = new InMemoryArtifactStore();
        var teamId = Guid.NewGuid();
        var big = new string('y', Threshold * 8);

        var original = Outputs(("body", JsonString(big)), ("meta", JsonRaw("""{ "n": 1, "ok": true }""")));

        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, original, Threshold, CancellationToken.None);
        var resolved = await NodeOutputArtifacts.ResolveAsync(store, teamId, offloaded, CancellationToken.None);

        resolved["body"].GetString().ShouldBe(big, "the offloaded value is re-inflated verbatim");
        resolved["meta"].GetRawText().ShouldBe(original["meta"].GetRawText());
    }

    [Fact]
    public async Task Offload_is_idempotent_an_existing_ref_is_passed_through()
    {
        var store = new InMemoryArtifactStore();
        var teamId = Guid.NewGuid();
        var big = new string('z', Threshold * 2);

        var once = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, Outputs(("body", JsonString(big))), Threshold, CancellationToken.None);
        var twice = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, once, Threshold, CancellationToken.None);

        twice["body"].GetRawText().ShouldBe(once["body"].GetRawText(), "re-offloading a ref is a no-op — never double-wrapped");
        store.Count.ShouldBe(1, "no second artifact written for the already-offloaded value");
    }

    [Fact]
    public async Task Resolve_is_fail_safe_a_missing_artifact_leaves_the_ref_intact()
    {
        var store = new InMemoryArtifactStore();
        var teamId = Guid.NewGuid();
        var big = new string('q', Threshold * 2);

        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, Outputs(("body", JsonString(big))), Threshold, CancellationToken.None);

        // A different team can't see the artifact (cross-team reads return null) — the ref must survive, not vanish.
        var resolved = await NodeOutputArtifacts.ResolveAsync(store, Guid.NewGuid(), offloaded, CancellationToken.None);

        NodeOutputArtifacts.IsRef(resolved["body"]).ShouldBeTrue("a missing / cross-team artifact leaves the ref verbatim — never silently drops the value");
    }

    [Fact]
    public async Task Resolve_required_rejects_a_missing_artifact_instead_of_returning_a_bare_ref()
    {
        var store = new InMemoryArtifactStore();
        var teamId = Guid.NewGuid();
        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, Outputs(("body", JsonString(new string('q', Threshold * 2)))), Threshold, CancellationToken.None);
        var artifactId = offloaded["body"].GetProperty(NodeOutputArtifacts.RefKey).GetProperty("id").GetGuid();

        var exception = await Should.ThrowAsync<ArtifactContentUnavailableException>(() =>
            NodeOutputArtifacts.ResolveRequiredAsync(store, Guid.NewGuid(), offloaded, CancellationToken.None));

        exception.ArtifactId.ShouldBe(artifactId);
        exception.Kind.ShouldBe(ArtifactContentUnavailableKind.MetadataMissing);
    }

    [Fact]
    public async Task Resolve_required_keeps_healthy_inline_and_offloaded_values_byte_identical()
    {
        var store = new InMemoryArtifactStore();
        var teamId = Guid.NewGuid();
        var original = Outputs(("body", JsonString(new string('v', Threshold * 2))), ("meta", JsonRaw("""{ "z": 2, "a": [1, true] }""")));
        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, original, Threshold, CancellationToken.None);

        var resolved = await NodeOutputArtifacts.ResolveRequiredAsync(store, teamId, offloaded, CancellationToken.None);

        resolved.Keys.ShouldBe(original.Keys);
        foreach (var key in original.Keys) resolved[key].GetRawText().ShouldBe(original[key].GetRawText());
    }

    [Fact]
    public async Task Resolve_required_rejects_corrupt_json_as_an_integrity_failure()
    {
        var store = new InMemoryArtifactStore();
        var teamId = Guid.NewGuid();
        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, Outputs(("body", JsonString(new string('q', Threshold * 2)))), Threshold, CancellationToken.None);
        var artifactId = offloaded["body"].GetProperty(NodeOutputArtifacts.RefKey).GetProperty("id").GetGuid();
        store.ReplaceBytes(teamId, artifactId, "not-json"u8.ToArray());

        var exception = await Should.ThrowAsync<ArtifactContentUnavailableException>(() =>
            NodeOutputArtifacts.ResolveRequiredAsync(store, teamId, offloaded, CancellationToken.None));

        exception.ArtifactId.ShouldBe(artifactId);
        exception.Kind.ShouldBe(ArtifactContentUnavailableKind.IntegrityFailure);
    }

    [Fact]
    public async Task Non_positive_threshold_disables_offload()
    {
        var store = new InMemoryArtifactStore();
        var big = new string('x', 100_000);

        var outputs = await NodeOutputArtifacts.OffloadLargeAsync(store, Guid.NewGuid(), Outputs(("body", JsonString(big))), 0, CancellationToken.None);

        NodeOutputArtifacts.IsRef(outputs["body"]).ShouldBeFalse();
        store.Count.ShouldBe(0);
    }

    private static Dictionary<string, JsonElement> Outputs(params (string Key, JsonElement Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    private static JsonElement JsonString(string s) => JsonSerializer.SerializeToElement(s);
    private static JsonElement JsonNumber(int n) => JsonSerializer.SerializeToElement(n);
    private static JsonElement JsonRaw(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    /// <summary>Hermetic content-addressed store: dedups by SHA-256, scopes by team (cross-team reads return null).</summary>
    private sealed class InMemoryArtifactStore : IArtifactStore
    {
        private readonly ConcurrentDictionary<(Guid Team, string Sha), (Guid Id, byte[] Bytes, string ContentType)> _byContent = new();
        private readonly ConcurrentDictionary<(Guid Team, Guid Id), (string Sha, byte[] Bytes, string ContentType)> _byId = new();

        public int Count => _byId.Count;

        public void ReplaceBytes(Guid teamId, Guid artifactId, byte[] bytes)
        {
            var current = _byId[(teamId, artifactId)];
            _byId[(teamId, artifactId)] = (current.Sha, bytes, current.ContentType);
        }

        public Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken)
        {
            var sha = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();

            if (_byContent.TryGetValue((teamId, sha), out var existing)) return Task.FromResult(existing.Id);

            var id = Guid.NewGuid();
            var copy = bytes.ToArray();
            _byContent[(teamId, sha)] = (id, copy, contentType);
            _byId[(teamId, id)] = (sha, copy, contentType);
            return Task.FromResult(id);
        }

        public Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) =>
            Task.FromResult(_byId.TryGetValue((teamId, artifactId), out var v)
                ? new ArtifactBytes { Id = artifactId, Sha256 = v.Sha, ContentType = v.ContentType, Bytes = v.Bytes }
                : null);

        public Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) =>
            Task.FromResult(_byId.TryGetValue((teamId, artifactId), out var v)
                ? new ArtifactMetadata { Id = artifactId, Sha256 = v.Sha, ContentType = v.ContentType, SizeBytes = v.Bytes.Length, CreatedAt = DateTimeOffset.UnixEpoch }
                : null);
    }
}
