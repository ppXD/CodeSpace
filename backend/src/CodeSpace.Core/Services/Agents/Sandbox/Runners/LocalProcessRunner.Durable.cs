using System.Diagnostics;
using System.Text;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox.Runners;

/// <summary>
/// The DURABLE-runner half of <see cref="LocalProcessRunner"/> (<see cref="ISandboxDurableRunner"/>): launch
/// the command under a <c>/bin/sh</c> supervisor that redirects its output to on-disk spool files and records
/// an exit-code marker, then observe the run by TAILING that spool. Decoupling the run's output from a parent
/// pipe is what lets a restarted backend recover the run from its persisted <see cref="SandboxHandle"/>
/// instead of losing it. On Linux the supervisor is launched under <c>setsid</c> so it leads its own session
/// and outlives a graceful-shutdown signal aimed at the API's process group — so the run keeps going for the
/// reconciler to recover/re-attach. The supervisor self-reports its pid via a pid file because <c>setsid</c>
/// may exec the shell in place or fork it (depending on whether setsid is a process-group leader), so the
/// launched process's own id isn't a reliable handle either way. POSIX-only (needs <c>/bin/sh</c>); a
/// non-POSIX host falls back to the streaming path.
/// </summary>
public sealed partial class LocalProcessRunner
{
    /// <summary>
    /// Operator override for the spool root (where each run's <c>out.log</c> / <c>err.log</c> / <c>exit</c>
    /// marker live). Default: a <c>codespace/agent-runs</c> dir under the system temp dir. Pinned by a test
    /// (Rule 8) — an air-gapped operator may need the spool on a specific durable volume.
    /// </summary>
    public const string SpoolRootEnvVar = "CODESPACE_AGENT_RUN_SPOOL_DIR";

    private const string StdoutFile = "out.log";
    private const string StderrFile = "err.log";
    private const string ExitMarkerFile = "exit";
    private const string PidFile = "pid";

    /// <summary>The per-run MCP listener socket file. Single source of truth so the executor's listener and the harness/proxy's connect path agree by construction on the same path.</summary>
    internal const string McpSocketFile = "mcp.sock";

    /// <summary>A DEDICATED socket-only subdir under the spool dir (<c>&lt;spool&gt;/mcp/</c>) that holds ONLY the socket. The bwrap bind binds THIS dir, never the spool dir itself — so the agent never sees the spool's <c>out.log</c> / <c>err.log</c> / <c>exit</c> / <c>pid</c> artifacts (it could otherwise read its own transcript or forge the <c>exit</c> marker — design §3b / Attack 4).</summary>
    internal const string McpSocketDir = "mcp";

    /// <summary>The usable <c>AF_UNIX</c> path maximum — 103 on macOS/BSD, 107 on Linux; use the LOWER so the short-path fallback fires on every host that would overflow either. Pinned by a test: a spool path longer than this would overflow <c>Bind</c> (empirically, .NET's <c>UnixDomainSocketEndPoint</c> binds at length 103 and throws at 104 on macOS), so <see cref="McpSocketPathFor"/> falls back to a short temp path.</summary>
    internal const int UnixSocketPathCap = 103;

    /// <summary>Per-run isolated config home (under the spool dir) that <see cref="SandboxSpec.ConfigHomeEnvVars"/> point at — keeps a shelled-out CLI off the operator's personal dotfiles. Reaped with the spool dir.</summary>
    private const string AgentConfigHomeDir = "agent-home";

    /// <summary>
    /// Operator override for the <c>codespace-mcp</c> proxy binary's ABSOLUTE path (e.g. an air-gapped mirror, a
    /// self-contained publish elsewhere). Default: <c>codespace-mcp</c> next to the running assembly
    /// (<see cref="AppContext.BaseDirectory"/>). Pinned by a test (Rule 8) — renaming it silently breaks an operator who
    /// pinned a custom proxy path.
    /// </summary>
    public const string McpProxyPathEnvVar = "CODESPACE_MCP_PROXY_PATH";

