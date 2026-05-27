using CodeSpace.Core.Hardening;
using CodeSpace.Core.Services.Workflows.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Unit pins for the three-mode enforcement guard against silent-null on missing project
/// refs (CLAUDE.md Rule 11 pattern). Coverage matches the rule's "Required tests for every
/// check" checklist:
///   1. EnforcementEnvVar constant name pinned.
///   2. Off mode: invalid value passes silently, no warning emitted.
///   3. Warn mode: invalid value passes, warning emitted with required tokens.
///   4. Strict mode: invalid value throws with required tokens.
///   5. Default mode (env var unset): same behaviour as Warn (per
///      <see cref="EnforcementModeReader.Read(string, EnforcementMode)"/> default).
/// </summary>
public class MissingProjectRefValidatorTests
{
    private static readonly Guid TeamId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WorkflowId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void EnforcementEnvVar_ConstantNamePinned()
    {
        // Renaming this constant breaks every operator who pinned the knob via env —
        // hard-pin in test (CLAUDE.md Rule 8). The rename becomes a compile-time-visible
        // decision, not an invisible refactor.
        MissingProjectRefValidator.EnforcementEnvVar.ShouldBe("CODESPACE_MISSING_PROJECT_REF_ENFORCEMENT");
    }

    [Fact]
    public void All_referenced_slugs_found_is_a_no_op_in_every_mode()
    {
        var referenced = new[] { "a", "b" };
        var found = new[] { "a", "b" };

        // None of the three modes do anything when the referenced set is a subset of
        // the found set — exercise all three to lock that contract.
        foreach (var mode in new[] { EnforcementMode.Off, EnforcementMode.Warn, EnforcementMode.Strict })
        {
            Should.NotThrow(() => MissingProjectRefValidator.EnsureKnown(
                referenced, found, TeamId, WorkflowId, mode, NullLogger.Instance));
        }
    }

    [Fact]
    public void Empty_referenced_set_is_a_no_op_even_when_team_has_no_projects()
    {
        Should.NotThrow(() => MissingProjectRefValidator.EnsureKnown(
            Array.Empty<string>(), Array.Empty<string>(), TeamId, WorkflowId,
            EnforcementMode.Strict, NullLogger.Instance));
    }

    [Fact]
    public void Off_mode_passes_silently_with_no_log_output()
    {
        var logger = new CapturingLogger();

        MissingProjectRefValidator.EnsureKnown(
            new[] { "exists", "missing" }, new[] { "exists" },
            TeamId, WorkflowId, EnforcementMode.Off, logger);

        logger.Events.ShouldBeEmpty("Off mode must produce zero log output");
    }

    [Fact]
    public void Warn_mode_passes_and_emits_log_warning_with_required_tokens()
    {
        var logger = new CapturingLogger();

        MissingProjectRefValidator.EnsureKnown(
            new[] { "exists", "ghost-a", "ghost-b" }, new[] { "exists" },
            TeamId, WorkflowId, EnforcementMode.Warn, logger);

        // Single warning emitted.
        logger.Events.Count.ShouldBe(1);
        var ev = logger.Events[0];
        ev.Level.ShouldBe(LogLevel.Warning);

        // Required tokens per CLAUDE.md Rule 11: the message must name the
        // missing values AND the env var to flip to strict — operators see only
        // the log line, not the source.
        ev.RenderedMessage.ShouldContain("ghost-a");
        ev.RenderedMessage.ShouldContain("ghost-b");
        ev.RenderedMessage.ShouldContain(MissingProjectRefValidator.EnforcementEnvVar);
        ev.RenderedMessage.ShouldContain("strict");
        ev.RenderedMessage.ShouldContain(WorkflowId.ToString());
        ev.RenderedMessage.ShouldContain(TeamId.ToString());
    }

    [Fact]
    public void Strict_mode_throws_with_required_tokens_in_message()
    {
        var ex = Should.Throw<MissingProjectRefException>(() =>
            MissingProjectRefValidator.EnsureKnown(
                new[] { "ghost-a", "ghost-b", "exists" }, new[] { "exists" },
                TeamId, WorkflowId, EnforcementMode.Strict, NullLogger.Instance));

        // Required tokens: missing slugs + the env var name (so the operator knows the
        // remediation path) + the workflow + team for triage.
        ex.Message.ShouldContain("ghost-a");
        ex.Message.ShouldContain("ghost-b");
        ex.Message.ShouldContain(MissingProjectRefValidator.EnforcementEnvVar);
        ex.Message.ShouldContain("warn");
        ex.Message.ShouldContain("off");
        ex.Message.ShouldContain(WorkflowId.ToString());
        ex.Message.ShouldContain(TeamId.ToString());
    }

    [Fact]
    public void Missing_slugs_in_warn_log_are_sorted_for_determinism()
    {
        // The validator sorts missing slugs before logging / throwing so operator-facing
        // text is deterministic across runs (helpful when greping logs / comparing
        // diff'd Failed runs).
        var logger = new CapturingLogger();

        MissingProjectRefValidator.EnsureKnown(
            new[] { "zebra", "alpha", "mango" }, Array.Empty<string>(),
            TeamId, WorkflowId, EnforcementMode.Warn, logger);

        var msg = logger.Events[0].RenderedMessage;
        msg.IndexOf("alpha").ShouldBeLessThan(msg.IndexOf("mango"));
        msg.IndexOf("mango").ShouldBeLessThan(msg.IndexOf("zebra"));
    }

    [Fact]
    public void Default_env_var_resolution_is_Warn_mode()
    {
        // Default rollout (CLAUDE.md Rule 11 v0): when the operator hasn't set the env
        // var at all, the reader returns Warn. The validator then logs but doesn't
        // throw. This pins the v0 default — a future bump to v1 (default Strict)
        // requires deliberately changing both the reader signature default AND this
        // test, which is the right gate.
        Environment.SetEnvironmentVariable(MissingProjectRefValidator.EnforcementEnvVar, null);
        EnforcementModeReader.Read(MissingProjectRefValidator.EnforcementEnvVar).ShouldBe(EnforcementMode.Warn);
    }

    // ── Test infrastructure ──────────────────────────────────────────────────────

    /// <summary>
    /// Minimal in-memory ILogger that captures every BeginScope/Log call. Compatible
    /// with the project's existing Microsoft.Extensions.Logging dependency — no extra
    /// mocking framework needed.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<LogEvent> Events { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Events.Add(new LogEvent(logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed record LogEvent(LogLevel Level, string RenderedMessage);
}
