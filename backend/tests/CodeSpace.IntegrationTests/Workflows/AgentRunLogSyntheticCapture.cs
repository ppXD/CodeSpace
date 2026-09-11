using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Messages.Agents;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>A pure function from byte position to byte value — the only thing the generating source and the regenerating destination share, and the reason 4 GiB can flow through the real write path while existing in neither.</summary>
internal static class SyntheticPayload
{
    /// <summary>
    /// Fills <paramref name="destination"/> with the payload's bytes starting at <paramref name="globalOffset"/>.
    ///
    /// <para>Every eight-byte block is the SplitMix64 finalizer of its own block index, and that finalizer is a
    /// bijection — so two windows at different offsets cover different block indices and can never hold the same
    /// bytes. That is a requirement, not a nicety: the CAS is content-addressed, so a formula whose windows repeat
    /// makes two segments ONE artifact object with two placements, and the segment/object correspondence this test
    /// asserts becomes a property of the payload instead of the capture. A narrower mix is exactly how that happens —
    /// anything that folds only the low bits of the position is periodic in the total, and the period is reached.</para>
    /// </summary>
    public static void Generate(Span<byte> destination, long globalOffset)
    {
        Span<byte> block = stackalloc byte[8];
        var index = 0;
        while (index < destination.Length)
        {
            var position = globalOffset + index;
            var within = (int)(position & 7);
            BinaryPrimitives.WriteUInt64LittleEndian(block, Mix((ulong)(position >> 3)));
            var take = Math.Min(8 - within, destination.Length - index);
            block.Slice(within, take).CopyTo(destination.Slice(index, take));
            index += take;
        }
    }

    /// <summary>SplitMix64's finalizer: full 64-bit avalanche, and invertible, which is what makes distinct block indices yield distinct blocks.</summary>
    private static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ value >> 30) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ value >> 27) * 0x94D049BB133111EBUL;

        return value ^ value >> 31;
    }
}

/// <summary>
/// A durable log source of arbitrary declared size that never holds its own payload: one reused segment buffer,
/// bytes minted per read, and an incremental SHA-256 over everything it has served. That digest is the only claim
/// about the source's content anywhere in this test — nothing compares against a materialized copy, because at
/// 4 GiB there is deliberately no copy to compare against.
/// </summary>
internal sealed class SyntheticLogSource(long totalBytes) : ISandboxDurableLogSource
{
    private readonly byte[] _buffer = new byte[AgentRunLogCaptureBridge.MaximumSegmentBytes];
    private readonly IncrementalHash _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _served;

    public long Served => _served;

    /// <summary>SHA-256 of every byte served, available once the source has reported end-of-source.</summary>
    public byte[] Digest => _digest.GetCurrentHash();

    public IReadOnlyList<SandboxDurableLogDescriptor> DescribeLogs(SandboxHandle handle) =>
    [
        new("stdout", AgentRunLogKinds.StandardOutput, AgentRunLogRepresentations.PlainTextContentType, AgentRunLogRepresentations.Utf8ContentEncoding, "synthetic-spool/v1"),
    ];

    public Task<SandboxDurableLogReadResult> ReadAsync(SandboxDurableLogReadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.OffsetBytes != _served) throw new Xunit.Sdk.XunitException($"The bridge re-read source offset {request.OffsetBytes} after serving {_served}; this source is forward-only and its digest would no longer describe the capture.");

        var available = totalBytes - _served;
        if (available == 0 && request.FinalDrain) return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.EndOfSource(false));
        if (available == 0 || (!request.FinalDrain && available < request.MinimumBytes)) return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.NoData());

        var length = (int)Math.Min(available, Math.Min(request.MaximumBytes, _buffer.Length));
        SyntheticPayload.Generate(_buffer.AsSpan(0, length), _served);
        _digest.AppendData(_buffer, 0, length);
        _served += length;
        return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.Available(_buffer.AsMemory(0, length)));
    }
}

/// <summary>
/// What the provider double remembers about one stored object: its identity, and the source span it can regenerate
/// from. Never its bytes — that is the whole point of driving 4 GiB through here.
/// </summary>
internal sealed record StoredSpan(long Length, byte[] Sha256, long GlobalStart, long ArrivalIndex);

/// <summary>
/// The destination as a ledger rather than a byte store: per object a <see cref="StoredSpan"/>, and one
/// arrival-order SHA-256 over every byte it was ever handed. Reads regenerate from the recorded span, so the real
/// CAS read-back and the real v3 manifest verification run unmodified over content this process never holds.
///
/// <para>The arrival cursor is what makes the global digest a sound claim: appends reach a destination strictly in
/// segment order (the database admits a segment only at the stream's locked head), so an arrival-order hash of a
/// run with no duplicate key IS the hash of the source. The test asserts both preconditions rather than assuming
/// them — <see cref="DuplicateKeys"/> must be zero and every recorded span's start must equal the segment offset
/// PostgreSQL holds.</para>
/// </summary>
internal sealed class SpanLedgerDriver(SpanLedger ledger) : IArtifactStorageDriver
{
    private const int BufferBytes = 128 * 1024;

    public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.StreamingWrite | StorageProviderCapabilities.StreamingRead | StorageProviderCapabilities.ConditionalCreate;

