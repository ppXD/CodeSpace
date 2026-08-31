using CodeSpace.Messages.Agents;
using Microsoft.Win32.SafeHandles;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;

/// <summary>
/// The SHARED containment + read layer every deliverable-reading grader stands on (artifact-present, LLM-judge,
/// citations, schema): a repo-relative path counts ONLY when it resolves to a real filesystem entry STRICTLY within
/// the workspace root — a blank path, a <c>../</c> escape, an absolute path, the root itself, and a SYMLINK whose
/// target in any path component leaves the clone all read as missing (fail-closed). One home for the clamp so a hardening fix lands
/// in every oracle at once, never in one grader's private copy.
/// </summary>
internal static class WorkspaceArtifactGuard
{
    /// <summary>
    /// True when the repo-relative path resolves to an existing file or directory STRICTLY within the workspace root.
    /// Every way of NOT being a real in-clone deliverable reads as missing (fail-closed): a blank path; a <c>../</c>
    /// escape or absolute path (lexically clamped); the workspace root itself (<c>.</c> / <c>""</c> — the clone dir is
    /// never a deliverable, and it always exists, so admitting it would be a silent pass); and a SYMLINK in any
    /// component whose target leaves the clone. The last guard matters because <see cref="File.Exists(string)"/> /
    /// <see cref="Directory.Exists(string)"/> FOLLOW symlinks — both <c>report.md → /etc/passwd</c> and
    /// <c>docs → /outside</c> spell in-bounds lexically — so each component resolves and re-clamps to root.
    /// </summary>
    public static bool ExistsWithin(string root, string relativePath)
    {
        return TryResolveExistingWithin(root, relativePath, out _);
    }

    /// <summary>
    /// Read a deliverable FILE's text under the same containment rules as <see cref="ExistsWithin"/>, bounded by
    /// <paramref name="maxBytes"/> (an over-cap file is truncated with a visible marker — a judged/parsed artifact must
    /// never balloon a prompt or the heap). False — with a fail-closed <paramref name="error"/> — when the path is not
    /// a real in-clone FILE (a directory is not readable content).
    /// </summary>
    public static bool TryReadWithin(string root, string relativePath, long maxBytes, out string content, out string? error)
    {
        content = "";
        error = null;

        if (!TryResolveExistingWithin(root, relativePath, out var full))
        {
            error = $"artifact-missing: {relativePath}";
            return false;
        }

        if (Directory.Exists(full))
        {
            error = $"artifact-not-a-file: {relativePath}";
            return false;
        }

        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);

