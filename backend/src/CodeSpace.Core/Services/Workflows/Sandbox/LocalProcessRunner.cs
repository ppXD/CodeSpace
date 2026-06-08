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

        cancellationToken.ThrowIfCancellationRequested();

        return new SandboxResult { Status = SandboxStatus.TimedOut, ExitCode = -1, Stdout = await stdoutTask.ConfigureAwait(false), Stderr = await stderrTask.ConfigureAwait(false) };
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
