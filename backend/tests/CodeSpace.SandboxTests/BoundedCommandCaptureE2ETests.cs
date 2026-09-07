using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.SandboxTests;

[Trait("Category", "Sandbox")]
[Collection("Cgroup")]
public sealed class BoundedCommandCaptureE2ETests(CgroupArenaFixture fixture)
{
    [KernelTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bounded_capture_preserves_confinement_and_exit_receipts_while_draining_overflow(bool streaming)
    {
        const int produced = 2 * 1024 * 1024;
        var root = Directory.CreateTempSubdirectory("cs-capture-kernel-").FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var outside = Path.Combine(root, "outside");
        await File.WriteAllTextAsync(outside, "must remain outside the command mount namespace");
        try
        {
            var script = "import os,sys; from pathlib import Path; assert not Path(sys.argv[1]).exists(); Path('receipt').write_text('executed'); " +
                $"sys.stdout.buffer.write(b'x'*{produced}+b'\\nretained\\n'); sys.stdout.flush(); sys.stderr.buffer.write(b'e'*4096); sys.stderr.flush(); sys.exit(9)";
            var lines = new List<string>();
            var result = await RunAsync(streaming, new SandboxSpec { Command = "/usr/bin/python3", Args = new[] { "-c", script, outside }, WorkingDirectory = workspace,
                CaptureBudget = new SandboxCaptureBudget { StdoutBytes = 32, StderrBytes = 16, StdoutLineBytes = 32 }, TimeoutSeconds = 20 }, lines);
            result.Status.ShouldBe(SandboxStatus.Failed, result.Stderr);
            result.ExitCode.ShouldBe(9, "capture overflow must not replace or replay the command outcome");
            (await File.ReadAllTextAsync(Path.Combine(workspace, "receipt"))).ShouldBe("executed");
            File.Exists(outside).ShouldBeTrue();
            var observation = result.Observation.ShouldNotBeNull();
            observation.Stdout!.ObservedBytes.ShouldBe(produced + 10);
            observation.Stdout.ReachedEndOfStream.ShouldBeTrue();
            observation.Stdout.CaptureComplete.ShouldBeFalse();
            observation.Stderr!.ObservedBytes.ShouldBe(4096);
            observation.Stderr.ReachedEndOfStream.ShouldBeTrue();
            observation.Stderr.CaptureComplete.ShouldBeFalse();
            result.Stderr.ShouldBe(new string('e', 16));
            if (streaming)
            {
                result.Stdout.ShouldBeEmpty();
                lines.ShouldBe(new[] { "retained" });
                observation.Stdout.DeliveryComplete.ShouldBe(false);
            }
            else
            {
                result.Stdout.ShouldBe(new string('x', 32));
                observation.Stdout.DeliveryComplete.ShouldBeNull();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [KernelTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bounded_capture_keeps_kernel_oom_classification_and_reclaims_its_cgroup(bool streaming)
    {
        var arena = fixture.Arena.ShouldNotBeNull($"the kernel suite requires delegated cgroups: {fixture.Why}");
        arena.HasPython.ShouldBeTrue();
        using var settings = RuntimeSettings.Override(settings => settings with { AgentCgroupRoot = arena.Root });
        var before = Directory.GetDirectories(arena.Root).Order().ToArray();
        var result = await RunAsync(streaming, new SandboxSpec { Command = "/usr/bin/python3", Args = new[] { "-c", "import sys; sys.stdout.write('observed\\n'); sys.stdout.flush(); b=bytearray(256*1024*1024)" }, MaxMemoryMb = 64,
            CaptureBudget = new SandboxCaptureBudget { StdoutBytes = 4, StderrBytes = 16, StdoutLineBytes = 32 }, TimeoutSeconds = 30 }, new List<string>());
        result.Status.ShouldBe(SandboxStatus.ResourceExhausted, result.Stderr);
        result.Observation!.Stdout!.ObservedBytes.ShouldBe(9);
        result.Observation.Stdout.ReachedEndOfStream.ShouldBeTrue();
        result.Observation.Stdout.CaptureComplete.ShouldBeFalse();
        Directory.GetDirectories(arena.Root).Order().ToArray().ShouldBe(before, "OOM capture must release the same resource domain as legacy command observation");
    }

    private static Task<SandboxResult> RunAsync(bool streaming, SandboxSpec spec, List<string> lines)
    {
        BubblewrapSandbox.Available.ShouldNotBeNull("the kernel suite must execute confinement, never silently degrade");
        var runner = new LocalProcessRunner();
        return streaming ? runner.RunStreamingAsync(spec, (line, _) => { lines.Add(line); return Task.CompletedTask; }, CancellationToken.None) : runner.RunAsync(spec, CancellationToken.None);
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
