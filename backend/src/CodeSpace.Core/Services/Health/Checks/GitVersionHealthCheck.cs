using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CodeSpace.Core.Services.Health.Checks;

/// <summary>
/// Whether this host's git can carry a clone token. The credential helper is configured through <c>GIT_CONFIG_COUNT</c>, which
/// git reads from 2.31; an older git ignores it, finds no credential, and every tokened clone, fetch and push fails as "could
/// not read Username" — which reads as a bad token. Degraded, never Unhealthy: the process works, the commands that need a
/// credential do not, and an orchestrator must not restart a pod over an image it cannot fix by restarting.
/// </summary>
public sealed class GitVersionHealthCheck : IHealthCheck, ISingletonDependency
{
    /// <summary>The check's name in <c>/health</c> — a contract, pinned by test.</summary>
    public const string Name = "git";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) => Task.FromResult(Assess(HostGit.Current));

    public static HealthCheckResult Assess(GitReading git)
    {
        if (git.Version is not { } version)
            return HealthCheckResult.Degraded($"The git release could not be read ({git.Detail}), so whether it can carry a clone credential is unknown.");

        var data = new Dictionary<string, object> { ["version"] = version.ToString(), ["minimum"] = TokenedGitCommand.MinimumGit.ToString() };

        if (version.IsAtLeast(TokenedGitCommand.MinimumGit)) return HealthCheckResult.Healthy($"git {version}", data);

        return HealthCheckResult.Degraded(
            $"git {version} is older than {TokenedGitCommand.MinimumGit}: the credential helper that hands git its token is configured through GIT_CONFIG_COUNT, which git ignores before {TokenedGitCommand.MinimumGit}, " +
            "so every clone, fetch and push that needs a token fails as \"could not read Username\". Upgrade the git in this image.",
            data: data);
    }
}
