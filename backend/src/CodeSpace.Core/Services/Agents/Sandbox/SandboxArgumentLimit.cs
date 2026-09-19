using System.Globalization;
using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>
/// The kernel's PER-STRING ceiling on an <c>execve</c>, and the preflight that refuses an invocation which would hit
/// it. Linux enforces <c>MAX_ARG_STRLEN = 32 * PAGE_SIZE</c> on every argv AND envp string
/// (<c>include/uapi/linux/binfmts.h</c>; <c>fs/exec.c copy_strings()</c> → <c>valid_arg_len()</c>), and because
/// <c>strnlen_user</c> counts the terminating NUL the largest CONTENT one string can carry is a byte below that.
///
/// <para>This is NOT the total argv+envp budget — that is <c>max(min(_STK_LIM*3/4, RLIMIT_STACK/4), ARG_MAX)</c>,
/// about 2 MiB at the default 8 MiB stack, and it is what <c>getconf ARG_MAX</c> reports. An invocation can clear the
/// total by an order of magnitude and still be refused here, which is exactly why the wall is invisible: nothing the
/// operator can query names 131071, and <c>ulimit -s unlimited</c> raises the total while leaving this constant
/// untouched (it has no sysctl and no rlimit knob).</para>
///
/// <para>Two things this check does NOT cover, neither reachable from a goal. The model-broker host token is
/// substituted into the environment AFTER it runs (<c>LocalProcessRunner.NativeLaunch.cs</c>'s
/// <c>ResolveModelBrokerHost</c>), so a string within a few bytes of the ceiling could cross it post-check — a
/// hostname's worth of bytes on a value already at 131 071. And the TOTAL argv+envp budget above is unmeasured here:
/// a launch could clear every per-string check and still exceed it, which takes roughly sixteen maximal strings.</para>
///
/// <para>Without this preflight an over-long agent goal — the prompt is a trailing positional argument on both
/// harnesses — reached <c>execve</c>, was refused with E2BIG, and never replaced the process image. The supervisor
/// shell therefore never ran to write the spool's exit marker, so the runner reported the run as "exited with code -1
/// (no exit marker — the process vanished, likely an external/OOM kill or host teardown)": a confident, wrong cause
/// that sent the reader after memory ceilings, with an empty stderr and a retry that could never succeed.</para>
/// </summary>
public static class SandboxArgumentLimit
{
    /// <summary>The smallest page size any real Linux kernel runs — and therefore the strictest value of <see cref="MaxStringBytes"/>.</summary>
    private const int SmallestLinuxPageSize = 4096;

    /// <summary>
    /// The most UTF-8 bytes one argv or envp string may carry: <c>32 * PAGE_SIZE - 1</c>, the NUL taking the last byte.
    ///
    /// <para>COMPUTED rather than pinned, because a 16 KiB- or 64 KiB-page arm64 kernel genuinely accepts 512 KiB or
    /// 2 MiB and a hardcoded 131071 would refuse launches that host can run. Off Linux it falls back to the strictest
    /// Linux value instead of the local page size: macOS enforces no per-string cap at all (only a 1 MiB SHARED
    /// total), so a dev box trusting its own kernel would accept exactly what every 4 KiB-page worker refuses — the
    /// divergence that let this reach production in the first place. Failing closed here costs a dev host nothing and
    /// makes the existing Linux CI lanes a real check.</para>
    /// </summary>
    public static int MaxStringBytes { get; } = 32 * (OperatingSystem.IsLinux() ? Environment.SystemPageSize : SmallestLinuxPageSize) - 1;

    /// <summary>
    /// <c>null</c> when every string this launch would pass to <c>execve</c> fits; otherwise host metadata naming the
    /// first one that does not — its position, its actual size, the limit, and that this is a size limit rather than
    /// a memory one. An environment entry is named by its VARIABLE only: a value can be a credential and must never
    /// reach an error surface.
    /// </summary>
    public static string? Exceeded(SandboxSpec spec)
    {
        if (TooLong(spec.Command)) return Refusal("the executable path", spec.Command);

        for (var index = 0; index < spec.Args.Count; index++)
            if (TooLong(spec.Args[index])) return Refusal($"argument {index + 1}", spec.Args[index]);

        // envp entries are copied as one "NAME=VALUE" string, so the name and the '=' count toward the same ceiling.
        foreach (var entry in spec.Environment)
            if (TooLong(entry.Key + "=" + entry.Value)) return Refusal($"environment value {entry.Key}", entry.Key + "=" + entry.Value);

        return null;
    }

    private static bool TooLong(string value) => Encoding.UTF8.GetByteCount(value) > MaxStringBytes;

    private static string Refusal(string position, string value) =>
        $"{position} is {Bytes(Encoding.UTF8.GetByteCount(value))} bytes; {Accepts} at most {Bytes(MaxStringBytes)} bytes in a single argument or environment value (MAX_ARG_STRLEN = 32 x {Bytes(MaxStringBytes / 32 + 1)}-byte page). This is an argument-size limit, not a memory limit — no process is created and no memory is allocated. Shorten the text or pass it to the agent as a file.";

    /// <summary>Whose ceiling this is. Off Linux it is not the local kernel's — macOS enforces no per-string cap — so saying "this kernel" there would be false, and the honest claim is the one the refusal is actually protecting: the strictest worker this launch could land on.</summary>
    private static string Accepts => OperatingSystem.IsLinux() ? "this kernel accepts" : "the strictest Linux worker accepts";

    private static string Bytes(int count) => count.ToString(CultureInfo.InvariantCulture);
}
