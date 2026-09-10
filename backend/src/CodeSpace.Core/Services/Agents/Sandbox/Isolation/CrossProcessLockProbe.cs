using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.NativeLaunch;
using Serilog;

namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// Asks a SECOND PROCESS whether an exclusive open of a file this one already holds is refused — the only probe that
/// answers the question <see cref="EgressSubnetAllocator"/> depends on, because the reservation it protects is
/// contended by other worker PROCESSES.
///
/// <para><b>Why the in-process probe cannot be the last word.</b> .NET emulates <see cref="FileShare.None"/> on Unix
/// with an advisory <c>flock(fd, LOCK_EX|LOCK_NB)</c>. Where that is flock proper, two opens in one process are two
/// open file descriptions and the second IS refused — so the allocator's in-process check is a correct and free FAST
/// PATH, and this class is never reached. On a Linux NFS client, though, <c>flock()</c> is itself emulated as an fcntl
/// byte-range lock against the server (flock(2) NOTES; <c>local_lock</c> selects it, and the default
/// <c>local_lock=none</c> sends both flavours to the server). POSIX byte-range locks are per-PROCESS: a second
/// exclusive open from the SAME process is never refused there, however correctly the mount locks across processes.
/// Believing the in-process answer would degrade the allocator to process-local uniqueness — the exact guarantee the
/// host-level reservation exists to add — on precisely the NFS/RWX mount shape this deployment recommends.</para>
///
/// <para>The child is the BUNDLED BOOTSTRAP (<c>codespace-runner-host</c>), which every deployment and test output
/// already carries beside the app, answering <see cref="ExclusiveLockProbeChild.Argument"/> before it reads anything
/// else. It is asked once per worker process, plus once per exhaustion re-probe (<c>EgressSubnetAllocator</c>'s
/// <c>ExhaustionOrRefusal</c>), only when the in-process fast path came back unrefused, and it prints
/// one token in ~30-40ms. Everything else — a bootstrap that is missing, a start that fails, a child that hangs or
/// prints something else — is <see cref="Verdict.Unproven"/>: nothing was proven, which is the answer that DEGRADES
/// rather than the optimistic one. A probe must never fail a launch, so nothing here throws.</para>
/// </summary>
internal static class CrossProcessLockProbe
{
    /// <summary>What a second PROCESS proved about this filesystem's exclusive locking.</summary>
    internal enum Verdict
    {
        /// <summary>The child's open was refused: another process's lock is enforced here.</summary>
        Enforced,

        /// <summary>The child's open SUCCEEDED while this process held the file: nothing is enforcing the lock.</summary>
        Unenforced,

        /// <summary>The child could not answer. Neither direction was shown, so the caller must not assume the useful one.</summary>
        Unproven,
    }

    /// <summary>The seam the allocator asks through, so both verdicts are unit-testable without staging a filesystem that lies.</summary>
    internal delegate Verdict Runner(string probeFilePath);

    /// <summary>How long the child gets. Generous because it is paid ONCE per worker and only on a mount whose in-process answer was useless: a cold bootstrap start is ~1s, a warm one ~35ms.</summary>
    internal const int ChildTimeoutMs = 10_000;

    /// <summary>Ask the child, with the caller still holding <paramref name="probeFilePath"/> open exclusively.</summary>
    internal static Verdict Ask(string probeFilePath)
    {
        try { return Interpret(AskChild(probeFilePath)); }
        catch (Exception error)
        {
            // Every failure is the SAME fact — this host proved nothing — and none of them may fail the launch that
            // asked. Broad by intent: a missing bootstrap, a denied exec, a platform without process start.
            Log.Warning(error, "Cross-process exclusive-lock probe could not run; treating filtered-egress subnet locking as unproven");

            return Verdict.Unproven;
        }
    }

    private static string? AskChild(string probeFilePath)
    {
        using var child = Process.Start(StartInfoFor(probeFilePath));

        if (child is null) return null;

        var token = child.StandardOutput.ReadToEndAsync();

        if (!child.WaitForExit(ChildTimeoutMs)) { Kill(child); return null; }

        return token.Wait(ChildTimeoutMs) ? token.Result : null;
    }

    private static ProcessStartInfo StartInfoFor(string probeFilePath)
    {
        // The SAME resolver the durable launch uses, so an operator who relocates the bootstrap relocates this too.
        var info = new ProcessStartInfo(LocalProcessRunner.RunnerHostBinaryPath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = false };

        info.ArgumentList.Add(ExclusiveLockProbeChild.Argument);
        info.ArgumentList.Add(probeFilePath);

        return info;
    }

    /// <summary>Only a token the child actually printed is evidence — an empty, truncated or unrecognised answer is a child that did not answer.</summary>
    private static Verdict Interpret(string? token) => (token ?? "").Trim() switch
    {
        ExclusiveLockProbeChild.RefusedToken => Verdict.Enforced,
        ExclusiveLockProbeChild.GrantedToken => Verdict.Unenforced,
        _ => Verdict.Unproven,
    };

    private static void Kill(Process child)
    {
        try { child.Kill(entireProcessTree: true); } catch { /* best-effort: it is already gone, or it is not ours to kill */ }
    }
}
