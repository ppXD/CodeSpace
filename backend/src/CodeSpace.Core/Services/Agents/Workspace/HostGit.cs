using CodeSpace.Messages.Health;
using System.Diagnostics;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// The <c>git</c> this host runs, asked once per process: every clone, fetch, push and launch-time <c>ls-remote</c> spawns
/// the same binary from the same PATH, so one <c>git --version</c> answers for all of them. The answer is what the
/// credential helper's 2.31 floor (<see cref="TokenedGitCommand.MinimumGit"/>) is measured against — at boot, in the health
/// report, and when a tokened command fails for want of a credential.
///
/// <para>A probe that fails — no git on PATH, a hung or unreadable answer — is a <see cref="GitReading"/> with no version,
/// never an exception: this is read from a boot path and a health endpoint, neither of which may fail because of it.</para>
/// </summary>
public static class HostGit
{
    private const int ProbeTimeoutMs = 10_000;

    private static readonly Lazy<GitReading> Probed = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly AsyncLocal<GitReading?> Stand = new();

    /// <summary>The host's git as it answered the first time it was asked.</summary>
    public static GitReading Current => Stand.Value ?? Probed.Value;

    /// <summary>
    /// Answer as <paramref name="reading"/> for the current async flow until the returned scope is disposed — the seam that
    /// lets a test put an old git in front of a code path without installing one. Scoped to the flow, so tests running in
    /// parallel never see each other's.
    /// </summary>
    internal static IDisposable Use(GitReading reading)
    {
        var previous = Stand.Value;
        Stand.Value = reading;

        return new Restore(previous);
    }

    private static GitReading Probe()
    {
        try
        {
            var start = new ProcessStartInfo { FileName = "git", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--version");

            using var process = Process.Start(start) ?? throw new InvalidOperationException("the process did not start");

            if (!process.WaitForExit(ProbeTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }

                return new GitReading(null, $"git could not be run: `git --version` did not answer within {ProbeTimeoutMs / 1000}s");
            }

            var output = process.StandardOutput.ReadToEnd().Trim();

            return process.ExitCode == 0 ? new GitReading(GitVersion.Parse(output), output) : new GitReading(null, $"git could not be run: `git --version` exited {process.ExitCode}");
        }
        catch (Exception ex)
        {
            return new GitReading(null, $"git could not be run: {ex.GetType().Name}");
        }
    }

    private sealed class Restore(GitReading? previous) : IDisposable
    {
        public void Dispose() => Stand.Value = previous;
    }
}
