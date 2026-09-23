using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.NativeLaunch;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

// Original audit counterexamples, retained as real process/filesystem regressions.
[Trait("Category", "Unit")]
[Collection("LocalProcessIdleWatchdog")]
public sealed class LocalProcessLaunchDiscoveryCounterexampleTests
{
    [Fact]
    public async Task Retrying_a_launch_with_a_lost_handle_must_not_start_the_same_effect_twice()
    {
        OperatingSystem.IsWindows().ShouldBeFalse("this native process audit requires POSIX");
        await using var fixture = new LaunchFixture();
        var runner = new LocalProcessRunner();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = await runner.LaunchAsync(fixture.Spec(), fixture.Key, deadline.Token); // Handle ACK deliberately not retained.
        await fixture.WaitForStartsAsync(1, deadline.Token);
        _ = await runner.LaunchAsync(fixture.Spec(), fixture.Key, deadline.Token); // Same request, same physical-intent key.
        using var replayObservation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        replayObservation.CancelAfter(TimeSpan.FromSeconds(2));
        try { await fixture.WaitForStartsAsync(2, replayObservation.Token); }
        catch (OperationCanceledException) when (replayObservation.IsCancellationRequested && !deadline.IsCancellationRequested) { }
        Console.WriteLine($"[launch-discovery] same-key start count={fixture.StartCount}; live child count={fixture.LiveChildren}");
        fixture.StartCount.ShouldBe(1, "an ACK-lost replay must discover the admitted launch instead of duplicating its physical side effect");
    }

    [Fact]
    public async Task A_preexisting_unreceipted_pid_slot_is_rejected_before_any_command_can_start()
    {
        await using var fixture = new LaunchFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Spool, "pid"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Should.ThrowAsync<NativeLaunchException>(() => new LocalProcessRunner().LaunchAsync(fixture.Spec() with { TimeoutSeconds = 1 }, fixture.Key, deadline.Token));
        error.Reason.ShouldBe("legacy-slot");
        fixture.StartCount.ShouldBe(0);
        fixture.LiveChildren.ShouldBe(0);
    }

    /// <summary>A fixture self-test: it pins this class's cleanup, not the product. The broker is still alive when the owned tree dies, so a dispose that returns before it has finished has deleted the spool under a writer.</summary>
    [Fact]
    public async Task Disposing_after_a_lost_handle_retry_outlives_every_launch_writer_and_removes_the_spool()
    {
        OperatingSystem.IsWindows().ShouldBeFalse("this native process audit requires POSIX");
        var fixture = new LaunchFixture();
        NativeLaunchReceipt receipt;
        try
        {
            var runner = new LocalProcessRunner();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _ = await runner.LaunchAsync(fixture.Spec(), fixture.Key, deadline.Token);
            await fixture.WaitForStartsAsync(1, deadline.Token);
            _ = await runner.LaunchAsync(fixture.Spec(), fixture.Key, deadline.Token);
            receipt = fixture.ReadReceipt();
        }
        finally { await fixture.DisposeAsync(); } // The act under test: a throw here fails the test with the fixture's own reason.

        Directory.Exists(fixture.Spool).ShouldBeFalse($"dispose must remove the whole spool, {NativeLaunchProtocol.DirectoryName} included");
        NativeProcess.IsAlive(receipt.Broker).ShouldBeFalse($"dispose returned while the broker could still write the receipt: {ProcessLiveness.Describe(receipt.Broker.ProcessId)}");
        receipt.Guardian.ShouldNotBeNull("a ready receipt names its guardian");
        NativeProcess.IsAlive(receipt.Guardian).ShouldBeFalse($"dispose returned while the guardian could still write a stop file: {ProcessLiveness.Describe(receipt.Guardian.ProcessId)}");
    }

    private sealed class LaunchFixture : IAsyncDisposable
    {
        private readonly Dictionary<int, DateTime> _owned = new();
        private readonly HashSet<int> _children = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-launch-audit-" + Guid.NewGuid().ToString("N"));
        public string Key { get; } = "launch-audit-" + Guid.NewGuid().ToString("N");
        public string Spool => LocalProcessRunner.SpoolDirectoryFor(Key);
        private string Registry => NativeLaunchFiles.DirectoryFor(Spool);
        private string Starts => Path.Combine(_root, "starts");
        private string Release => Path.Combine(_root, "release");
        public int StartCount => ReadStarts().Length;
        public int LiveChildren => _children.Count(IsOwnedAlive);

        public LaunchFixture() { Directory.CreateDirectory(_root); Directory.CreateDirectory(Spool); }

