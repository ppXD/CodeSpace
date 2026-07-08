using System.Diagnostics;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox.Runners;

/// <summary>
/// v0 sandbox runner: runs the command as a child OS process on the worker itself. No container
/// isolation — this is the local-dev / single-tenant default that proves the seam end to end while
/// Docker / Kubernetes-Job runners are built behind the same <see cref="ISandboxRunner"/> contract.
///
/// Implements the batch <see cref="ISandboxRunner"/> (full stdout/stderr capture), the streaming
/// <see cref="ISandboxStreamRunner"/> (stdout delivered line-by-line for live logs), and the DURABLE
/// <see cref="ISandboxDurableRunner"/> (launch + spool + re-attachable observation — see
/// <c>LocalProcessRunner.Durable.cs</c>). Enforces <see cref="SandboxSpec.TimeoutSeconds"/> by killing the
/// process tree, and surfaces a non-zero exit as <see cref="SandboxStatus.Failed"/> rather than throwing.
/// Caller cancellation is honoured distinctly from the spec timeout: it terminates the process and rethrows
/// (the durable path differs — see its remarks: cancellation stops observing without killing).
/// </summary>
public sealed partial class LocalProcessRunner : ISandboxRunner, ISandboxStreamRunner, ISandboxDurableRunner, ISingletonDependency
{
    public const string LocalKind = "local";

    /// <summary>
    /// The child process ALWAYS starts from a SCRUBBED environment: the worker's inherited env (DB / Redis /
    /// OAuth secrets, the variable master key, cloud credentials) is dropped and only the names in this allowlist
    /// plus the spec's own <see cref="SandboxSpec.Environment"/> survive. The agent's model key reaches the child
    /// by INJECTION into the spec env (resolve-and-inject), never by inheriting a bare key from the worker.
    /// This is the ONLY set of inherited variables a scrubbed child keeps: process/runtime essentials, locale,
    /// temp dirs, and the non-secret outbound-HTTPS knobs (proxy + custom CA bundle) an agent needs to reach its
    /// model API. Deliberately excludes every secret-bearing name. Names absent on the host are simply skipped.
    /// Pinned by a test (Rule 8) — widening it lets one more inherited worker variable reach the untrusted
    /// agent, so it must be a visible, reviewed decision rather than a silent drift.
    /// </summary>
    public static readonly IReadOnlyList<string> EnvAllowlist = new[]
    {
        // process / runtime essentials
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "TERM",
        // locale
        "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE", "TZ",
        // temp dirs
        "TMPDIR", "TEMP", "TMP",
        // Windows essentials
        "SystemRoot", "windir", "ComSpec", "PATHEXT", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS",
        // non-secret outbound HTTPS (proxy + custom CA) — needed to reach the model API
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "NODE_EXTRA_CA_CERTS", "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE",
    };

    /// <summary>Stall watchdog (Slice C3): seconds of NO streamed output before a run is judged STALLED and terminated early as <see cref="SandboxStatus.Stalled"/> — faster than waiting the full <see cref="SandboxSpec.TimeoutSeconds"/>. Unset ⇒ ON at <see cref="DefaultIdleTimeoutSeconds"/> (P2.4: real production runs get the protection by default — a genuinely-stuck agent no longer silently burns its whole wall-clock budget). Set explicitly to 0 / negative / non-numeric ⇒ the operator's OWN opt-OUT escape hatch (a repo/workload with known long silent tool calls). A positive value picks that window instead. Pinned by a test (Rule 8).</summary>
    public const string StdoutIdleTimeoutEnvVar = "CODESPACE_AGENT_STDOUT_IDLE_TIMEOUT_SECONDS";

    /// <summary>
    /// The default idle window (10 minutes) when the operator never touched <see cref="StdoutIdleTimeoutEnvVar"/> at
    /// all. Sized well above every silent-tool-call scenario this codebase's own test suite already treats as
    /// legitimate (the longest is a 120s synthetic sleep) and well below the 3600s/7200s overall wall-clock budgets,
    /// so it fires meaningfully faster than TimedOut for a run genuinely stuck (e.g. blocked on an interactive
    /// prompt it can't answer unattended) while staying generous enough to survive realistic slow-but-alive
    /// operations (a cold-cache dependency install, a large test suite, a slow/degraded model API response).
    /// </summary>
    public const int DefaultIdleTimeoutSeconds = 600;