    /// <summary>The published <c>codespace-mcp</c> binary file name (its <c>AssemblyName</c>) used to build the default path under <see cref="AppContext.BaseDirectory"/>.</summary>
    private const string McpProxyFile = "codespace-mcp";

    /// <summary>Tail cadence — how often the observer re-reads the spool for new lines / checks the exit marker.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Per-poll read cap so a burst can't allocate unbounded; the next poll continues from the new offset.</summary>
    private const int MaxReadChunk = 8 * 1024 * 1024;

    /// <summary>How long LaunchAsync waits for the supervisor to self-report its pid before treating the launch as failed.</summary>
    private static readonly TimeSpan PidFileWait = TimeSpan.FromSeconds(2);

    /// <summary>Poll cadence while waiting for the supervisor's pid file to appear at launch.</summary>
    private static readonly TimeSpan PidPollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>PID-reuse guard tolerance: a live process whose start time is within this many seconds of the recorded one is "the same supervisor"; beyond it, the pid was recycled.</summary>
    private const int StartTimeToleranceSeconds = 2;

    /// <summary>
    /// The supervisor script: FIRST record the supervisor's own pid (<c>$$</c>) to the pid file — read back in
    /// LaunchAsync because under <c>setsid</c> the launched process's own id isn't a reliable handle (setsid
    /// either execs the shell in place or forks it). Then run the command (the positional <c>"$@"</c>) with
    /// stdout→out.log, stderr→err.log, and finally write the exit code to the marker. The marker is written
    /// AFTER the command and BEFORE the shell exits, so "marker present" reliably means "the command finished
    /// with this code" and "shell gone with no marker" means it was killed before recording one.
    /// </summary>
    private const string SupervisorScript = "printf '%s' \"$$\" >\"$CSP_PID\"; \"$@\" >\"$CSP_OUT\" 2>\"$CSP_ERR\"; printf '%s' \"$?\" >\"$CSP_EXIT\"";

    public async Task<SandboxHandle> LaunchAsync(SandboxSpec spec, string spoolKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var spoolDir = SpoolDirectoryFor(spoolKey);
        Directory.CreateDirectory(spoolDir);

        using var process = new Process { StartInfo = BuildDurableStartInfo(spec, spoolDir) };
        process.Start();

        // The launched process may be `setsid` (Linux); its own id isn't a reliable handle for the supervisor
        // (setsid execs the shell in place or forks it). Read the pid the supervisor self-reported, then capture
        // that process's start time as a PID-reuse guard for later probes.
        var supervisorPid = await ResolveSupervisorPidAsync(spoolDir, process, cancellationToken).ConfigureAwait(false);

        return new SandboxHandle
        {
            Kind = LocalKind,
            ProcessId = supervisorPid,
            ProcessStartTimeUtc = TryReadStartTimeUtc(supervisorPid),
            SpoolDirectory = spoolDir,
            Deadline = DateTimeOffset.UtcNow.AddSeconds(spec.TimeoutSeconds),
        };
    }

    /// <summary>
    /// Resolve the supervisor's real pid: it writes <c>$$</c> to the pid file as its first action, so poll
    /// briefly for that (under <c>setsid</c> the launched process's own id isn't a reliable handle). Fall back
    /// to the launched process's own id only when it's still our shell (the non-detached macOS path); a
    /// detached launch that never reported a pid is a genuine launch failure and throws so the run lands a
    /// clean Failed rather than tracking the wrong pid.
    /// </summary>
    private static async Task<int> ResolveSupervisorPidAsync(string spoolDir, Process launched, CancellationToken ct)
    {
        var pidPath = Path.Combine(spoolDir, PidFile);
        var deadline = DateTimeOffset.UtcNow + PidFileWait;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (TryReadPid(pidPath, out var pid)) return pid;

            await Task.Delay(PidPollInterval, ct).ConfigureAwait(false);
        }

        if (TryReadPid(pidPath, out var late)) return late;

        if (!OperatingSystem.IsLinux() && !launched.HasExited) return launched.Id;

