using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CodeSpace.Core.Services.Health.Checks;

/// <summary>
/// The refresh-lock self-test's finding, as a health entry. Degraded for a lock that is not exclusive or not known to be:
/// the compare-and-set write and the re-read keep refreshed tokens correct, so nothing here is worth a restart, but a
/// deployment whose refresh is not serialized should know.
/// </summary>
public sealed class RefreshLockHealthCheck : IHealthCheck, ISingletonDependency
{
    /// <summary>The check's name in <c>/health</c> — a contract, pinned by test.</summary>
    public const string Name = "refresh-lock";

    private readonly IRefreshLockSelfTest _selfTest;

    public RefreshLockHealthCheck(IRefreshLockSelfTest selfTest) { _selfTest = selfTest; }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Assess(await _selfTest.VerdictAsync(cancellationToken).ConfigureAwait(false));

    public static HealthCheckResult Assess(RefreshLockVerdict verdict)
    {
        var data = new Dictionary<string, object> { ["outcome"] = verdict.Outcome.ToString(), ["observedAtUtc"] = verdict.ObservedAt.UtcDateTime };

        return verdict.Outcome == RefreshLockOutcome.Exclusive ? HealthCheckResult.Healthy(verdict.Detail, data) : HealthCheckResult.Degraded(verdict.Detail, data: data);
    }
}