    /// <summary>The configured stall-watchdog idle window: <see cref="DefaultIdleTimeoutSeconds"/> when the env var is genuinely unset, the parsed value when it's a positive integer, or null (disabled) when it's explicitly 0 / negative / non-numeric. Read per call so a test (and an operator) can toggle it without a restart.</summary>
    internal static TimeSpan? IdleTimeout()
    {
        var raw = Environment.GetEnvironmentVariable(StdoutIdleTimeoutEnvVar);

        if (raw is null) return TimeSpan.FromSeconds(DefaultIdleTimeoutSeconds);

        return int.TryParse(raw, out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>The wall-clock cancellation source for a spec's <see cref="SandboxSpec.TimeoutSeconds"/>: a positive value arms the timeout; <c>null</c> or ≤0 means NO wall-clock — an unarmed source that never auto-cancels, so the run is bounded only by caller cancellation + the stall watchdog (the operator's "no timeout" choice).</summary>
    private static CancellationTokenSource WallClockCts(int? timeoutSeconds) =>
        timeoutSeconds is { } seconds && seconds > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(seconds)) : new CancellationTokenSource();

    /// <summary>Internal signal: the stall watchdog tripped (no output for the idle window). Caught by the streaming caller and mapped to <see cref="SandboxStatus.Stalled"/>.</summary>
    private sealed class AgentStalledException : Exception { }

    public string Kind => LocalKind;

