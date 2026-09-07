using System.Collections.Concurrent;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

namespace CodeSpace.StorageTestWorker;

/// <summary>Fault injection around real CAS reads. No replacement content, metadata or success verdict is supplied.</summary>
public sealed class LogCompletionReadProbe(IArtifactCasRuntimeCoordinator inner, Func<CancellationToken, Task>? afterTwoReads = null) : IArtifactCasRuntimeCoordinator
{
    public ConcurrentQueue<LogSegmentReadObservation> Reads { get; } = new();
    public Task<ArtifactCasTransferResult> PutAsync(ArtifactCasTransferRequest request, CancellationToken cancellationToken) => inner.PutAsync(request, cancellationToken);

    public async Task<ArtifactCasReadResult> OpenReadAsync(ArtifactCasReadRequest request, CancellationToken cancellationToken)
    {
        var readBarrier = Reads.Count == 2 ? afterTwoReads : null;
        var result = await inner.OpenReadAsync(request, cancellationToken).ConfigureAwait(false);
        if (result is not ArtifactCasReadResult.Opened opened) return result;
        var observed = new LogSegmentReadObservation(request.ArtifactObjectId);
        return opened with { Content = new ObservedStream(opened.Content, observed, () => Reads.Enqueue(observed), readBarrier) };
    }

    private sealed class ObservedStream(Stream inner, LogSegmentReadObservation observed, Action started, Func<CancellationToken, Task>? beforeFirstRead) : Stream
    {
        private bool _started;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => observed.BytesRead; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_started)
            {
                _started = true;
                if (beforeFirstRead != null) await beforeFirstRead(cancellationToken).ConfigureAwait(false);
                started();
            }
            observed.MaximumRequestedBytes = Math.Max(observed.MaximumRequestedBytes, buffer.Length);
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            observed.BytesRead += read;
            if (buffer.Length > 0 && read == 0) observed.EofCount++;
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); observed.Disposed = true; GC.SuppressFinalize(this); }
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); observed.Disposed = true; } base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("The audit requires the production asynchronous streaming path.");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class LogSegmentReadObservation(Guid artifactObjectId)
{
    public Guid ArtifactObjectId { get; } = artifactObjectId;
    public long BytesRead { get; set; }
    public int MaximumRequestedBytes { get; set; }
    public int EofCount { get; set; }
    public bool Disposed { get; set; }
}
