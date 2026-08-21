using CodeSpace.Core.Settings;
using System.Text;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Backends;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The local-file <see cref="IArtifactBlobBackend"/> (D2): writes oversize artifact bytes to an env-rooted,
/// sha-sharded directory and resolves a file:// storage_url back to bytes. High-fidelity (Rule 12) — drives the
/// REAL backend against a REAL temp directory (its own per-test root via the env var), no mocks. Covers the
/// round-trip, content-addressed idempotence, and the read-path security guards (scheme + under-root).
/// </summary>
[Trait("Category", "Unit")]
public sealed class LocalFileArtifactBlobBackendTests : IDisposable
{
    private readonly string _root;

    public LocalFileArtifactBlobBackendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cs-artifact-backend-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static (LocalFileArtifactBlobBackend backend, string sha, byte[] bytes) Setup(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return (new LocalFileArtifactBlobBackend(), ArtifactStore.ComputeSha256Hex(bytes), bytes);
    }


    [Fact]
    public async Task Write_then_read_round_trips_identical_bytes_under_a_file_url()
    {
        var (backend, sha, bytes) = Setup("a large diff payload that would blow the inline budget");

        var url = await backend.WriteAsync(sha, bytes, CancellationToken.None);

        url.ShouldStartWith("file://", Case.Sensitive);
        (await backend.ReadAsync(url, CancellationToken.None)).ShouldBe(bytes);
    }

    [Fact]
    public async Task Write_is_content_addressed_idempotent_same_sha_same_url_no_error()
    {
        var (backend, sha, bytes) = Setup("idempotent content");

        var url1 = await backend.WriteAsync(sha, bytes, CancellationToken.None);
        var url2 = await backend.WriteAsync(sha, bytes, CancellationToken.None);   // file already exists → no-op

        url2.ShouldBe(url1, "the same sha maps to the same path → the same url, every time");
        (await backend.ReadAsync(url2, CancellationToken.None)).ShouldBe(bytes);
    }

    [Fact]
    public async Task Write_shards_the_path_by_sha_prefix()
    {
        var (backend, sha, bytes) = Setup("shard me");

        var url = await backend.WriteAsync(sha, bytes, CancellationToken.None);

        // <root>/<sha[0:2]>/<sha[2:4]>/<sha> — two fan-out levels keep any directory small.
        var path = new Uri(url).LocalPath;
        path.ShouldEndWith(Path.Combine(sha[..2], sha.Substring(2, 2), sha), Case.Sensitive);
    }

