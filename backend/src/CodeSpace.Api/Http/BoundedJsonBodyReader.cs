using System.Text.Json;

namespace CodeSpace.Api.Http;

public static class BoundedJsonBodyReader
{
    public const int MaxBytes = 1_048_576;

    public static async Task<JsonDocument> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        using var stream = new LimitedStream(request.Body);
        try { return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 64 }, ct).ConfigureAwait(false); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { throw new BodyTooLargeException(); }
    }

    public sealed class BodyTooLargeException : IOException;

    private sealed class LimitedStream(Stream source) : Stream
    {
        private int _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await source.ReadAsync(buffer[..Math.Min(buffer.Length, MaxBytes + 1 - _read)], ct).ConfigureAwait(false);
            _read += read;
            if (_read > MaxBytes) throw new BodyTooLargeException();
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
