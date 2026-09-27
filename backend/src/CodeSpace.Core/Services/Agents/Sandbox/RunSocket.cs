using System.Net.Sockets;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>
/// A per-run Unix-domain socket a sandbox reaches through a bind of the socket's DIRECTORY — the one rule both the MCP
/// endpoint and a model-broker lease listen by. <see cref="Listen"/> creates the socket's leaf directory owner-only
/// (0700), deletes a stale socket FILE left at the path, binds, restricts the socket to 0600 and only then listens.
///
/// <para><b>The directory is never deleted — not here, not on close.</b> A running sandbox's bind pins the directory's
/// inode, not its path. Replacing the socket FILE inside it is seen by the sandbox (the next connect reaches the new
/// listener), which is what makes a worker restart survivable; a directory removed and recreated at the same path is
/// a different inode, invisible to every sandbox already bound to the old one. So the only thing ever removed is the
/// file (<see cref="Remove"/>), and the directory goes with the run's spool.</para>
/// </summary>
internal static class RunSocket
{
    /// <summary>
    /// Bind and listen on <paramref name="socketPath"/>, or throw with nothing left open. A stale FILE at the path — a
    /// crashed incarnation's socket, bound to nothing — is deleted first; whoever calls this must already own the path,
    /// because a live peer's socket would be deleted just the same.
    /// </summary>
    public static Socket Listen(string socketPath, int backlog, Guid runId, ILogger logger)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            CreateOwnerOnlyDirectory(Path.GetDirectoryName(socketPath)!, runId, logger);
            Remove(socketPath);

            listener.Bind(new UnixDomainSocketEndPoint(socketPath));

            // Tighten to 0600 BEFORE the listener is reachable: a connect() before Listen cannot succeed, so this
            // closes the window where the inode is group/other-writable under a permissive umask.
            SetOwnerOnly(socketPath, runId, logger);

            listener.Listen(backlog);

            return listener;
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    /// <summary>Delete the socket FILE at <paramref name="socketPath"/> — never its directory (see the type remarks). Best-effort: a file already gone is the outcome this wanted.</summary>
    public static void Remove(string socketPath)
    {
        try { File.Delete(socketPath); } catch { /* already gone, or not ours to remove */ }
    }

    /// <summary>
    /// Create the socket's directory restricted to the owner (0700) — AND restrict the directory that LISTS it, which is
    /// the one that decides whether the run's random segment is enumerable at all. A directory's own mode governs its
    /// CHILDREN; its name is listed by its parent. So 0700 on the leaf stops another local user entering the run's
    /// directory or reaching the socket, but only 0700 on the parent (a layout root such as <c>&lt;spool&gt;/&lt;key&gt;/mcp/</c>
    /// or the short-path fallback's <c>&lt;temp&gt;/cs-mcp/</c>) stops them reading the segment out of a listing — and
    /// the segment is on bubblewrap's bind argv, so it is not secret from a same-uid reader either way. The parent is
    /// restricted ONLY when it is one of the roots the layout mints, never an arbitrary ancestor: the system temp root is
    /// somebody else's.
    ///
    /// <para>Best-effort on the modes (a chmod failure is a Warning, not a failed listener: the run's token remains the
    /// authoritative gate); a no-op on Windows, where unix modes don't apply.</para>
    /// </summary>
    private static void CreateOwnerOnlyDirectory(string directory, Guid runId, ILogger logger)
    {
        Directory.CreateDirectory(directory);

        if (OperatingSystem.IsWindows()) return;

        RestrictToOwner(directory, runId, logger);

        if (Path.GetDirectoryName(directory) is { Length: > 0 } parent && IsSocketRoot(parent)) RestrictToOwner(parent, runId, logger);
    }

    /// <summary>True for the directories the runner's layout mints as socket roots — the ones whose children are per-run segments and nothing else, so 0700 on them costs no other consumer anything.</summary>
    private static bool IsSocketRoot(string parent) =>
        Path.GetFileName(parent) is LocalProcessRunner.McpSocketDir or LocalProcessRunner.McpShortSocketRoot or LocalProcessRunner.ModelBrokerSocketDir or LocalProcessRunner.ModelBrokerShortSocketRoot;

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void RestrictToOwner(string directory, Guid runId, ILogger logger)
    {
        try { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (Exception ex) { logger.LogWarning(ex, "Agent run {RunId}: could not restrict the socket directory {SocketDirectory} to 0700; the run's socket directory name may be listable by another local user on this host", runId, directory); }
    }

    /// <summary>Restrict the socket file to the owner (0600) so another local user can't connect to it. A no-op on Windows where unix file modes don't apply. Best-effort — a chmod failure must NOT fail the listener (the run's token is the authoritative gate), but it's logged as a Warning so it isn't fully silent.</summary>
    private static void SetOwnerOnly(string socketPath, Guid runId, ILogger logger)
    {
        if (OperatingSystem.IsWindows()) return;

        try { File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) { logger.LogWarning(ex, "Agent run {RunId}: could not restrict the socket {SocketPath} to 0600; it may be group/other-accessible on this host", runId, socketPath); }
    }
}