        throw new InvalidOperationException($"Durable launch supervisor did not report its pid at '{pidPath}' within {PidFileWait.TotalSeconds:0}s.");
    }

    /// <summary>Read the supervisor's pid from its pid file: present, parseable, and positive. A missing / mid-write file returns false so the caller keeps polling.</summary>
    private static bool TryReadPid(string pidPath, out int pid)
    {
        pid = 0;

        try { return File.Exists(pidPath) && int.TryParse(File.ReadAllText(pidPath).Trim(), out pid) && pid > 0; }
        catch { return false; }
    }

    public async Task<SandboxResult> AttachAsync(SandboxHandle handle, Func<string, CancellationToken, Task> onStdoutLine, CancellationToken cancellationToken, Func<long, CancellationToken, Task>? onCheckpoint = null)
    {
        var stdoutPath = Path.Combine(handle.SpoolDirectory, StdoutFile);
        var stderrPath = Path.Combine(handle.SpoolDirectory, StderrFile);
        var exitPath = Path.Combine(handle.SpoolDirectory, ExitMarkerFile);

        // Resume from the handle's checkpoint (0 on a first attach / an older handle) so a re-attach after a
        // restart picks up where the dead observer stopped instead of re-emitting the whole spool.
        var offset = handle.StdoutOffset;

        // C3 stall watchdog (opt-in): the wall-clock of the LAST observed output. No new output for the idle window
        // ⇒ the run is stalled (e.g. blocked at a nested interactive prompt) ⇒ terminate early as Stalled. Re-attach
        // restarts this clock (a fresh observation), which is correct — it never fires on a run that is still emitting.
        // "Output" is BYTE growth of either spool file, NOT completed-line delivery — so a run streaming a newline-less
        // progress bar (\r updates) or a single slow long line is alive, not falsely stalled.
        var idleTimeout = IdleTimeout();
        var lastAdvance = DateTimeOffset.UtcNow;
        var lastByteLength = SafeFileLength(stdoutPath) + SafeFileLength(stderrPath);

        while (true)
        {
            var advanced = await EmitNewLinesAsync(stdoutPath, offset, onStdoutLine, drainPartial: false, cancellationToken).ConfigureAwait(false);

            // Checkpoint AFTER the batch's lines were delivered, only when it advanced — so the persisted offset
            // never runs ahead of the events (a re-attach at worst re-emits the last batch, never loses lines).
            if (advanced != offset && onCheckpoint is not null)
                await onCheckpoint(advanced, cancellationToken).ConfigureAwait(false);

            offset = advanced;

            // Reset the idle clock on ANY byte growth (incl. stderr-only or a not-yet-complete line), not only a
            // delivered line — the watchdog's signal is true silence, and an actively-emitting run is never silent.
            var byteLength = SafeFileLength(stdoutPath) + SafeFileLength(stderrPath);
            if (byteLength > lastByteLength) lastAdvance = DateTimeOffset.UtcNow;
            lastByteLength = byteLength;

            if (TryReadExitCode(exitPath, out var code))
                return await CompleteFromSpoolAsync(handle, offset, code, onStdoutLine, cancellationToken).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow >= handle.Deadline)
                return await TimeoutAsync(handle, offset, onStdoutLine, cancellationToken).ConfigureAwait(false);

            if (idleTimeout is { } idle && DateTimeOffset.UtcNow - lastAdvance >= idle)
                return await StalledAsync(handle, offset, onStdoutLine, cancellationToken).ConfigureAwait(false);

            if (!IsProcessAlive(handle.ProcessId, handle.ProcessStartTimeUtc))
                return await VanishedAsync(handle, offset, onStdoutLine, cancellationToken).ConfigureAwait(false);

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task TerminateAsync(SandboxHandle handle, CancellationToken cancellationToken)
    {
        // The explicit kill (NOT the cancel-stops-observing path): reuse the same start-time-guarded tree-kill the
        // timeout path uses, so a recycled pid is never killed and an already-exited run is a quiet no-op.
        KillByIdQuietly(handle.ProcessId, handle.ProcessStartTimeUtc);
        return Task.CompletedTask;
    }

    public Task<SandboxProbe> ProbeAsync(SandboxHandle handle, CancellationToken cancellationToken)
    {
        // Marker first: it's written BEFORE the supervisor exits, so its presence authoritatively means "finished".
        if (TryReadExitCode(Path.Combine(handle.SpoolDirectory, ExitMarkerFile), out var code))
            return Task.FromResult(new SandboxProbe { State = SandboxRunState.Exited, ExitCode = code });

        var state = IsProcessAlive(handle.ProcessId, handle.ProcessStartTimeUtc) ? SandboxRunState.Running : SandboxRunState.Gone;

        return Task.FromResult(new SandboxProbe { State = state });
    }

    /// <summary>Final drain (incl. a trailing partial line) + buffered stderr, mapped to Success/Failed by the exit code.</summary>
    private async Task<SandboxResult> CompleteFromSpoolAsync(SandboxHandle handle, long offset, int exitCode, Func<string, CancellationToken, Task> onLine, CancellationToken ct)
    {
        await EmitNewLinesAsync(Path.Combine(handle.SpoolDirectory, StdoutFile), offset, onLine, drainPartial: true, ct).ConfigureAwait(false);

        var stderr = await ReadAllSafeAsync(Path.Combine(handle.SpoolDirectory, StderrFile)).ConfigureAwait(false);

        return new SandboxResult { Status = exitCode == 0 ? SandboxStatus.Success : SandboxStatus.Failed, ExitCode = exitCode, Stdout = "", Stderr = stderr };
    }

    /// <summary>Deadline elapsed: terminate the process tree, drain what landed, return TimedOut.</summary>
    private async Task<SandboxResult> TimeoutAsync(SandboxHandle handle, long offset, Func<string, CancellationToken, Task> onLine, CancellationToken ct)
    {
        KillByIdQuietly(handle.ProcessId, handle.ProcessStartTimeUtc);

        await EmitNewLinesAsync(Path.Combine(handle.SpoolDirectory, StdoutFile), offset, onLine, drainPartial: true, ct).ConfigureAwait(false);

        var stderr = await ReadAllSafeAsync(Path.Combine(handle.SpoolDirectory, StderrFile)).ConfigureAwait(false);

        return new SandboxResult { Status = SandboxStatus.TimedOut, ExitCode = -1, Stdout = "", Stderr = stderr };
    }

    /// <summary>C3 stall watchdog tripped (no output for the idle window): terminate the process tree, drain what landed, return Stalled.</summary>
    private async Task<SandboxResult> StalledAsync(SandboxHandle handle, long offset, Func<string, CancellationToken, Task> onLine, CancellationToken ct)
    {
        KillByIdQuietly(handle.ProcessId, handle.ProcessStartTimeUtc);

        await EmitNewLinesAsync(Path.Combine(handle.SpoolDirectory, StdoutFile), offset, onLine, drainPartial: true, ct).ConfigureAwait(false);

        var stderr = await ReadAllSafeAsync(Path.Combine(handle.SpoolDirectory, StderrFile)).ConfigureAwait(false);

        return new SandboxResult { Status = SandboxStatus.Stalled, ExitCode = -1, Stdout = "", Stderr = stderr };
    }

    /// <summary>Supervisor disappeared: it writes the marker before exiting, so re-check after a beat — a true "gone with no marker" was a kill.</summary>
    private async Task<SandboxResult> VanishedAsync(SandboxHandle handle, long offset, Func<string, CancellationToken, Task> onLine, CancellationToken ct)
    {
        await Task.Delay(PollInterval, ct).ConfigureAwait(false);

        if (TryReadExitCode(Path.Combine(handle.SpoolDirectory, ExitMarkerFile), out var code))
            return await CompleteFromSpoolAsync(handle, offset, code, onLine, ct).ConfigureAwait(false);

        await EmitNewLinesAsync(Path.Combine(handle.SpoolDirectory, StdoutFile), offset, onLine, drainPartial: true, ct).ConfigureAwait(false);

        var stderr = await ReadAllSafeAsync(Path.Combine(handle.SpoolDirectory, StderrFile)).ConfigureAwait(false);

        return new SandboxResult { Status = SandboxStatus.Failed, ExitCode = -1, Stdout = "", Stderr = stderr };
    }

    /// <summary>Read the complete lines that landed since <paramref name="offset"/>, emit each, and return the advanced offset.</summary>
    private static async Task<long> EmitNewLinesAsync(string path, long offset, Func<string, CancellationToken, Task> onLine, bool drainPartial, CancellationToken ct)
    {
        var (lines, newOffset) = ReadNewLines(path, offset, drainPartial);

        foreach (var line in lines) await onLine(line, ct).ConfigureAwait(false);

        return newOffset;
    }

    internal static ProcessStartInfo BuildDurableStartInfo(SandboxSpec spec, string spoolDir)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty,
        };

        // On Linux, run the supervisor under `setsid` so it LEADS A NEW SESSION: a graceful-shutdown signal
        // aimed at the API's process group (a dev terminal's Ctrl-C, systemd KillMode=process) no longer reaches
        // it, so the run outlives the restart for the reconciler to recover/re-attach. `setsid` either execs the
        // shell in place or forks it, so the supervisor self-reports its real pid via the pid file rather than us
        // trusting the launched process's id. (This escapes the session/process group, NOT a cgroup or PID
        // namespace — systemd KillMode=control-group or a container teardown still stops it; that broader survival
        // is a heavier, separate concern.) macOS dev has no setsid, so run /bin/sh directly (no detach) — the
        // streaming + recovery paths are unaffected.
        if (OperatingSystem.IsLinux())
        {
            info.FileName = "setsid";
            info.ArgumentList.Add("/bin/sh");
        }
        else
        {
            info.FileName = "/bin/sh";
        }

        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(SupervisorScript);
        info.ArgumentList.Add("sh");          // $0 — the script reads the command + args from "$@" ($1 onward)

        // The per-run isolated config-home (Claude Code's CLAUDE_CONFIG_DIR / Codex's CODEX_HOME) doubles as the
        // sandbox's writable HOME + a writable bind, so compute + create it BEFORE building the command. Created
        // here (before launch) so bwrap's --bind source exists when the process starts.
        var configHome = spec.ConfigHomeEnvVars.Count > 0 ? Path.Combine(spoolDir, AgentConfigHomeDir) : null;
        if (configHome is not null) Directory.CreateDirectory(configHome);

        // Write the run-scoped MCP server declaration (0600 — it carries the run token) into the config-home BEFORE
        // launch so the harness reads it on start. No-op when the run has no tool fabric (spec.Mcp null) or no
        // config-home (the declaration has nowhere harness-isolated to live).
        WriteMcpDeclaration(spec.Mcp, configHome);

        // The actual command "$@": CONFINED under bwrap when this host supports it (fresh namespaces + read-only
        // minimal root + only the workspace/config-home writable), else the bare command (unconfined fallback).
        AppendChildCommand(info.ArgumentList, spec, configHome);

        ApplyEnvironment(info, spec);

        // Added AFTER ApplyEnvironment so they survive the scrub-driven Clear(); they are spool paths, not secrets.
        info.Environment["CSP_OUT"] = Path.Combine(spoolDir, StdoutFile);
        info.Environment["CSP_ERR"] = Path.Combine(spoolDir, StderrFile);
        info.Environment["CSP_EXIT"] = Path.Combine(spoolDir, ExitMarkerFile);
        info.Environment["CSP_PID"] = Path.Combine(spoolDir, PidFile);

        // Point the config-isolating tool at the per-run home so a shelled-out CLI reads ONLY the credentials we
        // inject — never the operator's personal ~/.claude / ~/.codex. Set AFTER the scrub so the injected value wins.
        if (configHome is not null)
            foreach (var name in spec.ConfigHomeEnvVars) info.Environment[name] = configHome;

        return info;
    }

    /// <summary>
    /// Append the agent command as the supervisor's <c>"$@"</c>: rewritten as a bubblewrap invocation
    /// (filesystem + namespace confinement, the ONLY writable host paths being the workspace + config-home) when
    /// <see cref="BubblewrapSandbox.Available"/>, else the bare command — the unconfined fallback on macOS dev, a
    /// host without <c>bwrap</c>, or one that denies unprivileged user namespaces.
    /// </summary>
    private static void AppendChildCommand(System.Collections.ObjectModel.Collection<string> argv, SandboxSpec spec, string? configHome)
    {
        // Fail-closed: a deployment that mandates isolation (CODESPACE_REQUIRE_SANDBOX) must never run unconfined.
        BubblewrapSandbox.EnsureSatisfiable(BubblewrapSandbox.Available, BubblewrapSandbox.IsRequired);

        var command = spec.Command;
        IReadOnlyList<string> args = spec.Args;

        // 1. Filesystem + namespace confinement (bubblewrap), innermost.
        if (BubblewrapSandbox.Available is { } bwrap)
        {
            var writable = new List<string>();
            if (!string.IsNullOrEmpty(spec.WorkingDirectory)) writable.Add(spec.WorkingDirectory);
            if (configHome is not null) writable.Add(configHome);

            var readOnlyExtra = new List<string>();

            // Bind the run's MCP socket writable so the spawned codespace-mcp proxy can connect to it. A SOCKET, not a
            // dir, so bind its PARENT dir — which is the DEDICATED <spool>/mcp/ subdir holding ONLY the socket (never
            // the spool's out.log/err.log/exit/pid — design §3b / Attack 4). The bind target must exist when bwrap
            // mounts; --unshare-net severs TCP but a bound UDS survives — the whole reason the transport is a socket.
            // Also bind the proxy binary's dir READ-ONLY so the harness can spawn it at its absolute identity-bound
            // path. No-op when the run has no tool fabric.
            if (spec.Mcp is { SocketPath: { Length: > 0 } socketPath } && Path.GetDirectoryName(socketPath) is { Length: > 0 } socketDir)
            {
                writable.Add(socketDir);

                if (Path.GetDirectoryName(McpProxyBinaryPath()) is { Length: > 0 } proxyDir) readOnlyExtra.Add(proxyDir);
            }

            args = BubblewrapSandbox.BuildArgs(new BwrapPlan
            {
                Command = command,
                Args = args,
                WorkingDirectory = spec.WorkingDirectory,
                HomeDir = configHome,
                WritablePaths = writable,
                ReadOnlyExtraPaths = readOnlyExtra,
                ShareNetwork = spec.AllowNetwork,
                EgressAllowlist = spec.EgressAllowlist,
            });
            command = bwrap;
        }

        // 2. Resource caps (prlimit), outermost — sets RLIMIT_NPROC + RLIMIT_FSIZE, then execs bwrap/agent which
        //    inherit them. Fork-bomb + runaway-file caps; memory-RSS + total-disk need the cgroup tier (a later slice).
        if (ProcessRlimits.Available is { } prlimit)
            (command, args) = ProcessRlimits.Wrap(prlimit, command, args, ProcessRlimits.EffectiveMaxProcesses(spec.MaxProcesses), ProcessRlimits.EffectiveMaxFileSizeMb(spec.MaxFileSizeMb));

        argv.Add(command);
        foreach (var arg in args) argv.Add(arg);
    }

    /// <summary>
    /// The ABSOLUTE host path of the <c>codespace-mcp</c> proxy binary: the <see cref="McpProxyPathEnvVar"/> override
    /// when set (an air-gapped mirror), else <c>codespace-mcp</c> next to the running assembly. Identity-bound into the
    /// sandbox, so this is also the in-sandbox command the harness declares — single source of truth for both the
    /// executor's <c>McpDeclarationContext.ProxyCommand</c> and the runner's read-only bind of its dir.
    /// </summary>
    public static string McpProxyBinaryPath() =>
        Environment.GetEnvironmentVariable(McpProxyPathEnvVar) is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, McpProxyFile);

    internal static string SpoolRoot() =>
        Environment.GetEnvironmentVariable(SpoolRootEnvVar) is { Length: > 0 } v ? v : Path.Combine(Path.GetTempPath(), "codespace", "agent-runs");

    internal static string SpoolDirectoryFor(string spoolKey) => Path.Combine(SpoolRoot(), spoolKey);

    /// <summary>
    /// The per-run MCP listener socket path — normally <c>&lt;spoolDir&gt;/mcp/mcp.sock</c> (a DEDICATED socket-only
    /// subdir, so the bwrap bind of its parent dir never exposes the spool's <c>out.log</c> / <c>exit</c> / etc. to the
    /// agent — design §3b / Attack 4) so it's reaped with the spool and the runner binds the SAME path. BUT an
    /// <c>AF_UNIX</c> address can't exceed <see cref="UnixSocketPathCap"/> bytes, and the spool root (a temp dir + a
    /// 32-hex run key + <c>/mcp/mcp.sock</c>) can overflow that on macOS — so when the canonical path is too long this
    /// falls back to a SHORT, still-unique temp path keyed by the run's FULL 32-hex run key whose parent (<c>cs-mcp/&lt;key&gt;</c>)
    /// also holds only the socket. Single source of truth: both the executor's listener and the harness/proxy's connect
    /// path call this, so they agree by construction.
    /// </summary>
    internal static string McpSocketPathFor(string spoolKey)
    {
        var canonical = Path.Combine(SpoolDirectoryFor(spoolKey), McpSocketDir, McpSocketFile);

        if (canonical.Length <= UnixSocketPathCap) return canonical;

        // Intentionally temp-rooted: the canonical path overflowed BECAUSE the spool root is long, so the short socket
        // must live elsewhere. Keyed by the FULL run key (~temp+42 ≈ 90 chars < cap on macOS), unlinked on dispose; if
        // even this overflows a pathological temp dir, the executor's fail-soft logs a Warning rather than crashes.
        return Path.Combine(Path.GetTempPath(), "cs-mcp", spoolKey, "s");
    }

    /// <summary>
    /// Write the harness-rendered MCP server declaration 0600 into the config-home (it carries the run token, so it must
    /// never be group/other-readable). The harness owns the FORMAT — it already rendered <see cref="McpServerWiring.Content"/>
    /// — so the runner stays dumb: it writes the bytes verbatim, no render. No-op when the run has no tool fabric
    /// (<paramref name="wiring"/> null) or no config-home (nowhere harness-isolated to put it — a run without config
    /// isolation can't host the proxy declaration without leaking it into a shared dir). The relative path is joined onto
    /// the config-home; on POSIX the file is then chmod'd 0600 (a no-op on Windows where unix modes don't apply — the
    /// per-run dir + token are the gate).
    /// </summary>
    internal static void WriteMcpDeclaration(McpServerWiring? wiring, string? configHome)
    {
        if (wiring is null || configHome is null) return;

        var path = Path.Combine(configHome, wiring.RelativeFileName);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, wiring.Content);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Read the bytes appended since <paramref name="offset"/> and split them into whole lines. Cuts the read at
    /// the LAST newline (so a multi-byte UTF-8 char is never split across a poll), leaving the trailing partial
    /// for the next read — unless <paramref name="drainPartial"/> (final drain), where the remainder is emitted
    /// too. Returns the lines and the new offset. Internal + static so it's unit-pinned.
    /// </summary>
    internal static (IReadOnlyList<string> Lines, long NewOffset) ReadNewLines(string path, long offset, bool drainPartial)
    {
        if (!File.Exists(path)) return (Array.Empty<string>(), offset);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (fs.Length <= offset) return (Array.Empty<string>(), offset);

        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(fs.Length - offset, MaxReadChunk)];
        var read = fs.Read(buffer, 0, buffer.Length);

        var boundary = ResolveBoundary(buffer, read, drainPartial);
        if (boundary == 0) return (Array.Empty<string>(), offset);

        return (SplitLines(Encoding.UTF8.GetString(buffer, 0, boundary)), offset + boundary);
    }

    /// <summary>How many bytes to consume this read: everything on a final drain; otherwise up to the last newline (0 = no whole line yet, unless a full chunk has no newline — then force progress).</summary>
    private static int ResolveBoundary(byte[] buffer, int read, bool drainPartial)
    {
        if (drainPartial) return read;

        for (var i = read - 1; i >= 0; i--)
            if (buffer[i] == (byte)'\n') return i + 1;

        return read == MaxReadChunk ? read : 0;
    }

    /// <summary>Split decoded spool text into lines: drop the empty remainder after a final newline, keep a non-empty trailing partial, and trim a CR from CRLF endings.</summary>
    internal static IReadOnlyList<string> SplitLines(string text)
    {
        var raw = text.Split('\n');
        var lines = new List<string>(raw.Length);

        for (var i = 0; i < raw.Length; i++)
        {
            var ln = raw[i];
            if (i == raw.Length - 1 && ln.Length == 0) break;

            lines.Add(ln.Length > 0 && ln[^1] == '\r' ? ln[..^1] : ln);
        }

        return lines;
    }

    /// <summary>True when the marker is present AND parses to an int. A mid-write / partial read returns false so the caller keeps polling.</summary>
    internal static bool TryReadExitCode(string exitPath, out int code)
    {
        code = 0;

        try { return File.Exists(exitPath) && int.TryParse(File.ReadAllText(exitPath).Trim(), out code); }
        catch { return false; }
    }

    /// <summary>
    /// True when the supervisor pid is still our live run. When a start time was recorded (<paramref
    /// name="expectedStartUtc"/>), it also guards against PID reuse: a live process whose start time no longer
    /// matches is a recycled pid — a DIFFERENT process the OS handed our old number — so it's NOT alive for us.
    /// An unreadable start time skips the guard rather than risk a false "gone" that would abandon a live run.
    /// </summary>
    private static bool IsProcessAlive(int pid, DateTimeOffset? expectedStartUtc)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;

            return !StartTimeMismatch(p, expectedStartUtc);
        }
        catch (ArgumentException) { return false; }        // no process with that id
        catch (InvalidOperationException) { return false; } // exited between the lookup and the access
    }

    /// <summary>True when a recorded start time disagrees (beyond tolerance) with the live process now holding the pid — i.e. the pid was recycled. False when no start time was recorded or it can't be read (guard skipped).</summary>
    private static bool StartTimeMismatch(Process p, DateTimeOffset? expectedStartUtc)
    {
        if (expectedStartUtc is not { } expected) return false;

        DateTimeOffset actual;
        try { actual = p.StartTime.ToUniversalTime(); }
        catch { return false; }

        return Math.Abs((actual - expected).TotalSeconds) > StartTimeToleranceSeconds;
    }

    /// <summary>Capture a process's start time (UTC) for the PID-reuse guard; null when the host can't report it (the guard is then skipped on probe).</summary>
    private static DateTimeOffset? TryReadStartTimeUtc(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime.ToUniversalTime(); }
        catch { return null; }
    }

    /// <summary>Terminate the supervisor's process tree (the timeout path and the explicit <see cref="TerminateAsync"/>) — but only when the pid is still OUR supervisor (the start-time guard), so a recycled pid handed to an unrelated process is never killed by us.</summary>
    private static void KillByIdQuietly(int pid, DateTimeOffset? expectedStartUtc)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited && !StartTimeMismatch(p, expectedStartUtc)) p.Kill(entireProcessTree: true);
        }
        catch { /* best-effort: already exited / reaped between the check and the kill */ }
    }

    private static async Task<string> ReadAllSafeAsync(string path)
    {
        try { return File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(false) : ""; }
        catch { return ""; }
    }

    /// <summary>Current byte length of a spool file, 0 when it is absent or unreadable — the watchdog's byte-progress probe.</summary>
    private static long SafeFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }
}