    public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = BuildStartInfo(spec) };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = WallClockCts(spec.TimeoutSeconds);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await TerminateAsync(process, stdoutTask, stderrTask, cancellationToken).ConfigureAwait(false);
        }

        var status = process.ExitCode == 0 ? SandboxStatus.Success : SandboxStatus.Failed;

        return new SandboxResult { Status = status, ExitCode = process.ExitCode, Stdout = await stdoutTask.ConfigureAwait(false), Stderr = await stderrTask.ConfigureAwait(false) };
    }

    public async Task<SandboxResult> RunStreamingAsync(SandboxSpec spec, Func<string, CancellationToken, Task> onStdoutLine, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = BuildStartInfo(spec) };

        process.Start();

        // stderr captured in full (diagnostic context for the result); stdout is pumped line-by-line to the consumer.
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = WallClockCts(spec.TimeoutSeconds);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await PumpStdoutAsync(process, onStdoutLine, linkedCts.Token, IdleTimeout()).ConfigureAwait(false);
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (AgentStalledException)
        {
            return await TerminateStreamingAsync(process, stderrTask, cancellationToken, stalled: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await TerminateStreamingAsync(process, stderrTask, cancellationToken, stalled: false).ConfigureAwait(false);
        }

        var status = process.ExitCode == 0 ? SandboxStatus.Success : SandboxStatus.Failed;

        return new SandboxResult { Status = status, ExitCode = process.ExitCode, Stdout = "", Stderr = await stderrTask.ConfigureAwait(false) };
    }

    /// <summary>
    /// Read stdout line-by-line, awaiting the consumer per line so a slow consumer backpressures the read. Ends when
    /// stdout closes (process exit). With no <paramref name="idleTimeout"/> (the default) this is a plain line pump.
    /// When the C3 stall watchdog is on, it delegates to <see cref="PumpStdoutWithIdleWatchdogAsync"/>, which reads at
    /// the BYTE level so any output — even a newline-less progress bar — resets the idle clock.
    /// </summary>
    private static async Task PumpStdoutAsync(Process process, Func<string, CancellationToken, Task> onStdoutLine, CancellationToken cancellationToken, TimeSpan? idleTimeout)
    {
        if (idleTimeout is { } idle)
        {
            await PumpStdoutWithIdleWatchdogAsync(process, onStdoutLine, cancellationToken, idle).ConfigureAwait(false);
            return;
        }

        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            await onStdoutLine(line, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The C3 stall watchdog read loop. Reads stdout at the CHAR level (not <c>ReadLineAsync</c>, which blocks until a
    /// newline and so can't tell a stalled run from one mid-line) bounded by the idle window: any chunk of bytes resets
    /// the window, so a run streaming a newline-less progress bar (<c>\r</c> updates) or a single slow long line is
    /// alive — never falsely stalled. Only TRUE silence (no bytes for the whole window) throws <see cref="AgentStalledException"/>;
    /// a genuine caller / overall-timeout cancel propagates as <see cref="OperationCanceledException"/>. Chars are
    /// buffered and delivered to the consumer as whole <c>\n</c>-terminated lines (a trailing CR from a CRLF trimmed),
    /// and a final partial line is flushed at EOF. Note: unlike <c>ReadLineAsync</c> (the default path) a LONE <c>\r</c>
    /// is NOT a line break here — a <c>\r</c>-style progress bar stays one logical line, which is the intended rendering
    /// (splitting it into N phantom lines is exactly the line-centric behaviour the watchdog moves away from).
    /// </summary>
    private static async Task PumpStdoutWithIdleWatchdogAsync(Process process, Func<string, CancellationToken, Task> onStdoutLine, CancellationToken cancellationToken, TimeSpan idle)
    {
        var reader = process.StandardOutput;
        var buffer = new char[4096];
        var line = new StringBuilder();

        while (true)
        {
            int read;

            using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                idleCts.CancelAfter(idle);

                try { read = await reader.ReadAsync(buffer.AsMemory(), idleCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new AgentStalledException();   // no bytes for the whole idle window (not a real cancel) → stalled
                }
            }

            if (read == 0)   // stdout closed → process exiting; flush a trailing partial line
            {
                if (line.Length > 0) await onStdoutLine(line.ToString(), cancellationToken).ConfigureAwait(false);
                break;
            }

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n') { line.Append(buffer[i]); continue; }

                if (line.Length > 0 && line[^1] == '\r') line.Length--;

                await onStdoutLine(line.ToString(), cancellationToken).ConfigureAwait(false);
                line.Clear();
            }
        }
    }

    /// <summary>Same terminate semantics as the batch path: kill the tree, let stderr settle, rethrow on caller-cancel, else map to Stalled (C3) or TimedOut.</summary>
    private static async Task<SandboxResult> TerminateStreamingAsync(Process process, Task<string> stderrTask, CancellationToken cancellationToken, bool stalled)
    {
        KillQuietly(process);

        var stderr = await SafeRead(stderrTask).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return new SandboxResult { Status = stalled ? SandboxStatus.Stalled : SandboxStatus.TimedOut, ExitCode = -1, Stdout = "", Stderr = stderr };
    }

    /// <summary>Builds the child <see cref="ProcessStartInfo"/> from a scrubbed environment. Internal + static so the scrub behaviour is unit-testable against a real <see cref="ProcessStartInfo"/>.</summary>
    internal static ProcessStartInfo BuildStartInfo(SandboxSpec spec)
    {
        var info = new ProcessStartInfo
        {
            FileName = spec.Command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty,
        };

        foreach (var arg in spec.Args) info.ArgumentList.Add(arg);

        ApplyEnvironment(info, spec);

        return info;
    }

    /// <summary>Reduce the inherited worker environment to <see cref="EnvAllowlist"/> over a <see cref="NonInteractiveEnv"/> base, then layer the spec's own variables on top — so a spec-injected value wins over an allow-listed inherited one, which wins over a non-interactive default.</summary>
    internal static void ApplyEnvironment(ProcessStartInfo info, SandboxSpec spec)
    {
        var preserved = new List<KeyValuePair<string, string?>>();
        foreach (var name in EnvAllowlist)
            if (info.Environment.TryGetValue(name, out var value)) preserved.Add(new(name, value));

        info.Environment.Clear();

        // C1: the non-interactive BASE of a scrubbed env — a nested apt/npm/pip/git prompt auto-defaults instead of
        // reading EOF / hanging until the wall-clock timeout kills the tree (a useless TimedOut, never a decide). Lowest
        // precedence: an allow-listed inherited value, then an explicit spec.Environment value, layered on top still
        // wins (operator intent > default). Flows through the bwrap path too — it has no --clearenv, so the confined
        // child inherits this env.
        foreach (var (key, value) in NonInteractiveEnv.Defaults) info.Environment[key] = value;

        foreach (var kept in preserved) info.Environment[kept.Key] = kept.Value;

        foreach (var (key, value) in spec.Environment) info.Environment[key] = value;
    }

    /// <summary>Kill the (possibly child-spawning) process, then map to TimedOut — unless the CALLER cancelled, which rethrows.</summary>
    private static async Task<SandboxResult> TerminateAsync(Process process, Task<string> stdoutTask, Task<string> stderrTask, CancellationToken cancellationToken)
    {
        KillQuietly(process);

        // Let the captured output settle (the kill closes the child's pipes) BEFORE we return OR throw,
        // so the read tasks never run against the Process as `using` disposes it on the caller-cancel path.
        var stdout = await SafeRead(stdoutTask).ConfigureAwait(false);
        var stderr = await SafeRead(stderrTask).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return new SandboxResult { Status = SandboxStatus.TimedOut, ExitCode = -1, Stdout = stdout, Stderr = stderr };
    }

    /// <summary>Await a redirected-stream read that may fault if the process was killed mid-read — partial/empty output is best-effort.</summary>
    private static async Task<string> SafeRead(Task<string> readTask)
    {
        try { return await readTask.ConfigureAwait(false); }
        catch { return ""; }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: the process may have exited between the check and the kill.
        }
    }
}