    [Fact]
    public async Task Streaming_write_copies_in_bounded_chunks_and_round_trips_without_a_whole_payload_request()
    {
        var backend = new LocalFileArtifactBlobBackend(_root);
        var bytes = Encoding.UTF8.GetBytes(new string('s', 600_000));
        var sha = ArtifactStore.ComputeSha256Hex(bytes);
        await using var content = new MaximumReadRequestStream(bytes, 128 * 1024);

        var url = await ((IArtifactBlobStreamWriter)backend).WriteStreamAsync(sha, content, bytes.LongLength, CancellationToken.None);

        content.LargestReadRequest.ShouldBeLessThanOrEqualTo(128 * 1024,
            "the local compatibility path must copy through a bounded buffer rather than ask its source for one payload-sized array");
        (await backend.ReadAsync(url, CancellationToken.None)).ShouldBe(bytes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Streaming_write_refuses_a_declared_length_mismatch_and_leaves_no_object_or_temp_file(int lengthDelta)
    {
        var backend = new LocalFileArtifactBlobBackend(_root);
        var bytes = Encoding.UTF8.GetBytes(new string('m', 300_000));
        var sha = ArtifactStore.ComputeSha256Hex(bytes);
        await using var content = new MaximumReadRequestStream(bytes, 128 * 1024);

        await Should.ThrowAsync<InvalidDataException>(() =>
            ((IArtifactBlobStreamWriter)backend).WriteStreamAsync(sha, content, bytes.LongLength + lengthDelta, CancellationToken.None));

        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ShouldBeEmpty(
            "a rejected stream may leave empty shard directories, but neither canonical bytes nor an upload temp may survive");
    }

    [Fact]
    public async Task Streaming_write_refuses_a_digest_mismatch_and_leaves_no_object_or_temp_file()
    {
        var backend = new LocalFileArtifactBlobBackend(_root);
        var bytes = Encoding.UTF8.GetBytes(new string('d', 300_000));
        var wrongSha = ArtifactStore.ComputeSha256Hex(Encoding.UTF8.GetBytes("different"));
        await using var content = new MaximumReadRequestStream(bytes, 128 * 1024);

        await Should.ThrowAsync<InvalidDataException>(() =>
            ((IArtifactBlobStreamWriter)backend).WriteStreamAsync(wrongSha, content, bytes.LongLength, CancellationToken.None));

        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ShouldBeEmpty(
            "digest admission happens before atomic placement, so corrupt identity cannot escape at the canonical path");
    }

    [Fact]
    public async Task Streaming_write_cancellation_removes_its_partial_temp_file()
    {
        var backend = new LocalFileArtifactBlobBackend(_root);
        var bytes = Encoding.UTF8.GetBytes(new string('c', 600_000));
        var sha = ArtifactStore.ComputeSha256Hex(bytes);
        using var cancellation = new CancellationTokenSource();
        await using var content = new CancelAfterFirstReadStream(bytes, cancellation);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ((IArtifactBlobStreamWriter)backend).WriteStreamAsync(sha, content, bytes.LongLength, cancellation.Token));

        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ShouldBeEmpty(
            "cancellation before atomic placement must remove the partially copied temp file");
    }

    [Fact]
    public async Task Write_rejects_a_non_hex_sha()
    {
        var backend = new LocalFileArtifactBlobBackend();
        await Should.ThrowAsync<ArgumentException>(() => backend.WriteAsync("not-a-sha", new byte[] { 1 }, CancellationToken.None));
    }

    [Fact]
    public async Task Read_rejects_a_non_file_url()
    {
        var backend = new LocalFileArtifactBlobBackend();
        await Should.ThrowAsync<InvalidOperationException>(() => backend.ReadAsync("https://evil.example/secret", CancellationToken.None));
    }

    [Fact]
    public async Task Read_rejects_a_file_url_resolving_outside_the_store_root()
    {
        // Defence-in-depth: a tampered storage_url pointing outside the configured root must be refused before
        // any filesystem touch (no arbitrary-file read via a doctored DB value).
        var backend = new LocalFileArtifactBlobBackend();
        var outside = new Uri(Path.Combine(Path.GetTempPath(), "totally-elsewhere-" + Guid.NewGuid().ToString("N"), "etc-passwd")).AbsoluteUri;

        await Should.ThrowAsync<InvalidOperationException>(() => backend.ReadAsync(outside, CancellationToken.None));
    }

    [Fact]
    public async Task Range_read_opens_only_the_requested_window_and_reports_total_length()
    {
        var backend = new LocalFileArtifactBlobBackend(_root);
        var bytes = Encoding.UTF8.GetBytes("0123456789abcdefghijklmnopqrstuvwxyz");
        var url = await backend.WriteAsync(ArtifactStore.ComputeSha256Hex(bytes), bytes, CancellationToken.None);

        var range = await backend.ReadRangeAsync(url, offset: 10, length: 8, CancellationToken.None);

        Encoding.UTF8.GetString(range.Bytes).ShouldBe("abcdefgh");
        range.TotalLength.ShouldBe(bytes.LongLength);
    }

    [Fact]
    public async Task Range_read_at_the_end_is_empty_and_past_the_end_is_rejected()
    {
        var backend = new LocalFileArtifactBlobBackend(_root);
        var bytes = Encoding.UTF8.GetBytes("short");
        var url = await backend.WriteAsync(ArtifactStore.ComputeSha256Hex(bytes), bytes, CancellationToken.None);

        (await backend.ReadRangeAsync(url, bytes.Length, 64, CancellationToken.None)).Bytes.ShouldBeEmpty();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => backend.ReadRangeAsync(url, bytes.Length + 1, 64, CancellationToken.None));
    }

    private sealed class MaximumReadRequestStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _maximumReadRequest;

        public MaximumReadRequestStream(byte[] bytes, int maximumReadRequest)
        {
            _inner = new MemoryStream(bytes, writable: false);
            _maximumReadRequest = maximumReadRequest;
        }

        public int LargestReadRequest { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            Observe(buffer.Length);
            return _inner.Read(buffer);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Observe(buffer.Length);
            return _inner.ReadAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        private void Observe(int requested)
        {
            LargestReadRequest = Math.Max(LargestReadRequest, requested);
            if (requested > _maximumReadRequest)
                throw new InvalidOperationException($"A read requested {requested} bytes; the bounded maximum is {_maximumReadRequest}.");
        }
    }

    private sealed class CancelAfterFirstReadStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly CancellationTokenSource _cancellation;
        private bool _read;

        public CancelAfterFirstReadStream(byte[] bytes, CancellationTokenSource cancellation)
        {
            _inner = new MemoryStream(bytes, writable: false);
            _cancellation = cancellation;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            if (!_read && read > 0)
            {
                _read = true;
                _cancellation.Cancel();
            }
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
