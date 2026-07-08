using System.Diagnostics;
using System.Net.Sockets;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox;
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
[Collection("LocalProcessIdleWatchdog")]
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
    public async Task A_silent_durable_run_is_terminated_as_stalled_well_before_its_deadline()
    {
        // C3 stall watchdog on the DURABLE (real-run) path: no spool output for the idle window → Stalled, killed early,
        // not left to the far-off deadline. Idle 2s; deadline 30s.
        if (OperatingSystem.IsWindows()) return;

        var prior = Environment.GetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, "2");

            var handle = await LaunchAsync(ContractSpecs.Sleep(30) with { TimeoutSeconds = 30 });

            var (result, _) = await AttachCollectAsync(handle);

            result.Status.ShouldBe(SandboxStatus.Stalled, "no spool advance for the 2s idle window → stalled, not a 30s timeout");
            result.ExitCode.ShouldBe(-1);
        }
        finally { Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, prior); }
    }

    [Fact]
    public async Task A_durable_run_emitting_within_the_idle_window_is_not_stalled()
    {
        // The watchdog must not kill an active durable run: spool advances inside every idle window → runs to completion.
        if (OperatingSystem.IsWindows()) return;

        var prior = Environment.GetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, "2");

            var handle = await LaunchAsync(new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "for i in 1 2 3 4; do echo tick$i; sleep 0.3; done" }, TimeoutSeconds = 30 });

            var (result, lines) = await AttachCollectAsync(handle);

            result.Status.ShouldBe(SandboxStatus.Success, "spool advancing within the idle window is never falsely stalled");
            lines.ShouldContain("tick4");
        }
        finally { Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, prior); }
    }

    [Fact]
    public async Task A_durable_run_emitting_newline_less_progress_is_not_stalled()
    {
        // Regression for the review's major finding on the DURABLE path: the watchdog resets on spool BYTE growth, not
        // only on a delivered line — so a run writing a \r-style progress bar with NO newline keeps the file growing
        // and is alive, not falsely stalled. `printf` (no newline) grows out.log every 0.3s inside the 2s window.
        if (OperatingSystem.IsWindows()) return;

        var prior = Environment.GetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, "2");

            var handle = await LaunchAsync(new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "for i in 1 2 3 4 5 6 7 8 9 10; do printf 'tick'; sleep 0.3; done" }, TimeoutSeconds = 30 });

            var (result, _) = await AttachCollectAsync(handle);

            result.Status.ShouldBe(SandboxStatus.Success, "newline-less byte growth of the spool within the window is never falsely stalled");
        }
        finally { Environment.SetEnvironmentVariable(LocalProcessRunner.StdoutIdleTimeoutEnvVar, prior); }
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
    public void BuildDurableStartInfo_with_resource_caps_disabled_carries_no_prlimit_wrapper()
    {
        // The fix for the live-brain whole-loop false-red: a TRUSTED-fake lane sets MaxProcesses=0 + MaxFileSizeMb=0
        // (via CODESPACE_AGENT_MAX_PROCESSES / _MAX_FILE_MB) so ProcessRlimits.Wrap returns the command UNCHANGED — NO
        // prlimit wrapper. RLIMIT_NPROC is per-UID; on a plain unprivileged shared host it counts the runner's whole
        // process table, so under the supervisor's CONCURRENT multi-agent fan-out a 4096 cap starves the agents' fork()s
        // → signal-kills (Status=Failed) and fork-starved git captures (Succeeded with realPatches=0). Disabling the
        // caps removes that wrapper entirely; this pins that wiring so a future default can't silently re-arm it.
        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd", Args = new[] { "--flag", "value" }, MaxProcesses = 0, MaxFileSizeMb = 0 }, "/tmp/spool-caps");

        info.ArgumentList.Any(a => a.Contains("prlimit")).ShouldBeFalse(
            "with both resource caps disabled the durable command must carry NO prlimit wrapper (else a per-UID rlimit can signal-kill or fork-starve a trusted fake on a shared host)");

        // The real command still trails (bwrap may still wrap it when confinement is available; only prlimit is gone).
        info.ArgumentList.TakeLast(3).ShouldBe(new[] { "mycmd", "--flag", "value" });
    }

    [Fact]
    public void BuildDurableStartInfo_runs_the_chain_inside_the_cgroup_self_add_OUTERMOST_before_egress()
    {
        // B4 wiring: the cgroup self-add prefix must wrap the WHOLE chain — outermost, BEFORE the egress netns prefix —
        // so the cgroup.procs write happens on the host before entering the netns and the entire subtree is capped.
        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd", Args = new[] { "value" } }, "/tmp/spool-cg",
            egressExecPrefix: new[] { "EGRESS", "y" },
            cgroupExecPrefix: new[] { "CGSELF", "x" });

        var c = info.ArgumentList.IndexOf("-c");
        var afterDollarZero = info.ArgumentList.Skip(c + 3).ToList();   // after `-c <script> sh`

        afterDollarZero.Take(4).ShouldBe(new[] { "CGSELF", "x", "EGRESS", "y" }, "cgroup self-add is OUTERMOST, then the egress netns prefix");
        afterDollarZero.TakeLast(2).ShouldBe(new[] { "mycmd", "value" }, "the real command still trails the wrappers");
    }

    [Fact]
    public void BuildDurableStartInfo_with_no_cgroup_prefix_is_byte_identical_to_before_the_knob()
    {
        // Non-breaking: an absent/empty cgroup prefix (the default — no cap requested / no delegated root) produces the
        // EXACT same argv as a call that never passed the parameter.
        var spec = new SandboxSpec { Command = "mycmd", Args = new[] { "value" } };

        var before = LocalProcessRunner.BuildDurableStartInfo(spec, "/tmp/spool-bi");
        var withEmptyCgroup = LocalProcessRunner.BuildDurableStartInfo(spec, "/tmp/spool-bi", null, Array.Empty<string>());

        withEmptyCgroup.ArgumentList.ShouldBe(before.ArgumentList);
    }

    [Fact]
    public void CgroupRoot_env_var_constant_is_pinned()
    {
        // Rule 8: renaming this breaks an operator who pinned a delegated cgroup root via env. Hard-pin the literal.
        CodeSpace.Core.Services.Agents.Sandbox.Isolation.CgroupResourceLimit.CgroupRootEnvVar.ShouldBe("CODESPACE_AGENT_CGROUP_ROOT");
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
    public void BuildDurableStartInfo_keeps_the_non_interactive_defaults_through_the_scrub()
    {
        // C1 is injected at the SHARED ApplyEnvironment choke point, so the durable/bwrap path must carry the
        // non-interactive defaults too (the bwrap argv has no --clearenv → the confined child inherits this env).
        // Guards the durable half of the "one choke point covers both paths" claim against a future reorder of the
        // post-Clear() env assembly — mirrors the spool-env precedent above.
        var info = LocalProcessRunner.BuildDurableStartInfo(new SandboxSpec { Command = "mycmd" }, "/tmp/spool-noninteractive");

        foreach (var (key, value) in NonInteractiveEnv.Defaults)
            info.Environment[key].ShouldBe(value, $"{key} must survive the durable assembly so the bwrap child auto-defaults a prompt");
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

    // ─── MCP wiring: the runner writes the declaration 0600 into config-home + binds the socket (Slice 4) ────

    // The harness renders the Content (FIX 3 — runner writes dumb bytes); here we bake a representative .mcp.json so the
    // write/bind tests have realistic content carrying the socket + token.
    private static McpServerWiring Wiring(string socketPath) => new()
    {
        RelativeFileName = ".mcp.json",
        Content = McpDeclarationWriter.RenderClaudeJson(new McpDeclarationContext { ProxyCommand = "/abs/codespace-mcp", SocketPath = socketPath, Token = "tok-xyz", ServerName = "codespace" }),
        SocketPath = socketPath,
    };

    [Fact]
    public void WriteMcpDeclaration_writes_the_rendered_server_into_the_config_home()
    {
        var configHome = TempDir();

        LocalProcessRunner.WriteMcpDeclaration(Wiring("/tmp/cs/mcp.sock"), configHome);

        var path = Path.Combine(configHome, ".mcp.json");
        File.Exists(path).ShouldBeTrue("the declaration is written at its config-home-relative path");

        var json = File.ReadAllText(path);
        json.ShouldContain("codespace-mcp");
        json.ShouldContain("/tmp/cs/mcp.sock");
        json.ShouldContain("tok-xyz", customMessage: "the run token rides the declaration so the proxy authenticates");
    }

    [Fact]
    public void WriteMcpDeclaration_writes_the_declaration_owner_only_0600()
    {
        if (OperatingSystem.IsWindows()) return;   // unix file modes don't apply

        var configHome = TempDir();

        LocalProcessRunner.WriteMcpDeclaration(Wiring("/tmp/cs/mcp.sock"), configHome);

        // The token lives in this file, so it must NOT be group/other-readable.
        var mode = File.GetUnixFileMode(Path.Combine(configHome, ".mcp.json"));
        mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite, customMessage: "the token-bearing declaration must be 0600");
    }

    [Fact]
    public void WriteMcpDeclaration_is_a_no_op_when_there_is_no_wiring_or_no_config_home()
    {
        var configHome = TempDir();

        // No wiring → nothing written (a run without the tool fabric).
        LocalProcessRunner.WriteMcpDeclaration(null, configHome);
        File.Exists(Path.Combine(configHome, ".mcp.json")).ShouldBeFalse("no wiring → no declaration");

        // No config-home → nowhere harness-isolated to put it → no-op (must not throw).
        Should.NotThrow(() => LocalProcessRunner.WriteMcpDeclaration(Wiring("/tmp/cs/mcp.sock"), null));
    }

    [Fact]
    public void WriteConfigHomeFiles_writes_each_file_at_its_relative_path()
    {
        var configHome = TempDir();

        LocalProcessRunner.WriteConfigHomeFiles(new[]
        {
            new ConfigHomeFile { RelativePath = "skills/tdd/SKILL.md", Content = "tdd body" },
            new ConfigHomeFile { RelativePath = "skills/debug/SKILL.md", Content = "debug body" },
        }, configHome);

        File.ReadAllText(Path.Combine(configHome, "skills", "tdd", "SKILL.md")).ShouldBe("tdd body");
        File.ReadAllText(Path.Combine(configHome, "skills", "debug", "SKILL.md")).ShouldBe("debug body");
    }

    [Fact]
    public void WriteConfigHomeFiles_skips_a_path_that_escapes_the_config_home()
    {
        var configHome = TempDir();

        LocalProcessRunner.WriteConfigHomeFiles(new[] { new ConfigHomeFile { RelativePath = "../escape/SKILL.md", Content = "x" } }, configHome);

        File.Exists(Path.Combine(Path.GetDirectoryName(configHome)!, "escape", "SKILL.md"))
            .ShouldBeFalse("a config-home-escaping relative path is skipped — the runner is the last gate before a write");
    }

    [Fact]
    public void WriteConfigHomeFiles_is_a_no_op_without_files_or_config_home()
    {
        var configHome = TempDir();

        Should.NotThrow(() => LocalProcessRunner.WriteConfigHomeFiles(Array.Empty<ConfigHomeFile>(), configHome));
        Should.NotThrow(() => LocalProcessRunner.WriteConfigHomeFiles(new[] { new ConfigHomeFile { RelativePath = "skills/x/SKILL.md", Content = "y" } }, null));
    }

    [Fact]
    public void BuildDurableStartInfo_writes_the_mcp_declaration_into_the_config_home_when_wired()
    {
        var spool = Path.Combine(Path.GetTempPath(), "codespace-mcp-decl-" + Guid.NewGuid().ToString("N"));
        _spoolDirs.Add(spool);

        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "claude", ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR" }, Mcp = Wiring("/tmp/cs/mcp.sock") }, spool);

        // The declaration lands in the SAME per-run home the config-dir env var points at.
        var home = info.Environment["CLAUDE_CONFIG_DIR"];
        File.Exists(Path.Combine(home, ".mcp.json")).ShouldBeTrue("the runner writes the declaration into the per-run config-home before launch");
    }

    [Fact]
    public void BuildDurableStartInfo_with_no_mcp_wiring_is_byte_identical_to_a_run_without_the_tool_fabric()
    {
        // Flag-OFF byte-identical guarantee: a spec with Mcp=null must produce the EXACT same argv + spool env as the
        // SAME spec built again — no socket bind, no proxy ro-bind, no declaration write. Two builds of the identical
        // Mcp-less spec must match token-for-token (the only source of divergence would be MCP wiring leaking in).
        var spool = Path.Combine(Path.GetTempPath(), "codespace-mcp-off-" + Guid.NewGuid().ToString("N"));
        _spoolDirs.Add(spool);

        SandboxSpec Spec() => new() { Command = "claude", WorkingDirectory = spool, ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR" } };

        var a = LocalProcessRunner.BuildDurableStartInfo(Spec(), spool);
        var b = LocalProcessRunner.BuildDurableStartInfo(Spec(), spool);

        a.ArgumentList.ToList().ShouldBe(b.ArgumentList.ToList(), "Mcp=null must add no socket bind / ro-bind — byte-identical argv");

        // And concretely: nothing references the dedicated socket subdir or a proxy bind.
        a.ArgumentList.ShouldNotContain(Path.Combine(spool, "mcp"), customMessage: "Mcp=null must not bind the socket dir");
        File.Exists(Path.Combine(spool, "agent-home", ".mcp.json")).ShouldBeFalse("Mcp=null must write no declaration");
    }

    // ─── B3.2b: the filtered-egress netns prefix wraps the whole supervisor chain ────────────────────────────────

    [Fact]
    public void BuildDurableStartInfo_runs_the_whole_chain_inside_the_egress_netns_prefix_when_one_is_set()
    {
        // When the durable launch sets up a filtered-egress netns it passes the `ip netns exec <ns>` prefix; the
        // supervisor must run the WHOLE chain (prlimit → bwrap → agent) behind it, so its only egress is the netns filter.
        var prefix = new[] { "ip", "netns", "exec", "cs-egr-deadbeef" };

        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd", Args = new[] { "--flag", "value" } }, "/tmp/spool-egr", prefix);

        var c = info.ArgumentList.IndexOf("-c");
        info.ArgumentList[c + 2].ShouldBe("sh");                       // $0 — the script reads the real command from "$@"
        var afterDollarZero = info.ArgumentList.Skip(c + 3).ToList();

        afterDollarZero.Take(4).ShouldBe(prefix, "the netns prefix is OUTERMOST — ahead of any prlimit/bwrap wrapper");
        afterDollarZero.TakeLast(3).ShouldBe(new[] { "mycmd", "--flag", "value" }, "the real command still trails every wrapper");

        if (BubblewrapSandbox.Available is not null)
            info.ArgumentList.ShouldNotContain("--unshare-net", customMessage: "inside a filtered netns bwrap SHARES it (inherits the allowlist filter) — it must never --unshare-net the namespace it was placed in");
    }

    [Fact]
    public void BuildDurableStartInfo_without_an_egress_prefix_preserves_today_network_behaviour()
    {
        // No prefix (null) ⇒ no `ip netns exec`, and a network-OFF run still --unshare-nets under bwrap — today's
        // behaviour preserved byte-for-byte. The netns wiring is inert until an enforceable allowlist sets a prefix.
        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "mycmd", AllowNetwork = false }, "/tmp/spool-noegr");

        info.ArgumentList.ShouldNotContain("netns", customMessage: "no enforceable allowlist ⇒ no netns prefix");

        if (BubblewrapSandbox.Available is not null)
            info.ArgumentList.ShouldContain("--unshare-net", customMessage: "network-off without a netns still severs egress via --unshare-net");
    }

    [Fact]
    public async Task ResolveAllowedIpsBounded_propagates_a_real_cancellation_rather_than_masking_it_as_a_timeout()
    {
        // The bounded resolver converts a TIMEOUT (a black-holed DNS) into a fail-closed setup abort, but a GENUINE run
        // cancellation must propagate as OperationCanceledException (handled as transient by the executor), never be
        // reclassified. A pre-cancelled token + a name (forces the DNS path) exercises the distinction deterministically.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => LocalProcessRunner.ResolveAllowedIpsBoundedAsync(new[] { "example.com" }, cts.Token));
    }

    [Fact]
    public void AppendChildCommand_binds_a_dedicated_socket_dir_NOT_the_spool_dir_so_no_spool_artifacts_leak()
    {
        if (BubblewrapSandbox.Available is null) return;   // bwrap-only: the writable --bind only exists under confinement

        var spool = Path.Combine(Path.GetTempPath(), "codespace-mcp-bind-" + Guid.NewGuid().ToString("N"));
        _spoolDirs.Add(spool);

        // The socket lives in the DEDICATED <spool>/mcp/ subdir (FIX 1) — its parent is that subdir, never the spool dir
        // (which holds out.log/err.log/exit/pid the agent must not read or forge — design §3b / Attack 4).
        var socketPath = Path.Combine(spool, "mcp", "mcp.sock");

        var info = LocalProcessRunner.BuildDurableStartInfo(
            new SandboxSpec { Command = "claude", WorkingDirectory = spool, ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR" }, Mcp = Wiring(socketPath) }, spool);

        var args = info.ArgumentList.ToList();
        var binds = args.Select((a, i) => (a, i)).Where(t => t.a == "--bind").Select(t => args[t.i + 1]).ToList();

        var boundSocketDir = Path.GetDirectoryName(socketPath)!;

        // (a) the socket's dir IS bound writable (so the proxy connects), but it is NOT the spool dir.
        binds.ShouldContain(boundSocketDir, customMessage: "the dedicated MCP socket dir must be bound writable so the proxy can reach it");
        binds.ShouldNotContain(spool, customMessage: "the spool dir itself must NOT be a writable bind — that would expose out.log/err.log/exit/pid to the agent");

        // (b) none of the spool artifacts live under the bound dir.
        foreach (var artifact in new[] { "out.log", "err.log", "exit", "pid" })
            File.Exists(Path.Combine(boundSocketDir, artifact)).ShouldBeFalse($"the bound socket dir must not contain the spool artifact {artifact}");
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

        // The dedicated socket-only subdir (FIX 1): a rename re-exposes the spool artifacts to the bwrap bind.
        LocalProcessRunner.McpSocketDir.ShouldBe("mcp");

        // The proxy-path override (Rule 8): a rename breaks an operator who pinned a custom codespace-mcp path.
        LocalProcessRunner.McpProxyPathEnvVar.ShouldBe("CODESPACE_MCP_PROXY_PATH");

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

            // FIX 1: the socket lives in the DEDICATED <spool>/mcp/ subdir, not directly in the spool dir.
            path.ShouldBe(Path.Combine("/tmp/cs", key, "mcp", "mcp.sock"));
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

        // A path well over BOTH platform sun_path ceilings (104 macOS / 108 Linux) MUST be rejected. UnixSocketPathCap+1
        // is NOT a portable overflow probe: it's the macOS usable max + 1, but Linux binds happily up to 107 — so use a
        // generous margin that overflows on every host. The cross-platform guard against the value drifting up is the
        // UnixSocketPathCap.ShouldBe(103) pin; this bind check proves the host actually rejects an over-length path.
        const int clearlyOverAnyCap = 130;

        var overCap = prefix + new string('a', clearlyOverAnyCap - prefix.Length);
        overCap.Length.ShouldBe(clearlyOverAnyCap);

        using var over = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Should.Throw<Exception>(() =>
        {
            var ep = new UnixDomainSocketEndPoint(overCap);   // the UDS endpoint ctor (or Bind) rejects an over-length sun_path
            over.Bind(ep);
        }).ShouldBeAssignableTo<ArgumentException>("a path well over the AF_UNIX sun_path cap must overflow on every host");
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
