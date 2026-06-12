using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The DURABLE half of <see cref="LocalProcessRunner"/> (<see cref="ISandboxDurableRunner"/>) against REAL
/// OS processes: launch under the /bin/sh spool supervisor, tail the spool, complete by the exit marker.
/// The defining behaviours are (a) cancelling the attach stops observing WITHOUT killing the process — the
/// hinge that lets a restarted backend recover the run — and (b) a fresh attach to an already-exited run
/// still recovers its full output from the spool. POSIX-only (the supervisor is /bin/sh), so each test
/// skips on Windows (Rule 12.1). Spool dirs are GUID-keyed + cleaned up (Rule 12.2/12.3).
/// </summary>
[Trait("Category", "Unit")]
public sealed class LocalProcessDurableRunnerTests : IDisposable
{
    private readonly LocalProcessRunner _runner = new();
    private readonly List<string> _spoolDirs = new();

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

    [Fact]
    public async Task Launches_a_supervised_process_and_records_a_handle()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.Print("hi"));

        handle.Kind.ShouldBe("local");
        handle.ProcessId.ShouldBeGreaterThan(0);
        Directory.Exists(handle.SpoolDirectory).ShouldBeTrue("the spool directory is created at launch");
        handle.Deadline.ShouldBeGreaterThan(DateTimeOffset.UtcNow, "the wall-clock deadline is in the future");
    }

    [Fact]
    public async Task Probe_treats_a_recycled_pid_as_gone_when_the_recorded_start_time_no_longer_matches()
    {
        if (OperatingSystem.IsWindows()) return;

        // The PID-reuse guard: across a restart the OS can hand our old pid to an unrelated process. A handle
        // bearing that pid but a start time that no longer matches the live process is NOT our run.
        var handle = await LaunchAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 30 });
        handle.ProcessStartTimeUtc.ShouldNotBeNull();

        (await _runner.ProbeAsync(handle, default)).State.ShouldBe(SandboxRunState.Running, "the live supervisor with its matching start time probes Running");

        var recycled = handle with { ProcessStartTimeUtc = handle.ProcessStartTimeUtc!.Value.AddMinutes(-30) };
        (await _runner.ProbeAsync(recycled, default)).State.ShouldBe(SandboxRunState.Gone, "the same pid with a mismatched recorded start time is a recycled pid, not our run");

        KillTree(handle.ProcessId);
    }

    [Fact]
    public async Task An_older_handle_without_a_recorded_start_time_still_probes_running()
    {
        if (OperatingSystem.IsWindows()) return;

        // Back-compat: a handle persisted before the PID-reuse guard existed has no start time → the guard is
        // skipped and liveness alone decides, so an in-flight run from an older backend is never wrongly abandoned.
        var handle = await LaunchAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 30 }) with { ProcessStartTimeUtc = null };

        (await _runner.ProbeAsync(handle, default)).State.ShouldBe(SandboxRunState.Running);

        KillTree(handle.ProcessId);
    }

    [Fact]
    public async Task Attach_streams_lines_in_order_then_completes_success_with_an_exit_marker()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.MultiLine("alpha", "beta", "gamma"));

        var (result, lines) = await AttachCollectAsync(handle);

        result.Status.ShouldBe(SandboxStatus.Success);
        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldBe("", "stdout is delivered live via the callback, not accumulated");
        lines.ShouldBe(new[] { "alpha", "beta", "gamma" });
        File.ReadAllText(Path.Combine(handle.SpoolDirectory, "exit")).Trim().ShouldBe("0", "the supervisor records the exit code in the marker");
    }

    [Fact]
    public async Task Nonzero_exit_completes_failed_with_the_code()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.PrintThenExit("partial", 2));

        var (result, lines) = await AttachCollectAsync(handle);

        result.Status.ShouldBe(SandboxStatus.Failed);
        result.ExitCode.ShouldBe(2);
        lines.ShouldContain("partial", "what the process printed before exiting is still observed");
    }

    [Fact]
    public async Task Stderr_is_captured_from_the_spool_with_no_stdout_lines()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.PrintToStderr("err-on-spool"));

        var (result, lines) = await AttachCollectAsync(handle);

        result.Stderr.ShouldContain("err-on-spool");
        lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Deadline_elapsing_terminates_the_process_and_reports_timed_out()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 1 });

        var (result, _) = await AttachCollectAsync(handle);

        result.Status.ShouldBe(SandboxStatus.TimedOut, "the observer enforces the handle's wall-clock deadline");
        result.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public async Task Cancelling_the_attach_stops_observing_WITHOUT_killing_the_process()
    {
        if (OperatingSystem.IsWindows()) return;

        // The durability hinge: a backend shutdown cancels the observer, but the supervised run must keep
        // going so a re-attach (after restart) can finish it. A cancel is NOT a kill.
        var handle = await LaunchAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 30 });

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(() => AttachCollectAsync(handle, cts.Token));

        File.Exists(Path.Combine(handle.SpoolDirectory, "exit")).ShouldBeFalse("the process has NOT exited — the cancel only stopped observing");
        ProcessIsAlive(handle.ProcessId).ShouldBeTrue("the supervised process survives the observer being torn down");

        KillTree(handle.ProcessId);   // cleanup — the test deliberately left it running
    }

    [Fact]
    public async Task Resuming_from_a_nonzero_StdoutOffset_emits_only_the_lines_after_it()
    {
        if (OperatingSystem.IsWindows()) return;

        // The re-attach foundation: a fresh observer resumes from the dead observer's checkpoint, so the lines
        // already emitted (and already in the append-only event log) are NOT replayed — no duplicate events.
        var handle = await LaunchAsync(ContractSpecs.MultiLine("one", "two", "three"));
        await WaitForExitMarkerAsync(handle);

        var resumed = handle with { StdoutOffset = "one\n".Length };   // 4 bytes — resume past the first line
        var (result, lines) = await AttachCollectAsync(resumed);

        result.Status.ShouldBe(SandboxStatus.Success);
        lines.ShouldBe(new[] { "two", "three" });   // only the lines after the checkpoint offset are re-emitted
    }

    [Fact]
    public async Task Attach_checkpoints_the_advancing_offset_as_it_emits()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.MultiLine("a", "b", "c"));

        var lines = new List<string>();
        var checkpoints = new List<long>();
        await _runner.AttachAsync(handle, (l, _) => { lines.Add(l.Trim()); return Task.CompletedTask; }, default,
            (offset, _) => { checkpoints.Add(offset); return Task.CompletedTask; });

        lines.ShouldBe(new[] { "a", "b", "c" });
        checkpoints.ShouldNotBeEmpty("the observer checkpoints the advancing offset so a re-attach can resume from it");
        checkpoints.ShouldBe(checkpoints.OrderBy(o => o).ToList(), "checkpoints only ever advance");
        checkpoints[^1].ShouldBe("a\nb\nc\n".Length, "the final checkpoint covers every whole line emitted");
    }

    [Fact]
    public async Task A_fresh_attach_to_an_already_exited_run_recovers_its_full_output()
    {
        if (OperatingSystem.IsWindows()) return;

        // Observation is decoupled from launch: this is the foundation the reconciler stands on — a run that
        // finished while no one was watching is still fully recoverable from its spool + exit marker.
        var handle = await LaunchAsync(ContractSpecs.MultiLine("one", "two", "three"));

        await WaitForExitMarkerAsync(handle);

        var (result, lines) = await AttachCollectAsync(handle);

        result.Status.ShouldBe(SandboxStatus.Success);
        lines.ShouldBe(new[] { "one", "two", "three" });   // attaching after the fact replays the whole spool from offset 0
    }

    [Fact]
    public async Task Probe_reports_exited_with_the_code_once_the_marker_is_present()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.PrintThenExit("x", 3));
        await WaitForExitMarkerAsync(handle);

        var probe = await _runner.ProbeAsync(handle, default);

        probe.State.ShouldBe(SandboxRunState.Exited);
        probe.ExitCode.ShouldBe(3);
    }

    [Fact]
    public async Task Probe_reports_running_while_the_supervised_process_is_alive()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 30 });

        var probe = await _runner.ProbeAsync(handle, default);

        probe.State.ShouldBe(SandboxRunState.Running);
        probe.ExitCode.ShouldBeNull();

        KillTree(handle.ProcessId);   // cleanup — still running
    }

    [Fact]
    public async Task Probe_reports_gone_when_the_process_died_without_recording_a_marker()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 30 });

        KillTree(handle.ProcessId);          // killed mid-run → it never writes an exit marker
        await Task.Delay(200);

        var probe = await _runner.ProbeAsync(handle, default);

        probe.State.ShouldBe(SandboxRunState.Gone);
    }

    [Fact]
    public void BuildDurableStartInfo_wraps_the_command_in_a_sh_supervisor_pointing_at_the_spool()
    {
        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd", Args = new[] { "--flag", "value" } }, "/tmp/spool-x");

        // On Linux the supervisor is launched under `setsid` (a new session, so it survives a group signal) with
        // /bin/sh as setsid's first arg; on macOS dev there's no setsid binary, so /bin/sh runs directly. Either
        // way the `-c <script> sh <command> <args...>` tail is identical — assert from the shared "-c" anchor.
        if (OperatingSystem.IsLinux())
        {
            info.FileName.ShouldBe("setsid");
            info.ArgumentList[0].ShouldBe("/bin/sh");
        }
        else
        {
            info.FileName.ShouldBe("/bin/sh");
        }

        var c = info.ArgumentList.IndexOf("-c");
        c.ShouldBeGreaterThanOrEqualTo(0, "the supervisor is invoked via sh -c");
        info.ArgumentList[c + 2].ShouldBe("sh");                       // $0 — the script reads the real command from "$@"
        info.ArgumentList[c + 3].ShouldBe("mycmd");
        info.ArgumentList[c + 4].ShouldBe("--flag");
        info.ArgumentList[c + 5].ShouldBe("value");
        info.Environment["CSP_OUT"].ShouldBe(Path.Combine("/tmp/spool-x", "out.log"));
        info.Environment["CSP_ERR"].ShouldBe(Path.Combine("/tmp/spool-x", "err.log"));
        info.Environment["CSP_EXIT"].ShouldBe(Path.Combine("/tmp/spool-x", "exit"));
        info.Environment["CSP_PID"].ShouldBe(Path.Combine("/tmp/spool-x", "pid"));
    }

    [Fact]
    public void BuildDurableStartInfo_keeps_the_spool_env_vars_through_a_scrub()
    {
        // The spool paths are added AFTER ApplyEnvironment, so a scrub's Clear() can't drop them — otherwise a
        // scrubbed run would have nowhere to write its output.
        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd" }, "/tmp/spool-y");

        info.Environment.ShouldContainKey("CSP_OUT");
        info.Environment.ShouldContainKey("CSP_ERR");
        info.Environment.ShouldContainKey("CSP_EXIT");
        info.Environment.ShouldContainKey("CSP_PID");
    }

    [Theory]
    [InlineData("alpha\nbeta\n", "alpha|beta")]       // trailing newline → the empty remainder is dropped
    [InlineData("alpha\nbeta", "alpha|beta")]         // trailing partial (no newline) → kept
    [InlineData("a\r\nb\r\n", "a|b")]                 // CRLF → the CR is trimmed
    [InlineData("solo", "solo")]
    [InlineData("", "")]
    public void SplitLines_splits_drops_trailing_empty_and_trims_cr(string text, string expectedJoined)
    {
        var expected = expectedJoined.Length == 0 ? Array.Empty<string>() : expectedJoined.Split('|');

        LocalProcessRunner.SplitLines(text).ShouldBe(expected);
    }

    [Fact]
    public void ReadNewLines_emits_whole_lines_holds_a_partial_then_drains_it()
    {
        var path = Path.Combine(TempDir(), "out.log");
        File.WriteAllText(path, "a\nb\npar");

        var (lines, offset) = LocalProcessRunner.ReadNewLines(path, 0, drainPartial: false);
        lines.ShouldBe(new[] { "a", "b" });
        offset.ShouldBe(4, "consumed up to the last newline (\"a\\nb\\n\"); the partial \"par\" is held back");

        var (none, held) = LocalProcessRunner.ReadNewLines(path, offset, drainPartial: false);
        none.ShouldBeEmpty("no further whole line yet");
        held.ShouldBe(offset);

        var (drained, end) = LocalProcessRunner.ReadNewLines(path, offset, drainPartial: true);
        drained.ShouldBe(new[] { "par" });   // the final drain emits the trailing partial
        end.ShouldBe(7);
    }

    [Theory]
    [InlineData("0", true, 0)]
    [InlineData("127", true, 127)]
    [InlineData("  42 ", true, 42)]   // surrounding whitespace tolerated
    [InlineData("abc", false, 0)]     // a non-numeric / mid-write marker is "not ready yet"
    public void TryReadExitCode_parses_a_present_numeric_marker(string contents, bool expectedFound, int expectedCode)
    {
        var path = Path.Combine(TempDir(), "exit");
        File.WriteAllText(path, contents);

        LocalProcessRunner.TryReadExitCode(path, out var code).ShouldBe(expectedFound);
        if (expectedFound) code.ShouldBe(expectedCode);
    }

    [Fact]
    public void TryReadExitCode_is_false_when_the_marker_is_absent() =>
        LocalProcessRunner.TryReadExitCode(Path.Combine(TempDir(), "no-such-exit"), out _).ShouldBeFalse();

    [Fact]
    public void Spool_root_defaults_under_temp_and_the_env_var_name_is_pinned()
    {
        LocalProcessRunner.SpoolRoot().ShouldContain("agent-runs");
        LocalProcessRunner.SpoolRoot().ShouldContain("codespace");

        // Renaming this breaks an air-gapped operator who pinned a durable spool volume via env — pin it (Rule 8).
        LocalProcessRunner.SpoolRootEnvVar.ShouldBe("CODESPACE_AGENT_RUN_SPOOL_DIR");
    }

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs-spool-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _spoolDirs.Add(dir);
        return dir;
    }

    private async Task WaitForExitMarkerAsync(SandboxHandle handle)
    {
        var marker = Path.Combine(handle.SpoolDirectory, "exit");
        for (var i = 0; i < 100 && !File.Exists(marker); i++) await Task.Delay(50);
        File.Exists(marker).ShouldBeTrue("the quick command should have finished + recorded its exit marker within ~5s");
    }

    private static bool ProcessIsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static void KillTree(int pid)
    {
        try { using var p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        foreach (var dir in _spoolDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
