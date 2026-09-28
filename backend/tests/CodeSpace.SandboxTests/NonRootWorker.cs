using System.Runtime.InteropServices;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// The shipped worker's posture, asserted rather than assumed: a non-root uid with no capabilities, on a host where
/// bubblewrap confines through unprivileged user namespaces and this process may NOT build a network namespace of its
/// own (<c>ip netns add</c> needs CAP_SYS_ADMIN). Every test in the <c>SandboxNonRoot</c> lane calls
/// <see cref="Require"/> first, so the lane cannot pass by running as root, or on a host that could fall back to a
/// veth namespace, and the trait selects the lane without any environment flag.
/// </summary>
internal static class NonRootWorker
{
    /// <summary>The trait value the non-root lane filters on (<c>--filter Category=SandboxNonRoot</c>).</summary>
    public const string Category = "SandboxNonRoot";

    /// <summary>
    /// True on Linux once the posture is proved; false on any other OS, where there is no such lane (Rule 12.1). On
    /// Linux a missing piece of the posture FAILS the test: a non-root arm that ran as root proves nothing about the
    /// posture it is named for.
    /// </summary>
    public static bool Require()
    {
        if (!OperatingSystem.IsLinux()) return false;

        GetEuid().ShouldNotBe(0u, "this is the non-root lane: run it as the worker's uid with no capabilities (setpriv --reuid 1654 --regid 1654 --clear-groups --inh-caps=-all --bounding-set=-all --no-new-privs); as root it proves nothing about the shipped posture");
        BubblewrapSandbox.Available.ShouldNotBeNull($"bubblewrap must confine as this uid ({BubblewrapSandbox.UnavailableReason}); check `sysctl kernel.apparmor_restrict_unprivileged_userns` and `bwrap --unshare-user --unshare-net true` as this user");
        FilteredEgressNetns.CanFilter.ShouldBeFalse("this uid must NOT be able to build a network namespace — that is the posture the relay exists for; a worker that can was given CAP_SYS_ADMIN");
        FilteredEgressNetns.FilterUnavailableReason.ShouldNotBeNull().ShouldNotStartWith(FilteredEgressNetns.IpForwardPath, customMessage: $"and the wall must be the namespace, not forwarding: this process built one and was stopped only at {FilteredEgressNetns.IpForwardPath}, so something gave it the privilege to build it (CAP_SYS_ADMIN, or a setuid ip)");

        return true;
    }

    /// <summary>The effective uid this test process runs as, for the lane's markers.</summary>
    public static uint EffectiveUid() => GetEuid();

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();
}
