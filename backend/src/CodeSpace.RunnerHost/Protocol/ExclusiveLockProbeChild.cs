namespace CodeSpace.NativeLaunch;

/// <summary>
/// The CHILD half of the cross-process exclusive-lock probe: one <c>open(2)</c> of a file the PARENT is already
/// holding with <see cref="FileShare.None"/>, from a second process, reported as one token on stdout. Compiled into
/// both the bundled bootstrap (which answers <see cref="Argument"/> from its argv) and the app (whose
/// <c>CrossProcessLockProbe</c> spawns it and reads the token), so the two can never disagree about the contract.
///
/// <para><b>Why a second PROCESS is the only probe that answers the question.</b> .NET emulates
/// <see cref="FileShare.None"/> on Unix with an advisory <c>flock(fd, LOCK_EX|LOCK_NB)</c>, and on a Linux NFS client
/// <c>flock()</c> is emulated AGAIN — as an fcntl byte-range lock against the server (flock(2) NOTES; the client's
/// <c>local_lock</c> mount option selects this, and its default <c>local_lock=none</c> means both lock flavours go to
/// the server). POSIX byte-range locks are per-PROCESS, so a second exclusive open from the SAME process is never
/// refused there however well the mount locks — an in-process probe reads "nothing is enforcing this" on precisely
/// the mount shape (an NFS/RWX volume) whose cross-process locking works. A child gets the real answer.</para>
///
/// <para>The tokens are POSITIVE evidence, deliberately: the parent trusts a verdict only when the child printed one
/// of them, so a bootstrap that does not know this verb (an older binary), a start that fails, or a child that hangs
/// all read as "nothing was proven" rather than as an optimistic "enforced". The exit code carries nothing.</para>
/// </summary>
internal static class ExclusiveLockProbeChild
{
    /// <summary>The bootstrap verb. Takes ONE fully-qualified path (the bootstrap's own argv guard requires that), so it can never be confused with the launch verbs.</summary>
    public const string Argument = "--probe-lock";

    /// <summary>The second open was REFUSED — another process's exclusive lock is enforced on this filesystem.</summary>
    public const string RefusedToken = "lock-refused";

    /// <summary>The second open SUCCEEDED while the parent still held the file: nothing is enforcing the lock.</summary>
    public const string GrantedToken = "lock-granted";

    /// <summary>Neither: the child could not attempt the open at all (the file was gone, the rights were not there), which proves nothing in either direction.</summary>
    public const string UnknownToken = "lock-unknown";

    /// <summary>Attempt the second exclusive open and print exactly one token. Always exits 0 — the token is the answer, and an exit code cannot distinguish "refused" from "could not run".</summary>
    public static int RunChild(string probeFilePath)
    {
        Console.Out.WriteLine(Attempt(probeFilePath));
        Console.Out.Flush();

        return 0;
    }

    /// <summary>A "file not found" IS an <see cref="IOException"/>, so the narrow causes are filtered FIRST: read as a lock refusal, a probe file the parent had already deleted would prove enforcement that was never tested.</summary>
    private static string Attempt(string probeFilePath)
    {
        try
        {
            using var second = new FileStream(probeFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            return GrantedToken;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException) { return UnknownToken; }
        catch (IOException) { return RefusedToken; }
    }
}
