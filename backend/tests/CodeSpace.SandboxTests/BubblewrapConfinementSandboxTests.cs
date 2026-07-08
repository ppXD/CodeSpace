using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12): the REAL bubblewrap + prlimit confinement of
/// <see cref="LocalProcessRunner"/> against a live kernel — workspace binding, operator-secret invisibility,
/// HOME redirection, egress severance (<c>--unshare-net</c>), and RLIMIT_NPROC/FSIZE caps read straight from
/// <c>/proc/self/limits</c>. These need a runner that can actually sandbox (bwrap + unprivileged user namespaces),
/// so they run for REAL only in the dedicated <c>sandbox-isolation.yml</c> privileged-container gate; everywhere
/// else (no bwrap) they degrade to the unconfined-trust branch and return. POSIX-only.
///
/// <para>Their own dedicated project (not <c>CodeSpace.UnitTests</c>) so a real-kernel isolation E2E never hides
/// inside the pure-logic unit tier — see <c>backend/tests/TESTING.md</c>. Class-level
/// <c>[Trait("Category", "Sandbox")]</c>; the whole project is the Sandbox tier.</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class BubblewrapConfinementSandboxTests : IDisposable
{
    private readonly LocalProcessRunner _runner = new();
    private readonly List<string> _spoolDirs = new();

    [Fact]
    public async Task Bubblewrap_confines_the_agent_to_its_workspace_and_blocks_operator_secrets()
    {
        if (BubblewrapSandbox.Available is null)
        {
            // The validation environment (Docker/Linux) sets CODESPACE_REQUIRE_SANDBOX=1 — there, an unavailable
            // sandbox is a HARD FAILURE, so this confinement assertion can never silently no-op into a green run.
            BubblewrapSandbox.IsRequired.ShouldBeFalse("CODESPACE_REQUIRE_SANDBOX is set but this host cannot sandbox (bwrap/userns) — the E2E cannot prove confinement here");
            return;
        }

        var workspace = TempDir();
        await File.WriteAllTextAsync(Path.Combine(workspace, "code.txt"), "WORKSPACE-VISIBLE\n");

        // An "operator secret" under /var/tmp — /var is NOT in the read-only root set, so it does not exist inside
        // the sandbox at all. (Unique + cleaned up — Rule 12.2/12.3.)
        var secretDir = Path.Combine("/var/tmp", "cs-host-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretDir);
        var secretPath = Path.Combine(secretDir, "id_rsa");
        await File.WriteAllTextAsync(secretPath, "OPERATOR-PRIVATE-KEY-7f3a");

        try
        {
            var spec = new SandboxSpec
            {
                Command = "/bin/sh",
                Args = new[] { "-c", $"cat code.txt; printf 'HOME=%s\\n' \"$HOME\"; cat {secretPath} 2>&1 || true" },
                WorkingDirectory = workspace,
                ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR" },
                TimeoutSeconds = 30,
            };

            var handle = await LaunchAsync(spec);
            var (result, lines) = await AttachCollectAsync(handle);
            var output = string.Join("\n", lines);

            result.Status.ShouldBe(SandboxStatus.Success, "the confined command runs to a clean exit");
            output.ShouldContain("WORKSPACE-VISIBLE", customMessage: "the workspace is bound read-write and readable inside the sandbox");
            output.ShouldNotContain("OPERATOR-PRIVATE-KEY", customMessage: "an operator secret OUTSIDE the binds is invisible to the confined agent — the core isolation guarantee");
            output.ShouldContain("HOME=" + Path.Combine(handle.SpoolDirectory, "agent-home"), customMessage: "HOME is redirected into the per-run config-home, not the operator's real home (so ~/.ssh etc. miss)");
        }
        finally { try { Directory.Delete(secretDir, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task Bubblewrap_severs_egress_when_network_is_disallowed()
    {
        if (BubblewrapSandbox.Available is null)
        {
            BubblewrapSandbox.IsRequired.ShouldBeFalse("CODESPACE_REQUIRE_SANDBOX is set but this host cannot sandbox (bwrap/userns)");
            return;
        }

        // AllowNetwork=false → --unshare-net → a fresh net namespace with only loopback, so a curl to a public IP
        // has no route and fails fast — deterministic regardless of the host's own connectivity. (curl is installed
        // in the validation container.)
        var spec = new SandboxSpec
        {
            Command = "/bin/sh",
            Args = new[] { "-c", "curl -s -m 4 -o /dev/null http://1.1.1.1 && echo NET-OK || echo NET-BLOCKED" },
            WorkingDirectory = TempDir(),
            AllowNetwork = false,
            TimeoutSeconds = 30,
        };

        var handle = await LaunchAsync(spec);
        var (_, lines) = await AttachCollectAsync(handle);

        string.Join("\n", lines).ShouldContain("NET-BLOCKED",
            customMessage: "with AllowNetwork=false the sandbox severs egress (loopback only) — no route to the internet / cloud-metadata");
    }

    [Fact]
    public async Task Bubblewrap_drops_every_capability()
    {
        if (BubblewrapSandbox.Available is null)
        {
            BubblewrapSandbox.IsRequired.ShouldBeFalse("CODESPACE_REQUIRE_SANDBOX is set but this host cannot sandbox (bwrap/userns) — the E2E cannot prove the capability drop here");
            return;
        }

        // Read the kernel's own view: the capability masks from /proc/self/status — shell-agnostic, proving
        // --cap-drop ALL actually took effect. This has TEETH: an unconfined (or merely userns-confined) process
        // shows a non-zero CapBnd, so the all-zero assertion fails the moment the drop is removed. The cgroup-NS
        // re-rooting is proven with teeth in DurableLaunchCgroupE2ETests, where a memory cap places the agent in a
        // real non-root leaf (here the launcher sits at the container cgroup root, so 0::/ would pass vacuously).
        var spec = new SandboxSpec
        {
            Command = "/bin/sh",
            Args = new[] { "-c", "grep '^Cap' /proc/self/status" },
            WorkingDirectory = TempDir(),
            TimeoutSeconds = 30,
        };

        var handle = await LaunchAsync(spec);
        var (result, lines) = await AttachCollectAsync(handle);

        result.Status.ShouldBe(SandboxStatus.Success, "the confined capability probe runs to a clean exit");

        var capLines = lines.Where(l => l.StartsWith("Cap")).ToList();
        capLines.ShouldNotBeEmpty("/proc/self/status reports the capability masks");
        foreach (var cap in capLines)
            cap.ShouldEndWith("0000000000000000", customMessage: $"--cap-drop ALL empties every capability mask, even userns-local ones — got '{cap}'");
    }

    // ─── Resource caps (prlimit wrapper — Linux only, gated on ProcessRlimits.Available) ─────────
    // 🟢 High fidelity (Rule 12): real durable launch (supervisor + prlimit + bwrap + agent), caps read straight
    // from the kernel (/proc/self/limits) so dash's missing `ulimit -u` can never silently no-op them. Skips on
    // macOS dev / no prlimit (the unconfined trust mode); the pure prlimit argv build is covered by ProcessRlimitsTests.

    [Fact]
    public async Task Caps_processes_and_file_size_via_prlimit_inherited_by_the_agent()
    {
        if (ProcessRlimits.Available is null) return;

        // Read the effective limits from the kernel — shell-agnostic, and proving prlimit → (bwrap →) agent
        // inheritance end to end. /proc/self/limits reports RLIMIT_NPROC as a count and RLIMIT_FSIZE in bytes.
        var spec = new SandboxSpec
        {
            Command = "/bin/sh",
            Args = new[] { "-c", "grep -E 'Max processes|Max file size' /proc/self/limits" },
            MaxProcesses = 8192,
            MaxFileSizeMb = 64,
            WorkingDirectory = TempDir(),
            TimeoutSeconds = 30,
        };

        var handle = await LaunchAsync(spec);
        var (_, lines) = await AttachCollectAsync(handle);
        var output = string.Join("\n", lines);

        output.ShouldContain("8192", customMessage: "the RLIMIT_NPROC fork-bomb cap reaches the agent");
        output.ShouldContain((64L * 1024 * 1024).ToString(), customMessage: "the 64 MiB RLIMIT_FSIZE single-file cap reaches the agent");
    }

    [Fact]
    public async Task Caps_a_runaway_file_write_so_it_cannot_fill_the_disk()
    {
        if (ProcessRlimits.Available is null) return;

        // A 1 MiB single-file cap: writing 5 MiB is truncated by RLIMIT_FSIZE (SIGXFSZ) well under 5 MiB, so a
        // runaway file (or stdout spool) can't fill the disk.
        var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "head -c 5242880 /dev/zero > big.dat 2>/dev/null; wc -c < big.dat" }, MaxFileSizeMb = 1, WorkingDirectory = TempDir(), TimeoutSeconds = 30 };

        var handle = await LaunchAsync(spec);
        var (_, lines) = await AttachCollectAsync(handle);

        var written = long.Parse(string.Join("", lines).Trim());
        written.ShouldBeGreaterThan(0);
        written.ShouldBeLessThan(5L * 1024 * 1024, "the 1 MiB RLIMIT_FSIZE cap truncates the 5 MiB write");
    }

    // ─── Shared real-process harness (GUID-keyed spool dirs, cleaned up — Rule 12.2/12.3) ─────────

    private async Task<SandboxHandle> LaunchAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        var handle = await _runner.LaunchAsync(spec, Guid.NewGuid().ToString("N"), ct);
        _spoolDirs.Add(handle.SpoolDirectory);
        return handle;
    }

    private async Task<(SandboxResult Result, List<string> Lines)> AttachCollectAsync(SandboxHandle handle, CancellationToken ct = default)
    {
        var lines = new List<string>();
        var result = await _runner.AttachAsync(handle, (l, _) => { lines.Add(l.Trim()); return Task.CompletedTask; }, ct);
        return (result, lines);
    }

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs-spool-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _spoolDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _spoolDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
