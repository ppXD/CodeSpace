using System.Diagnostics;
using System.Text;
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
        var exitPath = Path.Combine(handle.SpoolDirectory, ExitMarkerFile);

        // Resume from the handle's checkpoint (0 on a first attach / an older handle) so a re-attach after a
        // restart picks up where the dead observer stopped instead of re-emitting the whole spool.
        var offset = handle.StdoutOffset;

        while (true)
        {
            var advanced = await EmitNewLinesAsync(stdoutPath, offset, onStdoutLine, drainPartial: false, cancellationToken).ConfigureAwait(false);

            // Checkpoint AFTER the batch's lines were delivered, only when it advanced — so the persisted offset
            // never runs ahead of the events (a re-attach at worst re-emits the last batch, never loses lines).
            if (onCheckpoint is not null && advanced != offset) await onCheckpoint(advanced, cancellationToken).ConfigureAwait(false);

            offset = advanced;

            if (TryReadExitCode(exitPath, out var code))
                return await CompleteFromSpoolAsync(handle, offset, code, onStdoutLine, cancellationToken).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow >= handle.Deadline)
                return await TimeoutAsync(handle, offset, onStdoutLine, cancellationToken).ConfigureAwait(false);

            if (!IsProcessAlive(handle.ProcessId, handle.ProcessStartTimeUtc))
                return await VanishedAsync(handle, offset, onStdoutLine, cancellationToken).ConfigureAwait(false);

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
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
        info.ArgumentList.Add(spec.Command);
        foreach (var arg in spec.Args) info.ArgumentList.Add(arg);

        ApplyEnvironment(info, spec);

        // Added AFTER ApplyEnvironment so they survive the scrub-driven Clear(); they are spool paths, not secrets.
        info.Environment["CSP_OUT"] = Path.Combine(spoolDir, StdoutFile);
        info.Environment["CSP_ERR"] = Path.Combine(spoolDir, StderrFile);
        info.Environment["CSP_EXIT"] = Path.Combine(spoolDir, ExitMarkerFile);
        info.Environment["CSP_PID"] = Path.Combine(spoolDir, PidFile);

        return info;
    }

    internal static string SpoolRoot() =>
        Environment.GetEnvironmentVariable(SpoolRootEnvVar) is { Length: > 0 } v ? v : Path.Combine(Path.GetTempPath(), "codespace", "agent-runs");

    internal static string SpoolDirectoryFor(string spoolKey) => Path.Combine(SpoolRoot(), spoolKey);

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

    /// <summary>Terminate the supervisor's process tree on the timeout path — but only when the pid is still OUR supervisor (the start-time guard), so a recycled pid handed to an unrelated process is never killed by us.</summary>
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
}
