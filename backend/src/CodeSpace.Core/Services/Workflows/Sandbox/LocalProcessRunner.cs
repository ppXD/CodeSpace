using System.Diagnostics;
using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Sandbox;

/// <summary>
/// v0 sandbox runner: runs the command as a child OS process on the worker itself. No container
/// isolation — this is the local-dev / single-tenant default that proves the seam end to end while
/// Docker / Kubernetes-Job runners are built behind the same <see cref="ISandboxRunner"/> contract.
///
/// Captures stdout/stderr in full (a future slice adds streaming + size caps for long agent runs),
/// enforces <see cref="SandboxSpec.TimeoutSeconds"/> by killing the process tree, and surfaces a
/// non-zero exit as <see cref="SandboxStatus.Failed"/> rather than throwing. Caller cancellation is
/// honoured distinctly from the spec timeout: it terminates the process and rethrows.
/// </summary>
public sealed class LocalProcessRunner : ISandboxRunner, ISingletonDependency
{
    public const string LocalKind = "local";

    public string Kind => LocalKind;

    public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = BuildStartInfo(spec) };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(spec.TimeoutSeconds));
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

    private static ProcessStartInfo BuildStartInfo(SandboxSpec spec)
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
        foreach (var (key, value) in spec.Environment) info.Environment[key] = value;

        return info;
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
