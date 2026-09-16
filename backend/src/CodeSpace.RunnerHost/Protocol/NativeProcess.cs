using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CodeSpace.Messages.Agents;

namespace CodeSpace.NativeLaunch;

internal static class NativeProcess
{
    private static readonly Lazy<string> Boot = new(ReadBootId);
    public static string BootId => Boot.Value;
    public static NativeProcessIdentity Current => Identify(Environment.ProcessId);
    public static NativeProcessIdentity Identify(int pid)
    {
        if (OperatingSystem.IsMacOS())
        {
            var info = ReadDarwinProcess(pid) ?? throw new IOException("Cannot identify a terminated native process.");
            return new NativeProcessIdentity(pid, DarwinStartTicks(info), BootId, $"darwin:{info.StartSeconds}:{info.StartMicroseconds}");
        }
        var fields = LinuxProcessFields(pid);
        if (fields[0] is "Z" or "X") throw new IOException("Cannot identify a terminated native process.");
        using var process = Process.GetProcessById(pid);
        return new NativeProcessIdentity(pid, process.StartTime.ToUniversalTime().Ticks, BootId, "linux:" + fields[19]);
    }

    /// <summary>
    /// The <c>/proc/pid/stat</c> states that mean the process has ALREADY EXITED: <c>Z</c> (zombie — exited, awaiting
    /// its parent's reap) and <c>X</c> (dead). The distinction matters because a pid in one of these still answers
    /// <c>kill(pid, 0)</c>, which is what <see cref="System.Diagnostics.Process.HasExited"/> asks for a process it did
    /// not start — so the managed answer calls a corpse "running" and this one does not.
    /// </summary>
    internal static readonly string[] DeadStates = ["Z", "X"];

    public static bool IsAlive(NativeProcessIdentity identity)
    {
        if (identity.BootId != BootId || identity.ProcessId <= 1) return false;
        if (OperatingSystem.IsMacOS()) return ReadDarwinProcess(identity.ProcessId) is { } info && identity.StartKey == $"darwin:{info.StartSeconds}:{info.StartMicroseconds}";
        try
        {
            var fields = LinuxProcessFields(identity.ProcessId);
            return !DeadStates.Contains(fields[0]) && identity.StartKey == "linux:" + fields[19];
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Win32Exception) when (IsAbsent(identity.ProcessId)) { return false; }
    }

