using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The PURE bubblewrap argv builder (<see cref="BubblewrapSandbox.BuildArgs"/>) — unit-testable on any OS, since
/// it only constructs the confinement argument vector (no process, no probe). The REAL confinement (a child that
/// cannot read operator secrets) is proven by the Linux/bwrap E2E in <c>LocalProcessDurableRunnerTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class BubblewrapSandboxTests
{
    private static BwrapPlan Plan(string? home = "/spool/agent-home", string? wd = "/work/ws") => new()
    {
        Command = "/bin/sh",
        Args = new[] { "-c", "echo hi" },
        WorkingDirectory = wd,
        HomeDir = home,
        WritablePaths = new[] { "/work/ws", "/spool/agent-home" },
    };

    [Fact]
    public void Builds_a_confining_argv_over_a_readonly_root_with_only_the_run_paths_writable()
    {
        var a = BubblewrapSandbox.BuildArgs(Plan());

        // Fresh namespaces + a private session/proc/dev/tmp.
        a.ShouldContain("--unshare-user");
        a.ShouldContain("--unshare-pid");
        a.ShouldContain("--unshare-ipc");
        a.ShouldContain("--unshare-uts");
        a.ShouldContain("--die-with-parent");
        Adjacent(a, "--proc", "/proc").ShouldBeTrue();
        Adjacent(a, "--dev", "/dev").ShouldBeTrue();
        Adjacent(a, "--tmpfs", "/tmp").ShouldBeTrue();

        // The standard system roots are bound READ-ONLY (bind-if-present).
        foreach (var root in BubblewrapSandbox.ReadOnlyRootDirs)
            Triple(a, "--ro-bind-try", root, root).ShouldBeTrue($"{root} is bound read-only");

        // The ONLY read-WRITE host paths are the workspace + config-home.
        Triple(a, "--bind", "/work/ws", "/work/ws").ShouldBeTrue("workspace is bound read-write");
        Triple(a, "--bind", "/spool/agent-home", "/spool/agent-home").ShouldBeTrue("config-home is bound read-write");
        a.ShouldNotContain("/home");
        a.ShouldNotContain("/root");

        // HOME redirected into the sandbox; chdir into the workspace; command after the `--` terminator.
        Triple(a, "--setenv", "HOME", "/spool/agent-home").ShouldBeTrue();
        Adjacent(a, "--chdir", "/work/ws").ShouldBeTrue();
        var sep = a.ToList().IndexOf("--");
        sep.ShouldBeGreaterThan(0);
        a[sep + 1].ShouldBe("/bin/sh", "the real command follows the -- terminator");
        a[sep + 2].ShouldBe("-c");
        a[sep + 3].ShouldBe("echo hi");
    }

    [Fact]
    public void Defaults_HOME_to_tmp_when_no_config_home_is_supplied() =>
        Triple(BubblewrapSandbox.BuildArgs(Plan(home: null)), "--setenv", "HOME", "/tmp")
            .ShouldBeTrue("with no config-home, HOME points at the writable tmpfs so ~-relative reads still miss operator dotfiles");

    [Fact]
    public void Severs_the_network_only_when_sharing_is_disabled()
    {
        BubblewrapSandbox.BuildArgs(Plan() with { ShareNetwork = false }).ShouldContain("--unshare-net", customMessage: "no network → a fresh net namespace with only loopback");
        BubblewrapSandbox.BuildArgs(Plan() with { ShareNetwork = true }).ShouldNotContain("--unshare-net");
    }

    [Fact]
    public void Binds_an_absolute_command_directory_that_is_outside_the_standard_roots()
    {
        var a = BubblewrapSandbox.BuildArgs(Plan() with { Command = "/usr/local/custom/claude" });
        // /usr/local/custom is under /usr (a read-only root) → already covered, no extra bind needed.
        a.Count(x => x == "/usr/local/custom").ShouldBe(0);

        var b = BubblewrapSandbox.BuildArgs(Plan() with { Command = "/snap/bin/claude" });
        // /snap is NOT under a standard root → its dir is bound read-only so the binary stays reachable.
        Triple(b, "--ro-bind-try", "/snap/bin", "/snap/bin").ShouldBeTrue();
    }

    [Fact]
    public void Available_is_null_off_linux() =>
        // No bwrap concept off Linux → callers run unconfined (the documented degraded trust mode). On a Linux host
        // this is environment-dependent (needs working userns), so only assert the macOS/Windows guarantee.
        (OperatingSystem.IsLinux() || BubblewrapSandbox.Available is null).ShouldBeTrue();

    [Fact]
    public void ReadOnlyRootDirs_are_pinned() =>
        // Widening this re-exposes host paths to the untrusted agent — a deliberate, reviewed decision (Rule 8).
        BubblewrapSandbox.ReadOnlyRootDirs.ShouldBe(new[] { "/usr", "/bin", "/sbin", "/lib", "/lib64", "/lib32", "/etc", "/opt" });

    [Fact]
    public void CommandEnvVar_is_pinned() =>
        BubblewrapSandbox.CommandEnvVar.ShouldBe("CODESPACE_BWRAP_PATH");

    [Fact]
    public void RequireSandboxEnvVar_is_pinned() =>
        // A fail-closed deployment pins this name to mandate isolation — renaming it silently disarms the guard (Rule 8).
        BubblewrapSandbox.RequireSandboxEnvVar.ShouldBe("CODESPACE_REQUIRE_SANDBOX");

    [Fact]
    public void EnsureSatisfiable_fails_closed_only_when_isolation_is_required_but_unavailable()
    {
        Should.Throw<InvalidOperationException>(() => BubblewrapSandbox.EnsureSatisfiable(available: null, required: true));
        Should.NotThrow(() => BubblewrapSandbox.EnsureSatisfiable(available: null, required: false));
        Should.NotThrow(() => BubblewrapSandbox.EnsureSatisfiable(available: "bwrap", required: true));
    }

    private static bool Adjacent(IReadOnlyList<string> a, string flag, string value)
    {
        for (var i = 0; i + 1 < a.Count; i++)
            if (a[i] == flag && a[i + 1] == value) return true;
        return false;
    }

    private static bool Triple(IReadOnlyList<string> a, string flag, string v1, string v2)
    {
        for (var i = 0; i + 2 < a.Count; i++)
            if (a[i] == flag && a[i + 1] == v1 && a[i + 2] == v2) return true;
        return false;
    }
}
