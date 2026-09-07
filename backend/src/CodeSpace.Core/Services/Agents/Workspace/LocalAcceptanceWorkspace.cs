using System.Security.Cryptography;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using Microsoft.Win32.SafeHandles;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// An invocation's local directory continuity observation and explicitly named oracle digests. The held directory
/// descriptor prevents inode reuse after deletion. Checks observe the path before and after grading; a path-based
/// grader can still race a swap-and-restore between checks. This is neither a filesystem grant nor launch-time pinning.
/// </summary>
internal sealed class LocalAcceptanceWorkspace : IDisposable
{
    private readonly SafeFileHandle _directoryHandle;
    private readonly LocalAcceptanceFileIdentity _identity;
    private readonly IReadOnlyDictionary<string, byte[]> _oracleHashes;

    private LocalAcceptanceWorkspace(string directory, SafeFileHandle handle, LocalAcceptanceFileIdentity identity, IReadOnlyDictionary<string, byte[]> hashes)
    {
        Directory = directory;
        _directoryHandle = handle;
        _identity = identity;
        _oracleHashes = hashes;
    }

    public string Directory { get; }

    public static async Task<LocalAcceptanceWorkspace> CaptureAsync(string directory, IReadOnlyList<string> oraclePaths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new IOException("An absolute existing local workspace is required.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var handle = LocalAcceptanceFileIdentity.Open(root, directory: true);
        try
        {
            var identity = LocalAcceptanceFileIdentity.Read(handle);
            var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var path in oraclePaths) hashes[path] = await HashOracleAsync(root, path, cancellationToken).ConfigureAwait(false);
            var context = new LocalAcceptanceWorkspace(root, handle, identity, hashes);
            if (await context.CheckAsync(cancellationToken).ConfigureAwait(false) is { } failure) throw new IOException(failure);
            return context;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_directoryHandle.IsClosed) return "workspace-context-disposed";
        try
        {
            using var current = LocalAcceptanceFileIdentity.Open(Directory, directory: true);
            if (LocalAcceptanceFileIdentity.Read(current) != _identity) return "workspace-continuity-lost";
        }
        catch (IOException) { return "workspace-continuity-lost"; }
        foreach (var (path, expected) in _oracleHashes)
        {
            try
            {
                var actual = await HashOracleAsync(Directory, path, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return "oracle-integrity-changed";
            }
            catch (IOException) { return "oracle-integrity-changed"; }
            catch (UnauthorizedAccessException) { return "oracle-integrity-changed"; }
        }
        return null;
    }

    private static async Task<byte[]> HashOracleAsync(string root, string relativePath, CancellationToken cancellationToken) =>
        (await ObserveFileAsync(root, relativePath, cancellationToken).ConfigureAwait(false)).Digest;

    /// <summary>One full regular-file byte observation with bounded memory. A changed length is unknown, never a prefix fingerprint presented as a complete file.</summary>
    internal static async Task<FileObservation> ObserveFileAsync(string root, string relativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\0')) throw new IOException("OraclePaths requires literal relative file paths.");
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!WorkspaceArtifactGuard.IsStrictlyWithin(root, full)) throw new IOException("Oracle path escapes the workspace.");
        var current = root;
        foreach (var component in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Oracle symlinks are not supported, including intermediate components.");
        }
        using var handle = LocalAcceptanceFileIdentity.Open(full, directory: false);
        await using var stream = new FileStream(handle, FileAccess.Read);
        var length = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[8192];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("The observed file shrank while being read.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        if (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0) throw new IOException("The observed file grew while being read.");
        return new(hash.GetHashAndReset(), length);
    }

    public void Dispose() => _directoryHandle.Dispose();

    internal sealed record FileObservation(byte[] Digest, long SizeBytes)
    {
        public string Sha256 => Convert.ToHexStringLower(Digest);
    }
}
