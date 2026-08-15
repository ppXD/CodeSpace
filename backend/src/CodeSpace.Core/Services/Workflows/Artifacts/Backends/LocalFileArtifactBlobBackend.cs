using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Backends;

/// <summary>
/// A local-disk <see cref="IArtifactBlobBackend"/>: oversize artifact bytes are written under an env-rooted
/// directory, content-addressed by SHA-256 (sharded <c>&lt;root&gt;/ab/cd/abcd…</c> so no directory holds millions
/// of files), and referenced by a <c>file://</c> <c>storage_url</c>. Mirrors the agent spool's posture — plaintext
/// on local disk under OS permissions (secrets are already redacted from event/result text before persist); a
/// durable mount is the operator's job via the env var. A different transport (S3, a mirror) is a SIBLING impl
/// behind <see cref="IArtifactBlobBackend"/> (Rule 18), never a change here.
///
/// <para>Content-addressed ⇒ writes are idempotent: the same sha always maps to the same path, so a re-write is a
/// no-op. Reads validate the url resolves to a path UNDER the configured root (defence-in-depth against a tampered
/// <c>storage_url</c>) before touching the filesystem.</para>
/// </summary>
public sealed class LocalFileArtifactBlobBackend : IArtifactBlobBackend, ISingletonDependency
{
    private readonly string _root;

    /// <summary>Roots the store at <c>Artifacts:StoreDirectory</c> when configured, else where <see cref="CodeSpace.Core.Settings.DurableRoots"/> puts it — the path the container image already creates, or a per-user one off a container. Never the temp dir: artifacts outlive the rows that reference them only if the bytes do.</summary>
    public LocalFileArtifactBlobBackend()
        : this(CodeSpace.Core.Settings.RuntimeSettings.Current.ArtifactStoreDirectory) { }

    /// <summary>Test seam — the root is passed explicitly so a test pins behaviour without binding process-wide settings.</summary>
    internal LocalFileArtifactBlobBackend(string? configuredRoot) =>
        _root = CodeSpace.Core.Settings.DurableRoots.ArtifactStore(configuredRoot);

    public async Task<string> WriteAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (sha256 is not { Length: 64 } || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("sha256 must be a 64-char hex digest.", nameof(sha256));

        var path = PathFor(sha256);

        // Content-addressed ⇒ if the file is already there (same sha = same bytes), the write is a no-op.
        if (File.Exists(path)) return ToUrl(path);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a unique temp file then atomically move into place, so a concurrent writer (or a crash
        // mid-write) never leaves a torn artifact at the canonical path. A lost move race (another writer placed
        // identical content first) is fine — the content is identical, so we keep theirs and drop ours.
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes.ToArray(), cancellationToken).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Race: another writer created the canonical file between our Exists check and Move. Identical
            // content (same sha), so theirs is authoritative — discard our temp.
            TryDelete(tmp);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        return ToUrl(path);
    }

    public Task<bool> ExistsAsync(string storageUrl, CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(ResolveUnderRoot(storageUrl)));

    public async Task<byte[]> ReadAsync(string storageUrl, CancellationToken cancellationToken)
    {
        var path = ResolveUnderRoot(storageUrl);

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArtifactBlobRange> ReadRangeAsync(string storageUrl, long offset, int length, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        var path = ResolveUnderRoot(storageUrl);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (offset > stream.Length) throw new ArgumentOutOfRangeException(nameof(offset), "Artifact byte offset exceeds the object length.");

        var count = (int)Math.Min(length, stream.Length - offset);
        var bytes = new byte[count];
        var read = 0;
        while (read < count)
        {
            var current = await RandomAccess.ReadAsync(stream.SafeFileHandle, bytes.AsMemory(read, count - read), offset + read, cancellationToken).ConfigureAwait(false);
            if (current == 0) throw new EndOfStreamException("Artifact object ended before its reported length.");
            read += current;
        }

        return new ArtifactBlobRange(bytes, stream.Length);
    }

    // <root>/<sha[0:2]>/<sha[2:4]>/<sha> — two levels of fan-out keep any single directory small.
    private string PathFor(string sha256) => Path.Combine(_root, sha256[..2], sha256.Substring(2, 2), sha256);

    private static string ToUrl(string absolutePath) => new Uri(absolutePath).AbsoluteUri;   // file:///...

    /// <summary>Parse a file:// url to its path and assert it resolves UNDER the configured root — a tampered url pointing elsewhere is rejected.</summary>
    private string ResolveUnderRoot(string storageUrl)
    {
        if (!Uri.TryCreate(storageUrl, UriKind.Absolute, out var uri) || !uri.IsFile)
            throw new InvalidOperationException($"Unsupported artifact storage_url '{storageUrl}' for the local-file backend (expected file://).");

        var path = Path.GetFullPath(uri.LocalPath);
        var rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;

        if (!path.StartsWith(rootPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Artifact storage_url '{storageUrl}' resolves outside the artifact store root — refusing to read.");

        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort temp cleanup */ }
    }
}
