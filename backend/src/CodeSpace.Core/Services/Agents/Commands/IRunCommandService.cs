using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Commands;

/// <summary>
/// Runs one command in a sandbox, optionally inside a freshly-cloned repository workspace, and returns its
/// <see cref="SandboxResult"/>. The orchestration seam behind the <c>agent.run_command</c> node: it resolves
/// the runner + workspace provider by <see cref="RunCommandRequest.RunnerKind"/> (default "local"), clones the
/// repo when one is named, runs the command under the spec's isolation, and disposes the workspace.
///
/// Deployment-neutral by construction — a docker / k8s runner + workspace provider plug in behind the same
/// registries with no change here. A non-zero exit is a normal <see cref="SandboxResult"/> (Failed), never an
/// exception; only infrastructure failures (clone error, runner crash) and a blank command throw.
/// </summary>
public interface IRunCommandService
{
    Task<SandboxResult> RunAsync(RunCommandRequest request, CancellationToken cancellationToken);
}
