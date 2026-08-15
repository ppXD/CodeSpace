using System.Security.Cryptography;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>Streaming EOF integrity guard that owns both the provider stream and its driver lifetime.</summary>
internal sealed class ArtifactCasVerifyingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly ArtifactStorageDriverLease _driverLease;
    private readonly long _expectedSize;
    private readonly byte[] _expectedDigest;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _observedSize;
    private bool _verified;
    private bool _disposed;

    public ArtifactCasVerifyingReadStream(Stream inner, ArtifactStorageDriverLease driverLease, long expectedSize, byte[] expectedDigest)
    {
        _inner = inner;
        _driverLease = driverLease;
        _expectedSize = expectedSize;
        _expectedDigest = expectedDigest;
    }

    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _expectedSize;
    public override long Position { get => _observedSize; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Observe(buffer.AsSpan(offset, read), count > 0 && read == 0);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Observe(buffer[..read], buffer.Length > 0 && read == 0);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Observe(buffer.Span[..read], buffer.Length > 0 && read == 0);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Observe(buffer.AsSpan(offset, read), count > 0 && read == 0);
        return read;
    }

    private void Observe(ReadOnlySpan<byte> bytes, bool eof)
    {
        if (bytes.Length > 0)
        {
            _hash.AppendData(bytes);
            _observedSize += bytes.Length;
            if (_observedSize > _expectedSize) throw new InvalidDataException("Artifact CAS read exceeded its verified size.");
        }
        if (!eof || _verified) return;
        var digest = _hash.GetHashAndReset();
        if (_observedSize != _expectedSize || !CryptographicOperations.FixedTimeEquals(digest, _expectedDigest))
            throw new InvalidDataException("Artifact CAS read failed its verified SHA-256/size identity.");
        _verified = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            try { _inner.Dispose(); }
            finally
            {
                try { _hash.Dispose(); }
                finally { _driverLease.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
        }
        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _inner.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { _hash.Dispose(); }
            finally { await _driverLease.DisposeAsync().ConfigureAwait(false); }
        }
        GC.SuppressFinalize(this);
    }

    public override void Flush() => throw new NotSupportedException();
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
