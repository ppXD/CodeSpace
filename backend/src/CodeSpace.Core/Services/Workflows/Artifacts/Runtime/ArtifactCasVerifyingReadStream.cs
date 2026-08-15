using System.Security.Cryptography;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>Streaming EOF integrity guard that owns both the provider stream and its driver lifetime.</summary>
internal sealed class ArtifactCasVerifyingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly StorageRuntimeDriverLease _driverLease;
    private readonly long _expectedSize;
    private readonly byte[] _expectedDigest;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly object _disposeGate = new();
    private long _observedSize;
    private bool _verified;
    private int _disposeStarted;
    private Task? _disposeTask;

    public ArtifactCasVerifyingReadStream(Stream inner, StorageRuntimeDriverLease driverLease, long expectedSize, byte[] expectedDigest)
    {
        _inner = inner;
        _driverLease = driverLease;
        _expectedSize = expectedSize;
        _expectedDigest = expectedDigest.ToArray();
        _driverLease.Own(new OwnedReadResources(_inner, _hash, _readGate));
    }

    public override bool CanRead => Volatile.Read(ref _disposeStarted) == 0 && _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _expectedSize;
    public override long Position { get => _observedSize; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        using var operation = _driverLease.BeginOperation();
        _readGate.Wait();
        try
        {
            ThrowIfDisposed();
            var read = _inner.Read(buffer);
            Observe(buffer[..read], buffer.Length > 0 && read == 0);
            return read;
        }
        finally { _readGate.Release(); }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operation = _driverLease.BeginOperation();
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Observe(buffer.Span[..read], buffer.Length > 0 && read == 0);
            return read;
        }
        finally { _readGate.Release(); }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Observe(ReadOnlySpan<byte> bytes, bool eof)
    {
        if (bytes.Length > 0)
        {
            _hash.AppendData(bytes);
            _observedSize += bytes.Length;
            if (_observedSize > _expectedSize) throw new InvalidDataException("Artifact CAS read exceeded its verified size.");
        }
        if (_verified || !eof && _observedSize != _expectedSize) return;
        var digest = _hash.GetHashAndReset();
        if (_observedSize != _expectedSize || !CryptographicOperations.FixedTimeEquals(digest, _expectedDigest))
            throw new InvalidDataException("Artifact CAS read failed its verified SHA-256/size identity.");
        _verified = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) { base.Dispose(false); return; }
        var disposal = DisposeOnceAsync();
        if (disposal.IsCompleted)
        {
            try { disposal.GetAwaiter().GetResult(); }
            catch (Exception exception) when (IsRecoverable(exception)) { /* Cleanup cannot revoke the verified read outcome. */ }
        }
        else _ = ObserveBackgroundDisposalAsync(disposal);
    }

    public override ValueTask DisposeAsync() => new(DisposeOnceAsync());

    public override void Flush() => throw new NotSupportedException();
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private Task DisposeOnceAsync()
    {
        TaskCompletionSource? completion = null;
        Task disposal;
        lock (_disposeGate)
        {
            if (_disposeTask != null) return _disposeTask;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            disposal = _disposeTask;
            Volatile.Write(ref _disposeStarted, 1);
        }
        _ = CompleteDisposalAsync(completion);
        return disposal;
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private static async Task ObserveBackgroundDisposalAsync(Task disposal)
    {
        try { await disposal.ConfigureAwait(false); }
        catch (Exception exception) when (IsRecoverable(exception)) { /* Synchronous disposal cannot surface late provider cleanup failure. */ }
    }

    private async Task DisposeCoreAsync()
    {
        try { await _driverLease.DisposeWhenDrainedAsync().ConfigureAwait(false); }
        catch (Exception exception) when (IsRecoverable(exception)) { /* Cleanup cannot revoke the verified read outcome. */ }
        finally
        {
            base.Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0) throw new ObjectDisposedException(nameof(ArtifactCasVerifyingReadStream));
    }

    private static bool IsRecoverable(Exception exception) => exception is not OutOfMemoryException and not AccessViolationException;

    private sealed class OwnedReadResources(Stream inner, IncrementalHash hash, SemaphoreSlim readGate) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { hash.Dispose(); }
                finally { readGate.Dispose(); }
            }
        }
    }
}
