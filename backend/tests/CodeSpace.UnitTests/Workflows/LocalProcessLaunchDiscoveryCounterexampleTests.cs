using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
using CodeSpace.Messages.Agents;
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

    private sealed class LaunchFixture : IAsyncDisposable
    {
        private readonly Dictionary<int, DateTime> _owned = new();
        private readonly HashSet<int> _children = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-launch-audit-" + Guid.NewGuid().ToString("N"));
        public string Key { get; } = "launch-audit-" + Guid.NewGuid().ToString("N");
        public string Spool => LocalProcessRunner.SpoolDirectoryFor(Key);
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
            if (Directory.Exists(Spool)) Directory.Delete(Spool, recursive: true);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
