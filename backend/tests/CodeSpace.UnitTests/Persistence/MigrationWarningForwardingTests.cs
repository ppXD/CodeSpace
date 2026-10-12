using CodeSpace.Core.Persistence.Db;
using DbUp.Engine.Output;
using Npgsql;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// 🟢 Unit: which server notices a migration run writes to its log, and how. The real-runner path — a migration's
/// <c>RAISE WARNING</c> reaching what the production <c>DbUpRunner</c> prints — is pinned in
/// <c>DbUpRunnerTests.A_migration_warning_reaches_the_runners_own_log</c>.
/// </summary>
[Trait("Category", "Unit")]
public class MigrationWarningForwardingTests
{
    [Fact]
    public void A_warning_reaches_the_upgrade_log_with_its_hint()
    {
        var log = new CapturingUpgradeLog();

        WarningForwardingConnectionManager.Forward(Notice("WARNING", "WARNING", "0245: 1 address(es) are held by more than one live account", hint: "run CREATE UNIQUE INDEX"), log);

        log.Warnings.ShouldHaveSingleItem().ShouldBe("WARNING: 0245: 1 address(es) are held by more than one live account HINT: run CREATE UNIQUE INDEX");
    }

    [Fact]
    public void A_warning_with_braces_in_its_text_is_written_verbatim()
    {
        // 0118 lists team ids as a Postgres array. Used as a format string, "{…}" would throw instead of being logged.
        var log = new CapturingUpgradeLog();

        WarningForwardingConnectionManager.Forward(Notice("WARNING", "WARNING", "ownership-is-the-membership-role: {6f1c,9a2e}"), log);

        log.Warnings.ShouldHaveSingleItem().ShouldBe("WARNING: ownership-is-the-membership-role: {6f1c,9a2e}");
    }

    [Fact]
    public void A_warning_is_recognised_by_its_invariant_severity_whatever_the_server_language()
    {
        var log = new CapturingUpgradeLog();

        WarningForwardingConnectionManager.Forward(Notice("WARNUNG", "WARNING", "0245: localized"), log);

        log.Warnings.ShouldHaveSingleItem().ShouldBe("WARNING: 0245: localized");
    }

    [Theory]
    [InlineData("NOTICE")]   // every IF NOT EXISTS that finds its object already there
    [InlineData("INFO")]
    [InlineData("LOG")]
    [InlineData("DEBUG")]
    public void Routine_notices_stay_out_of_the_log(string severity)
    {
        var log = new CapturingUpgradeLog();

        WarningForwardingConnectionManager.Forward(Notice(severity, severity, "relation \"app_user\" already exists, skipping"), log);

        log.Warnings.ShouldBeEmpty();
        log.Other.ShouldBeEmpty();
    }

    private static PostgresNotice Notice(string severity, string invariantSeverity, string message, string? hint = null) =>
        new(messageText: message, severity: severity, invariantSeverity: invariantSeverity, sqlState: "01000", hint: hint);

    private sealed class CapturingUpgradeLog : IUpgradeLog
    {
        public List<string> Warnings { get; } = new();
        public List<string> Other { get; } = new();

        public void LogWarning(string format, params object[] args) => Warnings.Add(string.Format(format, args));
        public void LogTrace(string format, params object[] args) => Other.Add(string.Format(format, args));
        public void LogDebug(string format, params object[] args) => Other.Add(string.Format(format, args));
        public void LogInformation(string format, params object[] args) => Other.Add(string.Format(format, args));
        public void LogError(string format, params object[] args) => Other.Add(string.Format(format, args));
        public void LogError(Exception ex, string format, params object[] args) => Other.Add(string.Format(format, args));
    }
}
