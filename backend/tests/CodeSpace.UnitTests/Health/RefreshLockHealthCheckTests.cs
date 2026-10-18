using CodeSpace.Core.Services.Health;
using CodeSpace.Core.Services.Health.Checks;
using CodeSpace.Messages.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;

namespace CodeSpace.UnitTests.Health;

/// <summary>The refresh-lock self-test's finding, as a health entry: Degraded for anything but an exclusive lock, with the sentence an operator reads.</summary>
[Trait("Category", "Unit")]
public sealed class RefreshLockHealthCheckTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_check_is_named_refresh_lock_in_the_report() => RefreshLockHealthCheck.Name.ShouldBe("refresh-lock");

    [Fact]
    public void An_exclusive_lock_is_healthy()
    {
        var result = RefreshLockHealthCheck.Assess(new RefreshLockVerdict(RefreshLockOutcome.Exclusive, "exclusive", At));

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Data["outcome"].ShouldBe("Exclusive");
        result.Data["observedAtUtc"].ShouldBe(At.UtcDateTime);
    }

    [Theory]
    [InlineData(RefreshLockOutcome.NotExclusive)]
    [InlineData(RefreshLockOutcome.Inconclusive)]
    public void A_lock_that_is_not_exclusive_or_not_known_to_be_is_degraded_with_the_precise_message(RefreshLockOutcome outcome)
    {
        var result = RefreshLockHealthCheck.Assess(new RefreshLockVerdict(outcome, "the precise message", At));

        result.Status.ShouldBe(HealthStatus.Degraded, "Degraded leaves /health/ready at 200: the compare-and-set and the re-read keep refresh correct, so no pod is worth restarting");
        result.Description.ShouldBe("the precise message");
        result.Data["outcome"].ShouldBe(outcome.ToString());
    }

    [Fact]
    public async Task The_check_asks_the_self_test()
    {
        var test = new ScriptedSelfTest(new RefreshLockVerdict(RefreshLockOutcome.NotExclusive, "pooler", At));

        var result = await new RefreshLockHealthCheck(test).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.ShouldBe(HealthStatus.Degraded);
        test.Asks.ShouldBe(1);
    }

    private sealed class ScriptedSelfTest(RefreshLockVerdict verdict) : IRefreshLockSelfTest
    {
        public int Asks { get; private set; }

        public Task<RefreshLockVerdict> VerdictAsync(CancellationToken cancellationToken)
        {
            Asks++;
            return Task.FromResult(verdict);
        }
    }
}
