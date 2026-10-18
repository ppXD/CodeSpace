using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Health.Checks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Health;

/// <summary>
/// Once the host is serving, says what this pod is: one line naming the build, the role and the git it runs, a Warning when
/// that git is below the credential helper's floor, and the refresh-lock self-test, whose own finding is logged as it lands.
/// The log-side twin of <c>/health</c>, for the moment nothing can reach the pod to ask it.
///
/// <para>It runs off the thread that starts the host and swallows everything: the only thing a posture report may do to a
/// process is describe it. A pod whose git is too old, or whose lock is not exclusive, starts all the same and says so.</para>
/// </summary>
public sealed class BootPostureReporter : ISingletonDependency
{
    private readonly HostPosture _posture;
    private readonly IRefreshLockSelfTest _selfTest;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<BootPostureReporter> _logger;

    public BootPostureReporter(HostPosture posture, IRefreshLockSelfTest selfTest, IHostApplicationLifetime lifetime, ILogger<BootPostureReporter> logger)
    {
        _posture = posture;
        _selfTest = selfTest;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>Arrange for the report when the host has started. Returns at once.</summary>
    public void ReportWhenStarted() => _lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => ReportAsync(_lifetime.ApplicationStopping)));

    internal async Task ReportAsync(CancellationToken cancellationToken)
    {
        try
        {
            ReportPosture();

            await _selfTest.VerdictAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is stopping; there is nothing left to report to.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The boot posture report could not finish; the pod is unaffected");
        }
    }

    private void ReportPosture()
    {
        var git = HostGit.Current;

        _logger.LogInformation("Boot posture: build {Build}, role {Role} ({RoleMeaning}), git {GitVersion}", _posture.Build, _posture.RoleName, _posture.RoleMeaning, git.Version?.ToString() ?? git.Detail);

        var assessment = GitVersionHealthCheck.Assess(git);

        if (assessment.Status != HealthStatus.Healthy) _logger.LogWarning("{GitPosture}", assessment.Description);
    }
}
