using System.Net;
using System.Net.Sockets;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.SandboxTests;

[Trait("Category", "Sandbox")]
[Collection("Cgroup")]
public sealed class CommandIsolationE2ETests(CgroupArenaFixture fixture)
{
    [KernelTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commands_can_write_their_workspace_but_cannot_read_other_workspaces_or_write_system_roots(bool stream)
    {
        var root = Directory.CreateTempSubdirectory("cs-command-kernel-").FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var secret = Path.Combine(root, "outside-secret");
        await File.WriteAllTextAsync(secret, Guid.NewGuid().ToString("N"));
        try
        {
            var script = "import os,sys; from pathlib import Path; assert not Path(sys.argv[1]).exists(); Path('output.txt').write_text('permitted'); " +
                "\ntry:\n fd=os.open('/etc/hostname', os.O_WRONLY)\nexcept OSError:\n pass\nelse:\n os.close(fd); raise AssertionError('system root writable')\nprint('isolated')";
            var result = await RunAsync(stream, new SandboxSpec { Command = "/usr/bin/python3", Args = new[] { "-c", script, secret }, WorkingDirectory = workspace, TimeoutSeconds = 20 });
            result.Status.ShouldBe(SandboxStatus.Success, result.Stderr);
            (await File.ReadAllTextAsync(Path.Combine(workspace, "output.txt"))).ShouldBe("permitted");
            File.Exists(secret).ShouldBeTrue();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [KernelTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commands_given_a_read_only_workspace_can_read_and_git_diff_it_but_cannot_write_it(bool stream)
    {
        // A read-only reviewer reads the change under review with git: diff, log, show and status must all work over a
        // workspace mounted read-only, while a write to the tree or to .git fails with EROFS — the kernel's refusal,
        // not a CLI's permission mode.
        var root = Directory.CreateTempSubdirectory("cs-command-readonly-").FullName;
        try
        {
            var (workspace, baseCommit, headCommit) = TwoCommitRepository(root);
            var script = string.Join('\n',
                "fail() { printf '%s\\n' \"$1\" >&2; exit \"$2\"; }",
                "git diff \"$1\" \"$2\" > /tmp/diff 2>/tmp/err || fail \"git diff failed: $(cat /tmp/err)\" 10",
                "grep -q MARKER-NEW /tmp/diff || fail \"git diff did not show the change: $(cat /tmp/diff)\" 11",
                "[ \"$(git log --oneline | wc -l)\" -eq 2 ] || fail \"git log did not list both commits\" 12",
                "git show --stat HEAD > /dev/null 2>/tmp/err || fail \"git show failed: $(cat /tmp/err)\" 13",
                "git status --porcelain > /tmp/status 2>/tmp/err || fail \"git status failed: $(cat /tmp/err)\" 14",
                "[ ! -s /tmp/status ] || fail \"git status is not clean: $(cat /tmp/status)\" 15",
                // Grouped: the shell's own `> tamper.txt` is what fails, before a `2>` on the same command would apply.
                "if { printf x > tamper.txt; } 2>/tmp/err; then fail 'wrote a file into the read-only workspace' 20; fi",
                "grep -q 'Read-only file system' /tmp/err || fail \"the workspace write failed, but not as EROFS: $(cat /tmp/err)\" 21",
                "if touch .git/tamper 2>/tmp/err; then fail 'wrote into the read-only .git' 22; fi",
                "grep -q 'Read-only file system' /tmp/err || fail \"the .git write failed, but not as EROFS: $(cat /tmp/err)\" 23",
                "printf ok > /tmp/scratch || fail 'the private /tmp is not writable' 24");

            var result = await RunAsync(stream, new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", script, "sh", baseCommit, headCommit }, WorkingDirectory = workspace, ReadOnlyWorkingDirectory = true, TimeoutSeconds = 30 });

            result.Status.ShouldBe(SandboxStatus.Success, $"exit {result.ExitCode}: {result.Stderr}");
            File.Exists(Path.Combine(workspace, "tamper.txt")).ShouldBeFalse("nothing written inside may reach the host workspace");
            File.Exists(Path.Combine(workspace, ".git", "tamper")).ShouldBeFalse();
            Git(workspace, "status", "--porcelain").ShouldBeEmpty("the workspace is exactly as the run found it");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [KernelTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Network_denial_blocks_a_real_host_listener_and_explicit_network_access_can_reach_it(bool stream)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var script = "import socket,sys\ntry:\n s=socket.create_connection(('127.0.0.1',int(sys.argv[1])),timeout=2); s.close()\nexcept OSError:\n sys.exit(7)";
        foreach (var allowNetwork in new[] { true, false })
        {
            var result = await RunAsync(stream, new SandboxSpec { Command = "/usr/bin/python3", Args = new[] { "-c", script, port.ToString() }, AllowNetwork = allowNetwork, TimeoutSeconds = 15 });
            result.Status.ShouldBe(allowNetwork ? SandboxStatus.Success : SandboxStatus.Failed, result.Stderr);
            result.ExitCode.ShouldBe(allowNetwork ? 0 : 7, "the exact socket refusal distinguishes network isolation from a broken command");
        }
    }

    [KernelTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_size_caps_reach_the_real_command(bool stream)
    {
        ProcessRlimits.Available.ShouldNotBeNull("this suite requires the real prlimit binary");
        var workspace = Directory.CreateTempSubdirectory("cs-command-fsize-").FullName;
        try
        {
            var script = "import resource; assert resource.getrlimit(resource.RLIMIT_FSIZE)[0] == 1048576; open('large.bin','wb').write(b'x'*4194304)";
            var result = await RunAsync(stream, new SandboxSpec { Command = "/usr/bin/python3", Args = new[] { "-c", script }, WorkingDirectory = workspace, MaxFileSizeMb = 1, TimeoutSeconds = 20 });
            result.Status.ShouldBe(SandboxStatus.Failed);
            new FileInfo(Path.Combine(workspace, "large.bin")).Length.ShouldBe(1048576);
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    [KernelTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Memory_caps_are_enforced_classified_from_the_kernel_and_reclaimed(bool stream)
    {
        var arena = fixture.Arena.ShouldNotBeNull($"the kernel suite requires delegated cgroups: {fixture.Why}");
        arena.HasPython.ShouldBeTrue();
        using var settings = RuntimeSettings.Override(s => s with { AgentCgroupRoot = arena.Root });
        var before = Directory.GetDirectories(arena.Root).Order().ToArray();
        foreach (var over in new[] { false, true })
        {
            var result = await RunAsync(stream, new SandboxSpec { Command = "/usr/bin/python3", Args = new[] { "-c", $"b=bytearray({(over ? 256 : 8)}*1024*1024); print('allocated')" }, MaxMemoryMb = 64, TimeoutSeconds = 30 });
            result.Status.ShouldBe(over ? SandboxStatus.ResourceExhausted : SandboxStatus.Success, result.Stderr);
            Directory.GetDirectories(arena.Root).Order().ToArray().ShouldBe(before, "both successful and OOM command leaves must be reclaimed");
        }
    }

    /// <summary>A repository with a base and a head commit whose one file changes between them; returns its path and both SHAs.</summary>
    private static (string Workspace, string Base, string Head) TwoCommitRepository(string root)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;

        Git(workspace, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(workspace, "app.txt"), "MARKER-OLD\n");
        Git(workspace, "add", "-A");
        Git(workspace, "commit", "-q", "-m", "base");
        var baseCommit = Git(workspace, "rev-parse", "HEAD");

        File.WriteAllText(Path.Combine(workspace, "app.txt"), "MARKER-NEW\n");
        Git(workspace, "commit", "-q", "-am", "head");

        return (workspace, baseCommit, Git(workspace, "rev-parse", "HEAD"));
    }

    private static string Git(string directory, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-c", "user.name=kernel-e2e", "-c", "user.email=kernel-e2e@codespace.test", "-c", "commit.gpgsign=false" }.Concat(arguments)) info.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {stderr}");

        return stdout.Trim();
    }

    private static Task<SandboxResult> RunAsync(bool stream, SandboxSpec spec)
    {
        BubblewrapSandbox.Available.ShouldNotBeNull("the kernel suite must execute confinement, never silently degrade");
        var runner = new LocalProcessRunner();
        return stream ? runner.RunStreamingAsync(spec, (_, _) => Task.CompletedTask, CancellationToken.None) : runner.RunAsync(spec, CancellationToken.None);
    }

    private sealed class KernelTheoryAttribute : TheoryAttribute
    {
        public KernelTheoryAttribute()
        {
            if (BubblewrapSandbox.Available is null && !BubblewrapSandbox.IsRequired && Environment.GetEnvironmentVariable("CODESPACE_REQUIRE_CGROUP") != "1")
                Skip = "Requires Linux bubblewrap and delegated cgroups; the privileged GitHub Actions lane is authoritative.";
        }
    }
}
