using CodeSpace.Core.Hardening;
using CodeSpace.Core.Services.Workflows.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Unit pins for the three-mode enforcement guard against silent-null on missing required
/// inputs (CLAUDE.md Rule 11 pattern). Coverage matches the rule's "Required tests for every
/// check" checklist:
///   1. EnforcementEnvVar constant name pinned.
///   2. Off mode: missing required input passes silently, no warning emitted.
///   3. Warn mode: missing required input passes, warning emitted with required tokens.
///   4. Strict mode: missing required input throws with required tokens.
///   5. Default mode (env var unset): same behaviour as Warn (per
///      <see cref="EnforcementModeReader.Read(string, EnforcementMode)"/> default).
///
/// Plus edge cases: empty required-set is a no-op; all-satisfied is a no-op; sorted output
/// order for deterministic log readability.
/// </summary>
[Trait("Category", "Unit")]
public class MissingRequiredInputValidatorTests
{
    private static readonly Guid TeamId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid WorkflowId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void EnforcementEnvVar_ConstantNamePinned()
    {
        // Renaming this constant breaks every operator who pinned the knob via env —
        // hard-pin in test (CLAUDE.md Rule 8). The rename becomes a compile-time-visible
        // decision, not an invisible refactor.
        MissingRequiredInputValidator.EnforcementEnvVar.ShouldBe("CODESPACE_MISSING_REQUIRED_INPUT_ENFORCEMENT");
    }

    private static MissingRequiredInputContext Ctx(IReadOnlyCollection<string> required, IReadOnlyCollection<string> resolved) =>
        new(required, resolved, TeamId, WorkflowId);

    [Fact]
    public void All_required_inputs_resolved_is_a_no_op_in_every_mode()
    {
        var ctx = Ctx(new[] { "name", "email" }, new[] { "name", "email" });

        foreach (var mode in new[] { EnforcementMode.Off, EnforcementMode.Warn, EnforcementMode.Strict })
            Should.NotThrow(() => MissingRequiredInputValidator.EnsureSatisfied(ctx, mode, NullLogger.Instance));
    }

    [Fact]
    public void Empty_required_set_is_a_no_op_even_when_resolved_set_is_empty()
    {
        // Most workflows have zero Required inputs — fast-exit path. Validating "no required
        // = pass" explicitly so a future refactor that inverts the guard doesn't silently
        // start failing the empty-definition case.
        var ctx = Ctx(Array.Empty<string>(), Array.Empty<string>());
        Should.NotThrow(() => MissingRequiredInputValidator.EnsureSatisfied(ctx, EnforcementMode.Strict, NullLogger.Instance));
    }

    [Fact]
    public void Off_mode_passes_silently_with_no_log_output()
    {
        var logger = new CapturingLogger();
        var ctx = Ctx(new[] { "name", "email" }, new[] { "name" });

        MissingRequiredInputValidator.EnsureSatisfied(ctx, EnforcementMode.Off, logger);

        logger.Events.ShouldBeEmpty("Off mode must produce zero log output");
    }

    [Fact]
    public void Warn_mode_passes_and_emits_log_warning_with_required_tokens()
    {
        var logger = new CapturingLogger();
        var ctx = Ctx(new[] { "name", "ghost-a", "ghost-b" }, new[] { "name" });

        MissingRequiredInputValidator.EnsureSatisfied(ctx, EnforcementMode.Warn, logger);

        // Single warning emitted.
        logger.Events.Count.ShouldBe(1);
        var ev = logger.Events[0];
        ev.Level.ShouldBe(LogLevel.Warning);

        // Required tokens per CLAUDE.md Rule 11: the message must name the
        // missing values AND the env var to flip to strict — operators see only
        // the log line, not the source.
        ev.RenderedMessage.ShouldContain("ghost-a");
        ev.RenderedMessage.ShouldContain("ghost-b");
        ev.RenderedMessage.ShouldContain(MissingRequiredInputValidator.EnforcementEnvVar);
        ev.RenderedMessage.ShouldContain("strict");
        ev.RenderedMessage.ShouldContain(WorkflowId.ToString());
        ev.RenderedMessage.ShouldContain(TeamId.ToString());
    }

    [Fact]
    public void Strict_mode_throws_with_required_tokens_in_message()
    {
        var ctx = Ctx(new[] { "ghost-a", "ghost-b", "name" }, new[] { "name" });

        var ex = Should.Throw<MissingRequiredInputException>(() =>
            MissingRequiredInputValidator.EnsureSatisfied(ctx, EnforcementMode.Strict, NullLogger.Instance));

        // Required tokens: missing names + the env var name (so the operator knows the
        // remediation path) + the workflow + team for triage.
        ex.Message.ShouldContain("ghost-a");
        ex.Message.ShouldContain("ghost-b");
        ex.Message.ShouldContain(MissingRequiredInputValidator.EnforcementEnvVar);
        ex.Message.ShouldContain("warn");
        ex.Message.ShouldContain("off");
        ex.Message.ShouldContain(WorkflowId.ToString());
        ex.Message.ShouldContain(TeamId.ToString());
    }

    [Fact]
    public void Missing_names_in_warn_log_are_sorted_for_determinism()
    {
        // The validator sorts missing names before logging / throwing so operator-facing
        // text is deterministic across runs (helpful when greping logs / comparing
        // diff'd Failed runs).
        var logger = new CapturingLogger();
        var ctx = Ctx(new[] { "zebra", "alpha", "mango" }, Array.Empty<string>());

        MissingRequiredInputValidator.EnsureSatisfied(ctx, EnforcementMode.Warn, logger);

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
        Environment.SetEnvironmentVariable(MissingRequiredInputValidator.EnforcementEnvVar, null);
        EnforcementModeReader.Read(MissingRequiredInputValidator.EnforcementEnvVar).ShouldBe(EnforcementMode.Warn);
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
