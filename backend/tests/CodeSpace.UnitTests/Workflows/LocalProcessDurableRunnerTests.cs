using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
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

        // "$@" is the command, possibly wrapped by prlimit (resource caps, outermost) and/or bwrap (confinement),
        // each terminated by `--`. The REAL command is invariably the trailing tokens, however many wrappers precede.
        var afterDollarZero = info.ArgumentList.Skip(c + 3).ToList();
        // The real command is always the trailing tokens, after any prlimit/bwrap wrappers.
        afterDollarZero.TakeLast(3).ShouldBe(new[] { "mycmd", "--flag", "value" });

        if (BubblewrapSandbox.Available is null && ProcessRlimits.Available is null)
            afterDollarZero.ShouldBe(new[] { "mycmd", "--flag", "value" });   // unconfined + uncapped: command runs directly
        else
            afterDollarZero[0].ShouldNotBe("mycmd", "when this host can sandbox (bwrap) or cap (prlimit), the command is wrapped, not run bare");

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

    [Fact]
    public void BuildDurableStartInfo_points_config_home_env_vars_at_one_fresh_isolated_dir_under_the_spool()
    {
        // A config-isolating harness (Claude Code / Codex) asks for its config-dir var to be redirected so a
        // shelled-out CLI never reads the operator's ~/.claude / ~/.codex. The runner points every requested
        // name at ONE fresh dir under the spool (created here, reaped with the spool dir).
        var spool = Path.Combine(Path.GetTempPath(), "codespace-cfg-" + Guid.NewGuid().ToString("N"));
        _spoolDirs.Add(spool);

        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd", ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR", "CODEX_HOME" } }, spool);

        var expected = Path.Combine(spool, "agent-home");
        info.Environment["CLAUDE_CONFIG_DIR"].ShouldBe(expected);
        info.Environment["CODEX_HOME"].ShouldBe(expected, "every requested name points at the single isolated home");
        Directory.Exists(expected).ShouldBeTrue("the home is created so the CLI initializes a clean config there");
    }

    [Fact]
    public void BuildDurableStartInfo_adds_no_config_home_when_a_harness_requests_none()
    {
        var info = LocalProcessRunner.BuildDurableStartInfo(new SandboxSpec { Command = "mycmd" }, "/tmp/spool-noconfig");

        info.Environment.ShouldNotContainKey("CLAUDE_CONFIG_DIR");
        Directory.Exists(Path.Combine("/tmp/spool-noconfig", "agent-home")).ShouldBeFalse("no isolation requested → no per-run config home");
    }

    [Fact]
    public async Task A_launched_process_sees_its_config_home_env_var_pointing_at_an_isolated_spool_dir()
    {
        if (OperatingSystem.IsWindows()) return;

        // End-to-end on a REAL process: the child a config-isolating harness drives must actually SEE its
        // config-dir var set to a fresh per-run dir under the spool — so Claude Code / Codex read only the
        // config we inject, never the operator's personal ~/.claude / ~/.codex.
        var handle = await LaunchAsync(new SandboxSpec
        {
            Command = "/bin/sh",
            Args = new[] { "-c", "printf '%s' \"$CLAUDE_CONFIG_DIR\"" },
            ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR" },
        });

        var (result, lines) = await AttachCollectAsync(handle);

        result.Status.ShouldBe(SandboxStatus.Success);
        var expected = Path.Combine(handle.SpoolDirectory, "agent-home");
        // The child read CLAUDE_CONFIG_DIR set to the isolated per-run home under the spool (not the operator's ~/.claude).
        lines.ShouldBe(new[] { expected });
        Directory.Exists(expected).ShouldBeTrue("the isolated config home exists for the CLI to initialize into");
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

    [Fact]
    public void Mcp_socket_path_constants_are_pinned()
    {
        // The executor's listener and the runner/proxy connect path agree on these literals — a rename silently breaks the link.
        LocalProcessRunner.McpSocketFile.ShouldBe("mcp.sock");

        // The usable AF_UNIX path maximum: 103 on macOS/BSD, 107 on Linux. The LOWER cap so the short-path fallback
        // fires on every host that would overflow either — a CRITICAL guard against Bind overflowing on this darwin
        // host (empirically .NET binds at length 103 and throws at 104 here).
        LocalProcessRunner.UnixSocketPathCap.ShouldBe(103);
    }

    [Fact]
    public void Mcp_socket_path_is_under_the_spool_dir_for_a_short_root()
    {
        var original = Environment.GetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar);
        Environment.SetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar, "/tmp/cs");

        try
        {
            var key = Guid.NewGuid().ToString("N");
            var path = LocalProcessRunner.McpSocketPathFor(key);

            path.ShouldBe(Path.Combine("/tmp/cs", key, "mcp.sock"));
            path.Length.ShouldBeLessThanOrEqualTo(LocalProcessRunner.UnixSocketPathCap);
        }
        finally { Environment.SetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar, original); }
    }

    [Fact]
    public void Mcp_socket_path_falls_back_to_a_short_unique_path_when_the_canonical_path_overflows_the_cap()
    {
        var original = Environment.GetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar);
        Environment.SetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar, "/" + new string('x', 120));

        try
        {
            var key = Guid.NewGuid().ToString("N");
            var path = LocalProcessRunner.McpSocketPathFor(key);

            path.Length.ShouldBeLessThanOrEqualTo(LocalProcessRunner.UnixSocketPathCap, customMessage: "an overflowing canonical path must fall back to a short path that fits the sun_path cap");
            path.ShouldContain(key, customMessage: "the fallback must stay unique per run via the FULL run key (matching the canonical path's uniqueness)");
        }
        finally { Environment.SetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar, original); }
    }

    [Fact]
    public void Mcp_socket_path_cap_admits_a_bindable_path_and_one_byte_over_overflows()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        // The off-by-one regression test: a path of EXACTLY the admitted cap MUST bind, and one byte over MUST throw.
        // Build the parent dir under temp, then pad the filename so the FULL path hits the exact target length.
        var parent = TempDir();
        var prefix = parent + Path.DirectorySeparatorChar;

        var atCap = prefix + new string('a', LocalProcessRunner.UnixSocketPathCap - prefix.Length);
        atCap.Length.ShouldBe(LocalProcessRunner.UnixSocketPathCap);

        using (var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            Should.NotThrow(() => s.Bind(new UnixDomainSocketEndPoint(atCap)), "a path of exactly UnixSocketPathCap must be bindable");
        }
        try { File.Delete(atCap); } catch { /* best-effort */ }

        var overCap = prefix + new string('a', LocalProcessRunner.UnixSocketPathCap + 1 - prefix.Length);
        overCap.Length.ShouldBe(LocalProcessRunner.UnixSocketPathCap + 1);

        using var over = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Should.Throw<Exception>(() =>
        {
            var ep = new UnixDomainSocketEndPoint(overCap);   // the ctor itself throws on macOS; Bind throws on others
            over.Bind(ep);
        }).ShouldBeAssignableTo<ArgumentException>("one byte over the cap must overflow the AF_UNIX sun_path");
    }

    [Fact]
    public async Task Mcp_socket_path_fallback_is_genuinely_bindable_and_round_trips_a_byte()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        // The fallback branch (sun_path overflow) must yield a path that BINDS, not merely a short string. Force the
        // fallback with a long spool root, then bind a real listener, connect a client, and round-trip one byte.
        var original = Environment.GetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar);
        Environment.SetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar, "/" + new string('x', 120));

        try
        {
            var key = Guid.NewGuid().ToString("N");
            var path = LocalProcessRunner.McpSocketPathFor(key);
            path.Length.ShouldBeLessThanOrEqualTo(LocalProcessRunner.UnixSocketPathCap, "the fallback must fit the sun_path cap");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            try
            {
                using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(backlog: 1);

                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await client.ConnectAsync(new UnixDomainSocketEndPoint(path));

                using var server = await listener.AcceptAsync();

                await client.SendAsync(new byte[] { 0x42 }, SocketFlags.None);
                var buf = new byte[1];
                var n = await server.ReceiveAsync(buf, SocketFlags.None);

                n.ShouldBe(1, "the fallback socket carried the byte");
                buf[0].ShouldBe((byte)0x42, "the byte round-tripped over the genuinely-bound fallback socket");
            }
            finally { try { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { /* best-effort */ } }
        }
        finally { Environment.SetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar, original); }
    }

    // ─── Bubblewrap confinement E2E (Linux + bwrap only) ─────────────────────────
    // 🟢 High fidelity (Rule 12): drives the REAL durable launch (setsid + sh supervisor + bwrap + spool + attach)
    // against a real bwrap, and asserts REAL OS confinement — the agent reads its workspace but CANNOT read an
    // operator secret planted outside the binds, and HOME is redirected into the per-run config-home. Skips on
    // macOS dev / no bwrap / a host that denies unprivileged userns (Rule 12.1) — there the runner is unconfined
    // by design and the dict-level argv build is covered by BubblewrapSandboxTests.

    [Fact]
    [Trait("Category", "Sandbox")]
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
    [Trait("Category", "Sandbox")]
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

    // ─── Resource caps (prlimit wrapper — Linux only, gated on ProcessRlimits.Available) ─────────
    // 🟢 High fidelity (Rule 12): real durable launch (supervisor + prlimit + bwrap + agent), caps read straight
    // from the kernel (/proc/self/limits) so dash's missing `ulimit -u` can never silently no-op them. Skips on
    // macOS dev / no prlimit (the unconfined trust mode); the pure prlimit argv build is covered by ProcessRlimitsTests.

    [Fact]
    [Trait("Category", "Sandbox")]
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
    [Trait("Category", "Sandbox")]
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
