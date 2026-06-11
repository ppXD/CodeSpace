using System.Diagnostics;
using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox.Runners;

/// <summary>
/// The DURABLE-runner half of <see cref="LocalProcessRunner"/> (<see cref="ISandboxDurableRunner"/>): launch
/// the command under a <c>/bin/sh</c> supervisor that redirects its output to on-disk spool files and records
/// an exit-code marker, then observe the run by TAILING that spool. Decoupling the run's output from a parent
/// pipe is what lets a restarted backend recover the run from its persisted <see cref="SandboxHandle"/>
/// instead of losing it. POSIX-only (needs <c>/bin/sh</c>); a non-POSIX host falls back to the streaming path.
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

    /// <summary>Tail cadence — how often the observer re-reads the spool for new lines / checks the exit marker.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Per-poll read cap so a burst can't allocate unbounded; the next poll continues from the new offset.</summary>
    private const int MaxReadChunk = 8 * 1024 * 1024;

    /// <summary>
    /// The supervisor script: run the command (the positional <c>"$@"</c>) with stdout→out.log, stderr→err.log,
    /// then write the exit code to the marker. The marker is written AFTER the command and BEFORE the shell
    /// exits, so "marker present" reliably means "the command finished with this code" and "shell gone with no
    /// marker" means it was killed before recording one.
    /// </summary>
    private const string SupervisorScript = "\"$@\" >\"$CSP_OUT\" 2>\"$CSP_ERR\"; printf '%s' \"$?\" >\"$CSP_EXIT\"";

    public Task<SandboxHandle> LaunchAsync(SandboxSpec spec, string spoolKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var spoolDir = SpoolDirectoryFor(spoolKey);
        Directory.CreateDirectory(spoolDir);

        using var process = new Process { StartInfo = BuildDurableStartInfo(spec, spoolDir, ParseEnvScrubFlag(Environment.GetEnvironmentVariable(EnvScrubEnvVar))) };
        process.Start();

        var handle = new SandboxHandle
        {
            Kind = LocalKind,
            ProcessId = process.Id,
            SpoolDirectory = spoolDir,
            Deadline = DateTimeOffset.UtcNow.AddSeconds(spec.TimeoutSeconds),
        };

        return Task.FromResult(handle);
    }

    public async Task<SandboxResult> AttachAsync(SandboxHandle handle, Func<string, CancellationToken, Task> onStdoutLine, CancellationToken cancellationToken)
    {
        var stdoutPath = Path.Combine(handle.SpoolDirectory, StdoutFile);
        var exitPath = Path.Combine(handle.SpoolDirectory, ExitMarkerFile);
        var offset = 0L;

        while (true)
        {
            offset = await EmitNewLinesAsync(stdoutPath, offset, onStdoutLine, drainPartial: false, cancellationToken).ConfigureAwait(false);

            if (TryReadExitCode(exitPath, out var code))
                return await CompleteFromSpoolAsync(handle, offset, code, onStdoutLine, cancellationToken).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow >= handle.Deadline)
                return await TimeoutAsync(handle, offset, onStdoutLine, cancellationToken).ConfigureAwait(false);

            if (!IsProcessAlive(handle.ProcessId))
                return await VanishedAsync(handle, offset, onStdoutLine, cancellationToken).ConfigureAwait(false);

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<SandboxProbe> ProbeAsync(SandboxHandle handle, CancellationToken cancellationToken)
    {
        // Marker first: it's written BEFORE the supervisor exits, so its presence authoritatively means "finished".
        if (TryReadExitCode(Path.Combine(handle.SpoolDirectory, ExitMarkerFile), out var code))
            return Task.FromResult(new SandboxProbe { State = SandboxRunState.Exited, ExitCode = code });

        var state = IsProcessAlive(handle.ProcessId) ? SandboxRunState.Running : SandboxRunState.Gone;

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
        KillByIdQuietly(handle.ProcessId);

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

    internal static ProcessStartInfo BuildDurableStartInfo(SandboxSpec spec, string spoolDir, bool scrub)
    {
        var info = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty,
        };

        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(SupervisorScript);
        info.ArgumentList.Add("sh");          // $0 — the script reads the command + args from "$@" ($1 onward)
        info.ArgumentList.Add(spec.Command);
        foreach (var arg in spec.Args) info.ArgumentList.Add(arg);

        ApplyEnvironment(info, spec, scrub);

        // Added AFTER ApplyEnvironment so they survive a scrub-driven Clear(); they are spool paths, not secrets.
        info.Environment["CSP_OUT"] = Path.Combine(spoolDir, StdoutFile);
        info.Environment["CSP_ERR"] = Path.Combine(spoolDir, StderrFile);
        info.Environment["CSP_EXIT"] = Path.Combine(spoolDir, ExitMarkerFile);

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

    private static bool IsProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void KillByIdQuietly(int pid)
    {
        try { using var p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* best-effort: already exited / reaped between the check and the kill */ }
    }

    private static async Task<string> ReadAllSafeAsync(string path)
    {
        try { return File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(false) : ""; }
        catch { return ""; }
    }
}
