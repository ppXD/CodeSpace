using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.TaskLaunch;

public sealed partial class TaskLaunchBenchmarkCellRunner
{
    private const int GitTimeoutSeconds = 30;

    /// <summary>Run one git argv through the SAME confined local sandbox runner the grader uses for the test command — never a shell, no positional the caller doesn't control. A non-zero exit throws with stderr, so a broken fixture-repo/patch step surfaces as this cell's own infra fault rather than a silently wrong grade.</summary>
    private async Task RunGitAsync(IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await _runners.Resolve(Sandbox.SandboxKinds.Local)
            .RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workingDirectory, TimeoutSeconds = GitTimeoutSeconds }, cancellationToken)
            .ConfigureAwait(false);

        if (result.Status != SandboxStatus.Success)
            throw new InvalidOperationException($"git {string.Join(' ', args)} (cwd {workingDirectory}) failed: {result.Status}, exit {result.ExitCode}: {result.Stderr}");
    }
}