    /// <summary>
    /// The STATE half of <see cref="IsAlive"/> without its birth-key half: is <paramref name="pid"/> a process that is
    /// still running here, by the same reading of <c>Z</c>/<c>X</c> and of Darwin's <c>SZOMB</c>? For the legacy
    /// (pre-native-handle) paths, which have no recorded birth key to compare and previously answered this question
    /// with <see cref="System.Diagnostics.Process.HasExited"/> — reporting a killed-but-unreaped tree as alive, the
    /// exact misread this method exists to stop. Callers that DO hold an identity must use <see cref="IsAlive"/>:
    /// without the birth key this cannot tell our process from a recycled pid.
    ///
    /// <para>Throws the way <see cref="IsAlive"/> does when liveness cannot be read AT ALL (an unreadable
    /// <c>/proc</c>, a <c>libproc</c> failure that is not "absent"), so a caller can tell "gone" from "unknowable"
    /// instead of folding the second into the first.</para>
    /// </summary>
    public static bool IsRunning(int pid)
    {
        if (pid <= 1) return false;
        if (OperatingSystem.IsMacOS()) return ReadDarwinProcess(pid) is not null;
        try { return !DeadStates.Contains(LinuxProcessFields(pid)[0]); }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Win32Exception) when (IsAbsent(pid)) { return false; }
    }

    public static void NewSession()
    {
        if (setsid() < 0) throw new IOException("Cannot establish the native controller session.");
    }

    public static void ProtectFromParentDeath(NativeProcessIdentity parent)
    {
        if (OperatingSystem.IsLinux() && prctl(1, 9, 0, 0, 0) != 0) throw new IOException("Cannot establish parent-death protection.");
        if (getppid() != parent.ProcessId || !IsAlive(parent)) throw new IOException("The launch broker no longer owns the bootstrap.");
    }

    /// <summary>Compare the recorded kernel birth key before signaling. This portable check/use sequence is not
    /// a kernel-atomic PID capability: a later platform slice must use pidfd/generation-bound containment where
    /// stronger signal attribution is required. Host/process crash lifecycle tests do not prove absence of this race.</summary>
    public static void KillSession(NativeProcessIdentity identity)
    {
        if (identity.BootId != BootId || identity.ProcessId <= 1 || identity.ProcessId == Environment.ProcessId) return;
        try
        {
            if (OperatingSystem.IsMacOS() && ReadDarwinProcess(identity.ProcessId) is { } info && identity.StartKey != $"darwin:{info.StartSeconds}:{info.StartMicroseconds}") return;
            if (OperatingSystem.IsLinux() && File.Exists($"/proc/{identity.ProcessId}/stat") && identity.StartKey != "linux:" + LinuxProcessFields(identity.ProcessId)[19]) return;
            using var process = Process.GetProcessById(identity.ProcessId);
            // Includes children that formed another session (e.g. bwrap --new-session). The negative-PID kill
            // also reaches ordinary descendants after a dead session leader has been reaped.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Win32Exception) when (IsAbsent(identity.ProcessId) || OperatingSystem.IsMacOS() && ReadDarwinProcess(identity.ProcessId) is null) { }
        kill(-identity.ProcessId, 9);
    }

    private static bool IsAbsent(int pid) => kill(pid, 0) != 0 && Marshal.GetLastPInvokeError() == 3; // ESRCH, never "permission denied".

    public static bool Same(NativeProcessIdentity left, NativeProcessIdentity right) => left.ProcessId == right.ProcessId && left.BootId == right.BootId && !string.IsNullOrEmpty(left.StartKey) && left.StartKey == right.StartKey;

    private static string[] LinuxProcessFields(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20) throw new InvalidDataException("Native process birth identity is incomplete.");
        return fields;
    }

    private static long DarwinStartTicks(DarwinProcessInfo info) => checked(DateTime.UnixEpoch.Ticks + (long)info.StartSeconds * TimeSpan.TicksPerSecond + (long)info.StartMicroseconds * 10);

    private static DarwinProcessInfo? ReadDarwinProcess(int pid)
    {
        // Darwin's proc_bsdinfo ABI (sys/proc_info.h, PROC_PIDTBSDINFO=3).
        // A nonzero argument includes zombies (XNU proc_info.c, proc_pidinfo); without it a zombie returns
        // ESRCH even though kill(pid, 0) succeeds. Keep unavailable/permission failures distinct from SZOMB.
        var read = proc_pidinfo(pid, 3, 1, out var info, Marshal.SizeOf<DarwinProcessInfo>());
        var error = Marshal.GetLastPInvokeError();
        if (read == Marshal.SizeOf<DarwinProcessInfo>() && info.Pid == pid) return info.Status == 5 ? null : info;
        if (IsAbsent(pid)) return null;
        throw new IOException($"Native process identity is unavailable (read={read}, errno={error}); liveness cannot be assumed.");
    }

    public static int Exec(NativeLaunchInvocation invocation)
    {
        if (!Path.IsPathFullyQualified(invocation.Command)) throw new InvalidDataException("Bootstrap executable must be absolute.");
        if (invocation.WorkingDirectory.Length > 0) Directory.SetCurrentDirectory(invocation.WorkingDirectory);
        var strings = new List<IntPtr>();
        IntPtr Vector(IEnumerable<string> values)
        {
            var pointers = values.Select(value =>
            {
                if (value.Contains('\0')) throw new InvalidDataException("Native invocation contains NUL.");
                var pointer = Marshal.StringToCoTaskMemUTF8(value); strings.Add(pointer); return pointer;
            }).Append(IntPtr.Zero).ToArray();
            var vector = Marshal.AllocCoTaskMem(pointers.Length * IntPtr.Size); strings.Add(vector);
            Marshal.Copy(pointers, 0, vector, pointers.Length); return vector;
        }
        try
        {
            var argv = Vector(new[] { invocation.Command }.Concat(invocation.Args));
            var env = Vector(invocation.Environment.Select(pair => pair.Key + "=" + pair.Value));
            execve(invocation.Command, argv, env);
            return 126;
        }
        finally { foreach (var pointer in strings) Marshal.FreeCoTaskMem(pointer); }
    }

    private static string ReadBootId()
    {
        if (OperatingSystem.IsLinux()) return "linux:" + File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        if (OperatingSystem.IsMacOS())
        {
            var size = (nuint)Marshal.SizeOf<Timeval>();
            if (sysctlbyname("kern.boottime", out var time, ref size, IntPtr.Zero, 0) != 0) throw new IOException("Cannot identify the host boot.");
            return $"darwin:{time.Seconds}:{time.Microseconds}";
        }
        throw new PlatformNotSupportedException("Durable native launch requires POSIX.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Timeval { public long Seconds; public int Microseconds; }
    [StructLayout(LayoutKind.Explicit, Size = 136)] private struct DarwinProcessInfo
    {
        [FieldOffset(4)] public uint Status;
        [FieldOffset(12)] public int Pid;
        [FieldOffset(120)] public ulong StartSeconds;
        [FieldOffset(128)] public ulong StartMicroseconds;
    }
    [DllImport("libc", SetLastError = true)] private static extern int setsid();
    [DllImport("libc", SetLastError = true)] private static extern int getppid();
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
    [DllImport("libc", SetLastError = true)] private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);
    [DllImport("libc", SetLastError = true)] private static extern int execve([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr argv, IntPtr env);
    [DllImport("libc", SetLastError = true)] private static extern int sysctlbyname([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out Timeval value, ref nuint size, IntPtr newValue, nuint newSize);
    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)] private static extern int proc_pidinfo(int pid, int flavor, ulong argument, out DarwinProcessInfo info, int size);
}
