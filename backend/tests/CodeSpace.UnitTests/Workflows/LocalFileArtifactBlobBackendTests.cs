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
    private readonly string? _originalEnv;

    public LocalFileArtifactBlobBackendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cs-artifact-backend-test-" + Guid.NewGuid().ToString("N"));
        _originalEnv = Environment.GetEnvironmentVariable(LocalFileArtifactBlobBackend.StoreDirEnvVar);
        Environment.SetEnvironmentVariable(LocalFileArtifactBlobBackend.StoreDirEnvVar, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LocalFileArtifactBlobBackend.StoreDirEnvVar, _originalEnv);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static (LocalFileArtifactBlobBackend backend, string sha, byte[] bytes) Setup(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return (new LocalFileArtifactBlobBackend(), ArtifactStore.ComputeSha256Hex(bytes), bytes);
    }

    [Fact]
    public void Store_dir_env_var_name_pinned_for_operators()
    {
        // Rule 8 — operators point this at a durable mount; renaming silently relocates every artifact.
        LocalFileArtifactBlobBackend.StoreDirEnvVar.ShouldBe("CODESPACE_ARTIFACT_STORE_DIR");
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
}