        var buffer = new char[maxBytes];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);

        content = new string(buffer, 0, read);

        if (reader.Peek() >= 0) content += "\n[... truncated for grading ...]";

        return true;
    }

    /// <summary>
    /// Read a deliverable FILE's exact BYTES under the same containment rules as <see cref="ExistsWithin"/> —
    /// the typed-artifact capture's variant (DC-4). Unlike <see cref="TryReadWithin"/> an over-cap file is
    /// SKIPPED, never truncated: a judged prompt tolerates a truncation marker, but a captured artifact's bytes
    /// ARE the deliverable — a silently-clipped dataset is a lie, absence is honest. The refusal is TYPED
    /// (<paramref name="failure"/>) because a caller has to tell "the agent never wrote it" — the acceptance
    /// oracle's business — apart from "it exists and we did not take it", which is the caller's own capture loss.
    /// </summary>
    public static bool TryReadBytesWithin(string root, string relativePath, long maxBytes, out byte[] bytes, out WorkspaceArtifactReadFailure? failure)
    {
        bytes = Array.Empty<byte>();
        failure = null;

        if (!TryResolveExistingWithin(root, relativePath, out var full))
        {
            failure = WorkspaceArtifactReadFailure.Missing;
            return false;
        }

        if (Directory.Exists(full))
        {
            failure = WorkspaceArtifactReadFailure.NotAFile;
            return false;
        }

        if (new FileInfo(full).Length > maxBytes)
        {
            failure = WorkspaceArtifactReadFailure.OverCap;
            return false;
        }

        bytes = File.ReadAllBytes(full);
        return true;
    }

    /// <summary>
    /// Resolve one existing workspace file and pin the admitted inode behind a read handle. A caller must dispose the
    /// result. The handle is opened before this method returns, so replacing an admitted parent directory with a
    /// symlink cannot redirect either of a streaming writer's later passes to a different path.
    /// </summary>
    public static bool TryResolveFileWithin(string root, string relativePath, out WorkspaceArtifactFile file, out WorkspaceArtifactReadFailure? failure)
    {
        file = null!;
        failure = null;
        if (!TryResolveExistingWithin(root, relativePath, out var full))
        {
            failure = WorkspaceArtifactReadFailure.Missing;
            return false;
        }
        if (Directory.Exists(full))
        {
            failure = WorkspaceArtifactReadFailure.NotAFile;
            return false;
        }

        try
        {
            var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
            try
            {
                file = new WorkspaceArtifactFile(handle, RandomAccess.GetLength(handle));
                return true;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch (IOException)
        {
            failure = WorkspaceArtifactReadFailure.Missing;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failure = WorkspaceArtifactReadFailure.Missing;
            return false;
        }
    }

    /// <summary>
    /// Resolve every path component, not only the leaf. <c>root/link/file</c> must not be admitted when <c>link</c>
    /// points outside the workspace: <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> on the leaf alone returns null
    /// in that case because the FILE is not itself a symlink.
    /// </summary>
    private static bool TryResolveExistingWithin(string root, string relativePath, out string resolvedPath)
    {
        resolvedPath = "";
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        var lexicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var lexicalCandidate = Path.GetFullPath(Path.Combine(lexicalRoot, relativePath));
        if (!IsStrictlyWithin(lexicalRoot, lexicalCandidate)) return false;

        var rootInfo = new DirectoryInfo(lexicalRoot);
        if (!rootInfo.Exists) return false;
        var resolvedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? lexicalRoot));
        var current = resolvedRoot;
        var relative = Path.GetRelativePath(lexicalRoot, lexicalCandidate);
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (component.Length == 0) continue;
            var candidate = Path.Combine(current, component);
            var isDirectory = Directory.Exists(candidate);
            if (!isDirectory && !File.Exists(candidate)) return false;

            var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(candidate) : new FileInfo(candidate);
            current = Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate);
            if (!IsStrictlyWithin(resolvedRoot, current)) return false;
        }

        resolvedPath = current;
        return true;
    }

    /// <summary>True when <paramref name="candidate"/> lives STRICTLY under <paramref name="root"/> (a proper descendant — not root itself, not an escape).</summary>
    public static bool IsStrictlyWithin(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }
}

/// <summary>
/// One file identity admitted by the containment guard. Every read stream uses positional I/O against this one
/// handle, so streams have independent cursors without reopening the mutable workspace path. The owner disposes the
/// file after the artifact writer has disposed every stream it requested.
/// </summary>
internal sealed class WorkspaceArtifactFile : IDisposable
{
    private readonly SafeFileHandle _handle;

    public WorkspaceArtifactFile(SafeFileHandle handle, long lengthBytes)
    {
        _handle = handle;
        LengthBytes = lengthBytes;
    }

    public long LengthBytes { get; }

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        return new WorkspaceArtifactReadStream(_handle, LengthBytes);
    }

    public void Dispose() => _handle.Dispose();

    private sealed class WorkspaceArtifactReadStream : Stream
    {
        private readonly SafeFileHandle _handle;
        private readonly long _length;
        private long _position;
        private bool _disposed;

        public WorkspaceArtifactReadStream(SafeFileHandle handle, long length)
        {
            _handle = handle;
            _length = length;
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_position >= _length || buffer.Length == 0) return 0;
            var read = RandomAccess.Read(_handle, buffer[..(int)Math.Min(buffer.Length, _length - _position)], _position);
            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_position >= _length || buffer.Length == 0) return 0;
            var read = await RandomAccess.ReadAsync(_handle, buffer[..(int)Math.Min(buffer.Length, _length - _position)], _position, cancellationToken).ConfigureAwait(false);
            _position += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            _disposed = true;
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
