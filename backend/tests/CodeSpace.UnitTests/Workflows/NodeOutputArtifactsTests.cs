using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        var resolved = await NodeOutputArtifacts.ResolveAsync(store, NullLogger.Instance, teamId, offloaded, CancellationToken.None);

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
        var resolved = await NodeOutputArtifacts.ResolveAsync(store, NullLogger.Instance, Guid.NewGuid(), offloaded, CancellationToken.None);

        NodeOutputArtifacts.IsRef(resolved["body"]).ShouldBeTrue("a missing / cross-team artifact leaves the ref verbatim — never silently drops the value");
        ReasonOf(resolved["body"]).ShouldBe(nameof(ArtifactContentUnavailableKind.MetadataMissing), "the surviving pointer says WHY, so a reader is told rather than shown a bare pointer");
    }

    [Theory]
    [InlineData(ArtifactContentUnavailableKind.PhysicalObjectMissing)]
    [InlineData(ArtifactContentUnavailableKind.AccessDenied)]
    [InlineData(ArtifactContentUnavailableKind.IntegrityFailure)]
    [InlineData(ArtifactContentUnavailableKind.BackendUnavailable)]
    public async Task Resolve_required_fails_closed_on_every_storage_lane(ArtifactContentUnavailableKind lane)
    {
        var store = new InMemoryArtifactStore();
        var teamId = Guid.NewGuid();
        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, Outputs(("body", JsonString(new string('r', Threshold * 2)))), Threshold, CancellationToken.None);
        var artifactId = ArtifactIdOf(offloaded["body"]);
        store.FailReadOf(artifactId, new ArtifactContentUnavailableException(artifactId, lane));

        var exception = await Should.ThrowAsync<ArtifactContentUnavailableException>(() =>
            NodeOutputArtifacts.ResolveRequiredAsync(store, teamId, offloaded, CancellationToken.None));

        exception.Kind.ShouldBe(lane, "execution resolution never hands a pointer to a model, a map branch or loop state — whatever the lane");
    }

    [Theory]
    [InlineData(ArtifactContentUnavailableKind.PhysicalObjectMissing)]
    [InlineData(ArtifactContentUnavailableKind.AccessDenied)]
    [InlineData(ArtifactContentUnavailableKind.IntegrityFailure)]
    [InlineData(ArtifactContentUnavailableKind.BackendUnavailable)]
    public async Task Resolve_sheds_every_storage_lane_onto_the_ref_it_could_not_read(ArtifactContentUnavailableKind lane)
    {
        // The neighbour is a SECOND OFFLOADED value, not a small inline one: an inline neighbour is never fetched, so
        // it survives a per-NODE shed identically and proves nothing. Only a neighbour that must itself come back out
        // of the store can tell the two shed granularities apart.
        var store = new InMemoryArtifactStore();
        var teamId = Guid.NewGuid();
        var neighbour = new string('n', Threshold * 2);
        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, Outputs(("body", JsonString(new string('s', Threshold * 2))), ("trace", JsonString(neighbour))), Threshold, CancellationToken.None);
        NodeOutputArtifacts.IsRef(offloaded["trace"]).ShouldBeTrue("precondition: the neighbour is a pointer too, so reading it back costs a real store read");
        store.FailReadOf(ArtifactIdOf(offloaded["body"]), new ArtifactContentUnavailableException(ArtifactIdOf(offloaded["body"]), lane));

        var resolved = await NodeOutputArtifacts.ResolveAsync(store, NullLogger.Instance, teamId, offloaded, CancellationToken.None);

        NodeOutputArtifacts.IsRef(resolved["body"]).ShouldBeTrue("the display read sheds — the structure survives an unreadable value");
        ReasonOf(resolved["body"]).ShouldBe(lane.ToString());
        resolved["trace"].GetString().ShouldBe(neighbour, "one unreadable property costs the reader that property, never its offloaded neighbours");
    }

    [Fact]
    public async Task A_bug_shaped_fault_from_a_whole_object_read_is_never_dressed_as_a_storage_failure()
    {
        // ArgumentOutOfRangeException is a storage fact for the BOUNDED read alone, where it means "that window".
        // Nothing hands an offset to a whole-object read, so here it can only be a code bug — and a bug wearing
        // ArtifactContentUnavailable is a bug an operator is sent to restore their storage backend for.
        var store = new InMemoryArtifactStore();
        var teamId = Guid.NewGuid();
        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, Outputs(("body", JsonString(new string('u', Threshold * 2)))), Threshold, CancellationToken.None);
        store.FailReadOf(ArtifactIdOf(offloaded["body"]), new ArgumentOutOfRangeException("offset"));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => NodeOutputArtifacts.ResolveRequiredAsync(store, teamId, offloaded, CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_required_classifies_a_raw_provider_fault_it_is_handed()
    {
        // The store types its own reads now, but the ladder that catches an untyped provider fault is still the
        // fail-closed path's own — and it is the SAME table the bounded read applies.
        var store = new InMemoryArtifactStore();
        var teamId = Guid.NewGuid();
        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, Outputs(("body", JsonString(new string('t', Threshold * 2)))), Threshold, CancellationToken.None);
        var artifactId = ArtifactIdOf(offloaded["body"]);
        store.FailReadOf(artifactId, new FileNotFoundException("the object is gone"));

        var exception = await Should.ThrowAsync<ArtifactContentUnavailableException>(() =>
            NodeOutputArtifacts.ResolveRequiredAsync(store, teamId, offloaded, CancellationToken.None));

        exception.ArtifactId.ShouldBe(artifactId);
        exception.Kind.ShouldBe(ArtifactContentUnavailableKind.PhysicalObjectMissing);
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
    public async Task Shedding_a_whole_cell_writes_the_same_reason_marker_one_property_gets()
    {
        // What an isolation boundary ABOVE the per-property walk hands back. The two sheds must be indistinguishable
        // to a reader, or a cell lost whole becomes the bare pointer with no account of it that this slice removes.
        var store = new InMemoryArtifactStore();
        var teamId = Guid.NewGuid();
        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, Outputs(("body", JsonString(new string('w', Threshold * 2))), ("status", JsonNumber(200))), Threshold, CancellationToken.None);
        var shedByProperty = await ShedOnePropertyAsync(store, teamId, offloaded);

        var shedWhole = NodeOutputArtifacts.ShedAll(offloaded, ArtifactContentUnavailableKind.BackendUnavailable);

        shedWhole["body"].GetRawText().ShouldBe(shedByProperty["body"].GetRawText(), "one marker shape, whichever granularity failed");
        ReasonOf(shedWhole["body"]).ShouldBe(nameof(ArtifactContentUnavailableKind.BackendUnavailable));
        shedWhole["status"].GetInt32().ShouldBe(200, "a value that was never a pointer is passed through untouched");
    }

    /// <summary>The per-PROPERTY shed's own output for the same ref, produced through the real display path — the byte-for-byte reference the whole-cell shed has to match.</summary>
    private static async Task<Dictionary<string, JsonElement>> ShedOnePropertyAsync(InMemoryArtifactStore store, Guid teamId, Dictionary<string, JsonElement> offloaded)
    {
        var artifactId = ArtifactIdOf(offloaded["body"]);
        store.FailReadOf(artifactId, new ArtifactContentUnavailableException(artifactId, ArtifactContentUnavailableKind.BackendUnavailable));

        return await NodeOutputArtifacts.ResolveAsync(store, NullLogger.Instance, teamId, offloaded, CancellationToken.None);
    }

    [Fact]
    public async Task A_shed_property_leaves_one_warning_naming_the_artifact_and_the_lane()
    {
        // The shed is silent to the operator by design — the reader gets a run, not an error — so the ONLY account of
        // a destination that has started rotting is the backend log. The per-cell boundary above this walk logs too,
        // but nothing reaches it while this shed holds, so a log only there is a log that never fires.
        var store = new InMemoryArtifactStore();
        var logger = new CapturingLogger();
        var teamId = Guid.NewGuid();
        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(store, teamId, Outputs(("body", JsonString(new string('l', Threshold * 2))), ("trace", JsonString(new string('m', Threshold * 2)))), Threshold, CancellationToken.None);
        var artifactId = ArtifactIdOf(offloaded["body"]);
        var cause = new ArtifactContentUnavailableException(artifactId, ArtifactContentUnavailableKind.BackendUnavailable);
        store.FailReadOf(artifactId, cause);

        await NodeOutputArtifacts.ResolveAsync(store, logger, teamId, offloaded, CancellationToken.None);

        var entry = logger.Entries.ShouldHaveSingleItem("one line per shed property — the healthy offloaded neighbour is not news");
        entry.Level.ShouldBe(LogLevel.Warning, "the read still answered; a rotting destination is not the run's failure");
        entry.Message.ShouldContain(artifactId.ToString(), Case.Sensitive, "an operator triaging the log needs the artifact the shed was about");
        entry.Message.ShouldContain(nameof(ArtifactContentUnavailableKind.BackendUnavailable), Case.Sensitive, "and the lane — restoring a destination is a different action than re-running the producer");
        entry.Exception.ShouldBeSameAs(cause, "the store's own verdict rides along rather than being summarised second-hand");
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

    /// <summary>Keeps every line the walk writes, formatted the way a sink would render it, so the test reads what an operator would.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    private static Guid ArtifactIdOf(JsonElement value) => value.GetProperty(NodeOutputArtifacts.RefKey).GetProperty("id").GetGuid();

    private static string? ReasonOf(JsonElement value) =>
        value.GetProperty(NodeOutputArtifacts.RefKey).TryGetProperty(NodeOutputArtifacts.ReasonKey, out var reason) ? reason.GetString() : null;

    private static JsonElement JsonString(string s) => JsonSerializer.SerializeToElement(s);
    private static JsonElement JsonNumber(int n) => JsonSerializer.SerializeToElement(n);
    private static JsonElement JsonRaw(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    /// <summary>
    /// Hermetic content-addressed store: dedups by SHA-256, scopes by team (cross-team reads return null). ONE
    /// artifact's read can be made to fail, which is the only way to reach the lanes a rotted destination produces —
    /// and per-artifact rather than store-wide, so a healthy neighbour can be OFFLOADED too and still read back.
    /// </summary>
    private sealed class InMemoryArtifactStore : IArtifactStore
    {
        private readonly ConcurrentDictionary<(Guid Team, string Sha), (Guid Id, byte[] Bytes, string ContentType)> _byContent = new();
        private readonly ConcurrentDictionary<(Guid Team, Guid Id), (string Sha, byte[] Bytes, string ContentType)> _byId = new();
        private readonly ConcurrentDictionary<Guid, Exception> _readFaults = new();

        public int Count => _byId.Count;

        public void FailReadOf(Guid artifactId, Exception fault) => _readFaults[artifactId] = fault;

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

        public Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken)
        {
            if (_readFaults.TryGetValue(artifactId, out var fault)) return Task.FromException<ArtifactBytes?>(fault);

            return Task.FromResult(_byId.TryGetValue((teamId, artifactId), out var v)
                ? new ArtifactBytes { Id = artifactId, Sha256 = v.Sha, ContentType = v.ContentType, Bytes = v.Bytes }
                : null);
        }

        public Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) =>
            Task.FromResult(_byId.TryGetValue((teamId, artifactId), out var v)
                ? new ArtifactMetadata { Id = artifactId, Sha256 = v.Sha, ContentType = v.ContentType, SizeBytes = v.Bytes.Length, CreatedAt = DateTimeOffset.UnixEpoch }
                : null);
    }
}
