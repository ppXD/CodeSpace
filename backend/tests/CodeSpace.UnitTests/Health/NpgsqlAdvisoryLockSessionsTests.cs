using CodeSpace.Core.Services.Health;
using CodeSpace.Core.Services.Health.Sessions;
using CodeSpace.Core.Settings.Database;
using CodeSpace.Messages.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Health;

/// <summary>
/// The sessions are built when the host is, from a connection string that may be anything an operator typed. A string the
/// driver cannot parse is the self-test's "could not run" — one of the findings /health reports — and never a failure to
/// build the host, which is the one thing a posture report must not cause.
/// </summary>
[Trait("Category", "Unit")]
public sealed class NpgsqlAdvisoryLockSessionsTests
{
    [Theory]
    [InlineData("this is not a connection string")]
    [InlineData("Host=localhost;NoSuchKeyword=1")]
    [InlineData("")]
    public async Task A_connection_string_the_driver_rejects_builds_the_sessions_and_makes_the_self_test_inconclusive(string connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CodeSpaceStore:ConnectionString"] = connectionString }).Build();

        var sessions = Should.NotThrow(() => new NpgsqlAdvisoryLockSessions(new CodeSpaceConnectionString(configuration)), "construction must never fail the host");

        var verdict = await new RefreshLockSelfTest(sessions, TimeProvider.System, NullLogger<RefreshLockSelfTest>.Instance).VerdictAsync(CancellationToken.None);

        verdict.Outcome.ShouldBe(RefreshLockOutcome.Inconclusive);
    }
}
