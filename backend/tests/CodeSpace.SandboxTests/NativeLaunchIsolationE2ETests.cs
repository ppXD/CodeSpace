using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.NativeLaunch;
using Shouldly;

namespace CodeSpace.SandboxTests;

[Trait("Category", "Sandbox")]
public sealed class NativeLaunchIsolationE2ETests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cs-native-kernel-").FullName;
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(20));
    private readonly LocalProcessRunner _runner = new();
    private SandboxHandle? _handle;
    private string Key => Path.GetFileName(_root);
    private string Registry => NativeLaunchFiles.DirectoryFor(LocalProcessRunner.SpoolDirectoryFor(Key));

    [LinuxNativeFact]
    public async Task Observer_exit_does_not_stop_the_confined_process_and_broker_death_does()
    {
        await LaunchFromExitingObserverAsync(timeout: 12);
        var receipt = NativeLaunchFiles.Read<NativeLaunchReceipt>(Registry, NativeLaunchProtocol.ReceiptFile);
        var before = PulseLength();
        await Task.Delay(300);
        PulseLength().ShouldBeGreaterThan(before, "the detached workload survives its real observer process exit");
        using var broker = Process.GetProcessById(receipt.Broker.ProcessId);
        broker.Kill(entireProcessTree: false);
        await WaitAsync(() => !NativeProcess.IsAlive(receipt.Execution!));
        await AssertPulseStoppedAsync();
        var replay = await _runner.LaunchAsync(Spec(_root, 12), Key, _deadline.Token);
        replay.ProcessId.ShouldBe(_handle!.ProcessId);
        replay.Deadline.ShouldBe(_handle.Deadline);
        File.ReadAllLines(Path.Combine(_root, "starts")).Length.ShouldBe(1);
    }

    [LinuxNativeFact]
    public async Task Parent_death_protection_stops_the_confined_workload_even_while_the_guardian_is_suspended()
    {
        await LaunchFromExitingObserverAsync(timeout: 12);
        var receipt = NativeLaunchFiles.Read<NativeLaunchReceipt>(Registry, NativeLaunchProtocol.ReceiptFile);
        kill(receipt.Guardian!.ProcessId, 19).ShouldBe(0); // SIGSTOP: guardian cannot do the cleanup being asserted.
        try
        {
            using var broker = Process.GetProcessById(receipt.Broker.ProcessId);
            broker.Kill(entireProcessTree: false);
            await WaitAsync(() => !NativeProcess.IsAlive(receipt.Execution!));
            await AssertPulseStoppedAsync();
            File.Exists(Path.Combine(Registry, NativeLaunchProtocol.StopFile)).ShouldBeFalse("the paused guardian did not manufacture this kernel parent-death result");
        }
        finally { kill(receipt.Guardian.ProcessId, 18); } // SIGCONT for its ordinary cleanup.
    }

    [LinuxNativeFact]
    public async Task The_execution_deadline_remains_enforced_with_no_observer_process()
    {
        await LaunchFromExitingObserverAsync(timeout: 3);
        var receipt = NativeLaunchFiles.Read<NativeLaunchReceipt>(Registry, NativeLaunchProtocol.ReceiptFile);
        await WaitAsync(() => File.Exists(Path.Combine(Registry, NativeLaunchProtocol.StopFile)));
        NativeLaunchFiles.Read<NativeLaunchStop>(Registry, NativeLaunchProtocol.StopFile).Reason.ShouldBe("deadline");
        await WaitAsync(() => !NativeProcess.IsAlive(receipt.Execution!));
        await AssertPulseStoppedAsync();
        (await _runner.AttachAsync(_handle!, (_, _) => Task.CompletedTask, _deadline.Token)).Status.ShouldBe(SandboxStatus.TimedOut);
    }

    internal static SandboxSpec Spec(string directory, int timeout) => new()
    {
        Command = "/bin/sh", Args = ["-c", "printf 'start\\n' >> starts; touch ready; while :; do printf x >> pulse; sleep 0.05; done"], WorkingDirectory = directory, TimeoutSeconds = timeout,
    };

    private async Task LaunchFromExitingObserverAsync(int timeout)
    {
        OperatingSystem.IsLinux().ShouldBeTrue();
        BubblewrapSandbox.Available.ShouldNotBeNull("the kernel gate must exercise real required confinement");
        var info = SandboxTestHost.StartSelf("--durable-observer", _root, timeout.ToString());
        using var observer = Process.Start(info)!;
        var output = await observer.StandardOutput.ReadLineAsync(_deadline.Token);
        await observer.WaitForExitAsync(_deadline.Token);
        observer.ExitCode.ShouldBe(86, await observer.StandardError.ReadToEndAsync(_deadline.Token));
        _handle = JsonSerializer.Deserialize<SandboxHandle>(output!, NativeLaunchProtocol.Json)!;
        _handle.Confinement.ShouldNotBeNull();
        await WaitAsync(() => File.Exists(Path.Combine(_root, "ready")));
    }

    private long PulseLength() => File.Exists(Path.Combine(_root, "pulse")) ? new FileInfo(Path.Combine(_root, "pulse")).Length : 0;

    private async Task AssertPulseStoppedAsync()
    {
        await Task.Delay(200);
        var stopped = PulseLength();
        stopped.ShouldBeGreaterThan(0);
        await Task.Delay(300);
        PulseLength().ShouldBe(stopped, "the actual confined writer must stop; a dead controller alone is insufficient");
    }

    private async Task WaitAsync(Func<bool> condition)
    {
        while (!condition()) await Task.Delay(20, _deadline.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_handle is not null) await _runner.TerminateAsync(_handle, CancellationToken.None);
        try
        {
            var receipt = NativeLaunchFiles.Read<NativeLaunchReceipt>(Registry, NativeLaunchProtocol.ReceiptFile);
            foreach (var identity in new[] { receipt.Guardian, receipt.Broker })
                if (identity is not null && NativeProcess.IsAlive(identity)) { kill(identity.ProcessId, 18); using var process = Process.GetProcessById(identity.ProcessId); process.Kill(); }
        }
        catch (IOException) { }
        await Task.Delay(100);
        var spool = LocalProcessRunner.SpoolDirectoryFor(Key);
        if (Directory.Exists(spool)) Directory.Delete(spool, recursive: true);
        Directory.Delete(_root, recursive: true);
        _deadline.Dispose();
    }

    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);

    private sealed class LinuxNativeFactAttribute : FactAttribute
    {
        public LinuxNativeFactAttribute()
        {
            if ((!OperatingSystem.IsLinux() || BubblewrapSandbox.Available is null) && !BubblewrapSandbox.IsRequired) Skip = "Requires real Linux bubblewrap; the required-confinement GitHub Actions lane is authoritative.";
        }
    }
}
