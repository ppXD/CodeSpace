using System.Runtime.Versioning;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Messages.Agents;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox E2E (high fidelity, Rule 12): the production availability probe (<see cref="BubblewrapSandbox.ProbeAt"/>)
/// runs the real bwrap on this kernel, started inside a private mount namespace that masks <c>/proc</c> the way
/// Docker's default masked paths (and a Kubernetes pod's default <c>procMount</c>) do. On such a host a bare user
/// namespace still works while the fresh <c>/proc</c> every launch mounts is refused, so a probe that tested less
/// than the launch argv reported it as confining and every launch then died inside bwrap.
///
/// <para>Staging the namespace needs root, so it runs for real only in the privileged sandbox-isolation job;
/// elsewhere it returns. Every arm that ran prints <see cref="RanMarker"/>, which the lane requires, so a silent
/// return can never pass for coverage.</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class BubblewrapProbeE2ETests(ITestOutputHelper output)
{
    /// <summary>Printed by every arm that actually staged its host; the sandbox lane requires one per arm.</summary>
    public const string RanMarker = "[bwrap-probe-e2e] ran";

    [Theory]
    // /dev/null over /proc/kcore: the probe must report the host unavailable, and name the launch's mounts as the wall.
    [InlineData(true, SandboxConfinement.ReasonMountsDenied)]
    // The same staging without the overmount: the control that proves the wrapper is not what refused.
    [InlineData(false, null)]
    public void The_probe_runs_the_launch_argv_so_a_masked_proc_is_reported_unavailable(bool maskProc, string? expectedReason)
    {
        if (BubblewrapSandbox.Available is null)
        {
            BubblewrapSandbox.IsRequired.ShouldBeFalse("Sandbox:RequireConfinement is set but this host cannot sandbox (bwrap/userns) — the E2E cannot stage a host posture here");
            return;
        }

        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess) return;

        File.Exists("/proc/kcore").ShouldBeTrue("the staging masks /proc/kcore as Docker does; this kernel has none, so pick another of Docker's masked paths under /proc");

        using var host = new StagedProcHost(maskProc);

        var probe = BubblewrapSandbox.ProbeAt(host.BwrapPath);

        probe.Reason.ShouldBe(expectedReason,
            customMessage: $"masked /proc={maskProc}: the probe must answer for the argv a launch runs. Reproduce by hand as root: unshare -m sh -c 'mount --bind /dev/null /proc/kcore; bwrap --unshare-user --unshare-net --proc /proc --ro-bind / / -- true; echo exit=$?'");
        probe.Path.ShouldBe(expectedReason is null ? host.BwrapPath : null, "a host is available exactly when it has no unconfinable reason");

        output.WriteLine($"{RanMarker} {(maskProc ? "masked-proc" : "unmasked-proc")} reason={probe.Reason ?? "none"}");
    }

    /// <summary>
    /// A bwrap that starts in its own private mount namespace, with <c>/dev/null</c> bound over <c>/proc/kcore</c> when
    /// asked, and is otherwise this host's real bwrap. The overmount lives only in that namespace (private propagation),
    /// so nothing reaches the host; the script's GUID-named directory is removed on dispose (Rule 12.2/12.3).
    /// </summary>
    [SupportedOSPlatform("linux")]
    private sealed class StagedProcHost : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cs-bwrap-probe-" + Guid.NewGuid().ToString("N"));

        public StagedProcHost(bool maskProc)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(BwrapPath, Script(maskProc));
            File.SetUnixFileMode(BwrapPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string BwrapPath => Path.Combine(_dir, "staged-bwrap");

        private static string Script(bool maskProc) =>
            $"#!/bin/sh\nexec unshare --mount --propagation private /bin/sh -c '{(maskProc ? "mount --bind /dev/null /proc/kcore && " : "")}exec \"$0\" \"$@\"' {BubblewrapSandbox.Available} \"$@\"\n";

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
