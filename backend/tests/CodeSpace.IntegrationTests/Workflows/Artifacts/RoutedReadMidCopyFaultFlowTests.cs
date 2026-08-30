using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// The ROUTED lane's half of "one rotted output is shed, not fatal". The local lane cannot stage this fault at all — a
/// local file either opens or it does not — but a provider hands back bytes and then stops all the time: a dropped
/// connection, a revoked mount, an object removed under an open handle. Until the copy's failures were classified, that
/// fault escaped the whole-object read untyped and took the entire run-detail read down with it, for exactly the tier
/// this slice exists for.
///
/// <para>Both halves of that classification are pinned here, because the copy's catch has to tell them apart: a
/// destination that stopped is shed, and a stream read after its own lease was let go is a defect that must arrive
/// intact. The second is the one the shared table can silently swallow, since a disposal derives from the exception a
/// backend refusing a locator raises.</para>
///
/// <para>Tier: 🟢 High-fidelity — real Postgres, the real route + location ledger, the real CAS runtime and the real
/// local-rwx driver. Only the provider's read STREAM is cut off part-way, on ONE object key, which is the one thing no
/// filesystem will do on request.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RoutedReadMidCopyFaultFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public RoutedReadMidCopyFaultFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_routed_stream_cut_off_mid_copy_costs_the_reader_that_cell_and_not_the_run()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        _roots.Add((await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId)).Root);

        var healthy = await OffloadRoutedAsync(teamId, new string('h', ArtifactStoreConfig.InlineThresholdBytes * 2));
        var faulted = await OffloadRoutedAsync(teamId, new string('f', ArtifactStoreConfig.InlineThresholdBytes * 2));
        var run = RunWith(Cell("planner", healthy.Outputs), Cell("agent", faulted.Outputs));

        using var scope = _fixture.BeginScope(builder => builder
            .RegisterInstance(new MidCopyFaultCatalog(ObjectKeyFor(faulted.Sha), DroppedTransfer)).As<IArtifactStorageDriverFactoryCatalog>().SingleInstance());

        var inflated = await scope.Resolve<IRunNodeOutputInflater>().InflateAsync(run, teamId, CancellationToken.None);

        inflated.Nodes[0].Outputs.GetProperty("body").GetString().ShouldBe(healthy.Value,
            "the routed cell whose stream is intact still inflates — a neighbour's dead connection is not this cell's fact");

        var shed = inflated.Nodes[1].Outputs.GetProperty("body");
        NodeOutputArtifacts.IsRef(shed).ShouldBeTrue("the cut-off cell keeps its pointer rather than costing the reader the run");
        shed.GetProperty(NodeOutputArtifacts.RefKey).GetProperty(NodeOutputArtifacts.ReasonKey).GetString().ShouldBe(
            nameof(ArtifactContentUnavailableKind.BackendUnavailable),
            "a stream that stopped mid-transfer is the destination failing, NOT the stored copy disagreeing with what was recorded — different lane, different operator action");
    }

    [Fact]
    public async Task A_stream_disposed_under_the_copy_reaches_the_caller_as_the_bug_it_is()
    {
        // The other half of the same catch. A dropped connection is a fact about the DESTINATION, so it sheds; a read
        // of a stream whose lease was already let go is a fact about US, and it arrives as an ObjectDisposedException —
        // which derives from the InvalidOperationException a backend refusing a locator raises. Shed on the same table
        // as that refusal, a disposal bug leaves the operator a rotted cell to chase and no defect to fix.
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        _roots.Add((await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId)).Root);

        var faulted = await OffloadRoutedAsync(teamId, new string('d', ArtifactStoreConfig.InlineThresholdBytes * 2));
        var run = RunWith(Cell("agent", faulted.Outputs));

        using var scope = _fixture.BeginScope(builder => builder
            .RegisterInstance(new MidCopyFaultCatalog(ObjectKeyFor(faulted.Sha), DisposedUnderTheCopy)).As<IArtifactStorageDriverFactoryCatalog>().SingleInstance());

        await Should.ThrowAsync<ObjectDisposedException>(() => scope.Resolve<IRunNodeOutputInflater>().InflateAsync(run, teamId, CancellationToken.None));
    }

    /// <summary>The destination failing under an open handle — a storage-plane fact the reader is meant to shed.</summary>
    private static Exception DroppedTransfer() => new IOException("The routed provider dropped the transfer part-way through the object.");

    /// <summary>Our own defect — a stream read after its lease was let go. Never a storage verdict, whatever it derives from.</summary>
    private static Exception DisposedUnderTheCopy() => new ObjectDisposedException(nameof(Stream));

    /// <summary>One oversize output property, offloaded through the REAL store to the team's routed destination, plus the sha its object key is built from.</summary>
    private async Task<OffloadedCell> OffloadRoutedAsync(Guid teamId, string value)
    {
        using var scope = _fixture.BeginScope();

        var outputs = new Dictionary<string, JsonElement> { ["body"] = JsonSerializer.SerializeToElement(value) };
        var offloaded = await NodeOutputArtifacts.OffloadLargeAsync(scope.Resolve<IArtifactStore>(), teamId, outputs, ArtifactStoreConfig.InlineThresholdBytes, CancellationToken.None);
        var artifactId = offloaded["body"].GetProperty(NodeOutputArtifacts.RefKey).GetProperty("id").GetGuid();

        var row = await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleAsync(a => a.Id == artifactId);
        row.CasArtifactObjectId.ShouldNotBeNull("precondition: the bytes are reachable only through the routed location ledger");

        return new OffloadedCell(JsonSerializer.SerializeToElement(offloaded), row.Sha256, value);
    }

    /// <summary>The routed object key one payload lands under — content-addressed and sharded exactly as <c>ArtifactStore.Routing</c> composes it.</summary>
    private static string ObjectKeyFor(string sha256) => $"workflow-artifacts/{sha256[..2]}/{sha256.Substring(2, 2)}/{sha256}";

    private sealed record OffloadedCell(JsonElement Outputs, string Sha, string Value);

    private static WorkflowRunNodeSummary Cell(string nodeId, JsonElement outputs) => new()
    {
        NodeId = nodeId, IterationKey = "", Status = NodeStatus.Success, Inputs = Empty, Outputs = outputs,
        StartedAt = DateTimeOffset.UnixEpoch, CompletedAt = DateTimeOffset.UnixEpoch, RerunnableFromHere = false,
    };

    private static WorkflowRunDetail RunWith(params WorkflowRunNodeSummary[] nodes) => new()
    {
        Id = Guid.NewGuid(), RunNumber = 1, SourceType = "manual", NormalizedPayload = Empty,
        Status = WorkflowRunStatus.Success, CreatedDate = DateTimeOffset.UnixEpoch, Nodes = nodes, Outputs = Empty,
    };

    private static JsonElement Empty => JsonDocument.Parse("{}").RootElement.Clone();

    public void Dispose()
    {
        foreach (var root in _roots.Where(Directory.Exists))
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>The build's real provider set, with ONE object key's read stream cut off part-way through by <paramref name="fault"/>.</summary>
    private sealed class MidCopyFaultCatalog : IArtifactStorageDriverFactoryCatalog
    {
        private readonly MidCopyFaultFactory _factory;

        public MidCopyFaultCatalog(string faultedObjectKey, Func<Exception> fault) => _factory = new MidCopyFaultFactory(faultedObjectKey, fault);

        public IArtifactStorageDriverFactory? Get(string providerTypeKey) =>
            string.Equals(providerTypeKey, LocalRwxArtifactStorageDriverFactory.TypeKey, StringComparison.Ordinal) ? _factory : null;

        public IArtifactStorageDriverFactory Require(string providerTypeKey) => Get(providerTypeKey) ?? throw new NotSupportedException(providerTypeKey);
    }

    private sealed class MidCopyFaultFactory : IArtifactStorageDriverFactory
    {
        private readonly LocalRwxArtifactStorageDriverFactory _real = new();
        private readonly string _faultedObjectKey;
        private readonly Func<Exception> _fault;

        public MidCopyFaultFactory(string faultedObjectKey, Func<Exception> fault) { _faultedObjectKey = faultedObjectKey; _fault = fault; }

        public string ProviderTypeKey => LocalRwxArtifactStorageDriverFactory.TypeKey;

        public async ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken) =>
            new MidCopyFaultDriver(await _real.CreateAsync(request, cancellationToken), _faultedObjectKey, _fault);
    }

    /// <summary>Delegates every verb to the real driver; the named key's opened stream serves a prefix and then dies.</summary>
    private sealed class MidCopyFaultDriver : IArtifactStorageDriver
    {
        private const int ServedBeforeFault = 4096;

        private readonly IArtifactStorageDriver _inner;
        private readonly string _faultedObjectKey;
        private readonly Func<Exception> _fault;

        public MidCopyFaultDriver(IArtifactStorageDriver inner, string faultedObjectKey, Func<Exception> fault) { _inner = inner; _faultedObjectKey = faultedObjectKey; _fault = fault; }

        public StorageProviderCapabilities Capabilities => _inner.Capabilities;

        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken) => _inner.PutAsync(request, cancellationToken);

        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) => _inner.HeadAsync(request, cancellationToken);

        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => _inner.DeleteAsync(request, cancellationToken);

        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) => _inner.ProbeAsync(request, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        public async ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken)
        {
            var opened = await _inner.OpenReadAsync(request, cancellationToken);

            if (!string.Equals(request.ObjectKey, _faultedObjectKey, StringComparison.Ordinal) || !opened.IsSuccess) return opened;

            return ArtifactStorageReadResult.Opened(new CutOffStream(opened.Content!, ServedBeforeFault, _fault), opened.ContentLength, opened.TotalLength, opened.Metadata!);
        }
    }

    /// <summary>A provider stream that serves <c>budget</c> bytes and then raises <c>fault</c> — the copy is already part-done when it lands.</summary>
    private sealed class CutOffStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _budget;
        private readonly Func<Exception> _fault;
        private long _served;

        public CutOffStream(Stream inner, int budget, Func<Exception> fault) { _inner = inner; _budget = budget; _fault = fault; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _served; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served >= _budget)
                throw _fault();

            var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _budget - _served)], cancellationToken);
            _served += read;

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override int Read(Span<byte> buffer)
        {
            var scratch = new byte[buffer.Length];
            var read = Read(scratch, 0, scratch.Length);
            scratch.AsSpan(0, read).CopyTo(buffer);

            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync() => await _inner.DisposeAsync();
    }
}