        public SandboxSpec Spec() => new()
        {
            Command = "/bin/sh", Args = ["-c", "printf '%s %s\\n' \"$PPID\" \"$$\" >>\"$1\"; while [ ! -f \"$2\" ]; do sleep 0.02; done", "launch-audit", Starts, Release],
            WorkingDirectory = _root, TimeoutSeconds = 5,
        };

        public async Task WaitForStartsAsync(int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                var starts = ReadStarts();
                if (starts.Length >= count)
                {
                    foreach (var line in starts)
                    {
                        var pids = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                        pids.Length.ShouldBe(2);
                        foreach (var pid in pids)
                        {
                            using var process = Process.GetProcessById(pid);
                            _owned.TryAdd(pid, process.StartTime.ToUniversalTime());
                        }
                        _children.Add(pids[1]);
                    }
                    return;
                }
                await Task.Delay(20, cancellationToken);
            }
        }

        public NativeLaunchReceipt ReadReceipt() => NativeLaunchFiles.Read<NativeLaunchReceipt>(Registry, NativeLaunchProtocol.ReceiptFile);

        private string[] ReadStarts() => File.Exists(Starts) ? File.ReadAllLines(Starts).Where(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 2).ToArray() : [];

        private bool IsOwnedAlive(int pid)
        {
            try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime() == _owned[pid]; }
            catch (ArgumentException) { return false; }
        }

        public async ValueTask DisposeAsync()
        {
            await File.WriteAllTextAsync(Release, "release");
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var (pid, started) in _owned)
            {
                if (!IsOwnedAlive(pid)) continue;
                using var process = Process.GetProcessById(pid);
                if (process.StartTime.ToUniversalTime() != started) continue;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cleanup.Token);
            }
            try { await WaitForLaunchControllersToExitAsync(); }
            finally
            {
                await DeleteAsync(_root);
                await DeleteAsync(Spool);
            }
        }

        /// <summary>What the broker normally needs once its execution is gone is one 50 ms poll and a receipt write; its own worst case is a 5 s drain plus a 3 s wait for the guardian.</summary>
        private static readonly TimeSpan ControllerPatience = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan DeletePatience = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Wait until the launch's own controllers are gone before touching its spool. The broker and guardian are not
        /// in <see cref="_owned"/>: the broker is the PARENT of the supervisor shell this fixture kills, so killing the
        /// owned tree leaves it running, and within one poll of its execution dying it publishes the terminal receipt
        /// into launch-v1 — a temporary file, a hard link, a rename. A recursive delete that listed the directory before
        /// that write fails its final rmdir with "Directory not empty". A controller that outlives the bound is killed,
        /// so nothing leaks, and named.
        /// </summary>
        private async Task WaitForLaunchControllersToExitAsync()
        {
            var controllers = LaunchControllers();
            var watch = Stopwatch.StartNew();
            while (controllers.Any(NativeProcess.IsAlive) && watch.Elapsed < ControllerPatience) await Task.Delay(20);

            var lingering = controllers.Where(NativeProcess.IsAlive).ToArray();
            if (lingering.Length == 0) return;

            var described = string.Join("\n", lingering.Select(controller => ProcessLiveness.Describe(controller.ProcessId)));
            var bootstrap = ProcessLiveness.DescribeBootstrap(Spool);
            foreach (var controller in lingering) NativeProcess.KillSession(controller);
            throw new TimeoutException($"the launch's broker/guardian outlived its execution by more than {ControllerPatience.TotalSeconds:0}s, and either can still write {Registry}. Killed now:\n{described}\n{bootstrap}");
        }

        /// <summary>The broker and guardian the receipt names — none when no bootstrap ever committed one, as for a slot refused before any launch.</summary>
        private NativeProcessIdentity[] LaunchControllers()
        {
            try { var receipt = ReadReceipt(); return new[] { receipt.Broker, receipt.Guardian }.OfType<NativeProcessIdentity>().ToArray(); }
            catch (Exception absent) when (absent is FileNotFoundException or DirectoryNotFoundException) { return []; }
        }

        /// <summary>The last resort, not the fix: with the controllers gone nothing should still write here, so a delete that keeps failing names what is still inside instead of retrying without end.</summary>
        private static async Task DeleteAsync(string directory)
        {
            var watch = Stopwatch.StartNew();
            while (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) when (watch.Elapsed < DeletePatience) { await Task.Delay(50); }
                catch (IOException error) { throw new IOException($"{directory} was still being written {DeletePatience.TotalSeconds:0}s after every launch process was gone; it holds [{DescribeEntries(directory)}]. Find the writer with `ps -ef | grep codespace-runner-host` and `lsof +D {directory}`", error); }
            }
        }

        private static string DescribeEntries(string directory)
        {
            try { return string.Join(", ", Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Select(entry => Path.GetRelativePath(directory, entry))); }
            catch (IOException error) { return "unlistable: " + error.GetType().Name; }
        }
    }
}