    public async ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken)
    {
        var received = await AbsorbAsync(request.Content, cancellationToken).ConfigureAwait(false);
        var stored = ledger.Admit(request.ObjectKey, received.Length, received.Sha256, request.Condition);
        if (stored == null) return ArtifactStoragePutResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.AlreadyExists, "exists"));

        return ArtifactStoragePutResult.Stored(Describe(request.ObjectKey, stored));
    }

    public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken)
    {
        var stored = ledger.Find(request.ObjectKey);
        if (stored == null) return ValueTask.FromResult(ArtifactStorageHeadResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Missing, "missing")));

        return ValueTask.FromResult(ArtifactStorageHeadResult.Found(Describe(request.ObjectKey, stored)));
    }

    public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken)
    {
        var stored = ledger.Find(request.ObjectKey);
        if (stored == null) return ValueTask.FromResult(ArtifactStorageReadResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Missing, "missing")));

        var content = new SyntheticSpanStream(stored.GlobalStart, stored.Length);
        return ValueTask.FromResult(ArtifactStorageReadResult.Opened(content, stored.Length, stored.Length, Describe(request.ObjectKey, stored)));
    }

    public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken)
    {
        ledger.Remove(request.ObjectKey);
        return ValueTask.FromResult(ArtifactStorageDeleteResult.Removed());
    }

    public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ArtifactStorageProbeResult { Status = ArtifactStorageProbeStatus.Available, Latency = TimeSpan.Zero });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Consume the whole caller-owned stream through one pooled window, keeping length and digest and discarding every byte.</summary>
    private async Task<Absorbed> AbsorbAsync(Stream content, CancellationToken cancellationToken)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        long length = 0;
        try
        {
            while (true)
            {
                var read = await content.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                digest.AppendData(buffer, 0, read);
                ledger.Observe(buffer, read);
                length += read;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return new Absorbed(length, digest.GetHashAndReset());
    }

    private static ArtifactStorageObjectMetadata Describe(string key, StoredSpan stored) => new()
    {
        ObjectKey = key, Length = stored.Length, Sha256 = null, ETag = $"etag-{Convert.ToHexStringLower(stored.Sha256)}",
    };

    private sealed record Absorbed(long Length, byte[] Sha256);
}

/// <summary>The destination's durable state, owned by the test so it outlives every driver lease the broker opens.</summary>
internal sealed class SpanLedger
{
    private readonly Dictionary<string, StoredSpan> _objects = new(StringComparer.Ordinal);
    private readonly IncrementalHash _arrivals = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly object _gate = new();
    private long _cursor;
    private long _arrivalIndex;
    private long _duplicateKeys;

    /// <summary>SHA-256 of every byte this destination was handed, in arrival order.</summary>
    public byte[] ArrivalDigest
    {
        get { lock (_gate) return _arrivals.GetCurrentHash(); }
    }

    public long TotalBytes
    {
        get { lock (_gate) return _cursor; }
    }

    /// <summary>A second write to a key already held. Must be zero before the arrival digest may be read as the source's.</summary>
    public long DuplicateKeys
    {
        get { lock (_gate) return _duplicateKeys; }
    }

    public IReadOnlyDictionary<string, StoredSpan> Objects
    {
        get { lock (_gate) return _objects.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal); }
    }

    public void Observe(byte[] buffer, int count)
    {
        lock (_gate) _arrivals.AppendData(buffer, 0, count);
    }

    /// <summary>Records one object at the cursor, or null when a create-only write hit an occupied key.</summary>
    public StoredSpan? Admit(string key, long length, byte[] sha256, ArtifactStorageWriteCondition condition)
    {
        lock (_gate)
        {
            if (_objects.TryGetValue(key, out var existing))
            {
                _duplicateKeys++;
                return condition == ArtifactStorageWriteCondition.CreateOnly ? null : existing;
            }
            var stored = new StoredSpan(length, sha256, _cursor, _arrivalIndex++);
            _objects[key] = stored;
            _cursor += length;
            return stored;
        }
    }

    public StoredSpan? Find(string key)
    {
        lock (_gate) return _objects.GetValueOrDefault(key);
    }

    public void Remove(string key)
    {
        lock (_gate) _objects.Remove(key);
    }
}

/// <summary>One object's bytes, regenerated on demand and bounded by its recorded length. Forward-only; nothing in the read path seeks.</summary>
internal sealed class SyntheticSpanStream(long globalStart, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var take = (int)Math.Min(buffer.Length, length - _position);
        if (take <= 0) return 0;
        SyntheticPayload.Generate(buffer[..take], globalStart + _position);
        _position += take;
        return take;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Serves the ledger driver for the profile the seeded world pins, so the REAL broker and coordinator run over it.</summary>
internal sealed class SpanLedgerFactory(SpanLedger ledger) : IArtifactStorageDriverFactory
{
    public string ProviderTypeKey => LocalRwxArtifactStorageDriverFactory.TypeKey;

    public ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken)
    {
        _ = request.CredentialHandle?.UseSecret(secret => secret.ValueKind == JsonValueKind.Object);
        return ValueTask.FromResult<IArtifactStorageDriver>(new SpanLedgerDriver(ledger));
    }
}

internal sealed class SpanLedgerCatalog(IArtifactStorageDriverFactory factory) : IArtifactStorageDriverFactoryCatalog
{
    public IArtifactStorageDriverFactory? Get(string providerTypeKey) => string.Equals(providerTypeKey, factory.ProviderTypeKey, StringComparison.Ordinal) ? factory : null;
    public IArtifactStorageDriverFactory Require(string providerTypeKey) => Get(providerTypeKey) ?? throw new NotSupportedException();
}
