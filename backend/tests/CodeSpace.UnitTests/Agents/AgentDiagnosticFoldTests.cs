using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: a stderr-only death. Both production CLIs can die WITHOUT saying anything on the JSON protocol stream —
/// a plain-text fatal on stderr and a non-zero exit — and both parsers drop non-JSON lines by construction, so the
/// fold reaches its bare-exit rung with nothing to report. Pins that the rung now folds the process's own last words
/// in, that it does so ONLY there (a protocol-derived error or final message still wins, unchanged), that the
/// excerpt is bounded and persistable, and that the resulting text is what a cause-aware retry can classify — which
/// is the whole reason the missing text mattered rather than merely reading badly.
/// </summary>
[Trait("Category", "Unit")]
public class AgentDiagnosticFoldTests
{
    private static IAgentHarness HarnessFor(string kind) => kind == ClaudeCodeHarness.HarnessKind ? new ClaudeCodeHarness() : new CodexHarness();

    [Theory]
    [InlineData(ClaudeCodeHarness.HarnessKind, "claude exited with code 1")]
    [InlineData(CodexHarness.HarnessKind, "codex exited with code 1")]
    public void A_stderr_only_fatal_reaches_the_error_instead_of_a_bare_exit_code(string kind, string exitText)
    {
        // The gap this closes: no Error event, no final message — today's fold could only name the number.
        var result = HarnessFor(kind).BuildResult(Array.Empty<AgentEvent>(), exitCode: 1, diagnostics: "node:internal/errors: ENOENT config\n");

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.Error.ShouldBe($"{exitText} — stderr: node:internal/errors: ENOENT config");
    }

    [Theory]
    [InlineData(ClaudeCodeHarness.HarnessKind, "claude exited with code 1")]
    [InlineData(CodexHarness.HarnessKind, "codex exited with code 1")]
    public void A_silent_stderr_leaves_todays_exit_text_exactly_as_it_was(string kind, string exitText)
    {
        HarnessFor(kind).BuildResult(Array.Empty<AgentEvent>(), exitCode: 1, diagnostics: "  \n\n").Error.ShouldBe(exitText, "nothing was said, so there is nothing to fold — never an empty marker");
    }

    [Theory]
    [InlineData(ClaudeCodeHarness.HarnessKind)]
    [InlineData(CodexHarness.HarnessKind)]
    public void A_protocol_error_still_wins_and_is_not_diluted_by_the_diagnostics(string kind)
    {
        var events = new[] { new AgentEvent { Kind = AgentEventKind.Error, Text = "patch did not apply" } };

        HarnessFor(kind).BuildResult(events, exitCode: 1, diagnostics: "npm WARN deprecated glob@7").Error.ShouldBe("patch did not apply", "the CLI said why on its own stream; routine stderr noise must not be appended to it");
    }

    [Theory]
    [InlineData(ClaudeCodeHarness.HarnessKind)]
    [InlineData(CodexHarness.HarnessKind)]
    public void The_cli_final_message_still_wins_over_the_diagnostics(string kind)
    {
        var events = new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "could not reach the provider" } };

        HarnessFor(kind).BuildResult(events, exitCode: 1, diagnostics: "npm WARN deprecated glob@7").Error.ShouldBe("could not reach the provider");
    }

    [Theory]
    [InlineData(ClaudeCodeHarness.HarnessKind)]
    [InlineData(CodexHarness.HarnessKind)]
    public void A_successful_run_reports_no_error_however_much_the_process_wrote_on_stderr(string kind)
    {
        var events = new[] { new AgentEvent { Kind = AgentEventKind.FinalSummary, Text = "done" } };

        var result = HarnessFor(kind).BuildResult(events, exitCode: 0, diagnostics: "npm WARN deprecated glob@7");

        result.Status.ShouldBe(AgentRunStatus.Succeeded);
        result.Error.ShouldBeNull("stderr is diagnostics, not a verdict — a clean exit stays clean");
    }

    [Fact]
    public void A_gateway_format_fault_seen_only_on_stderr_still_classifies_as_one()
    {
        // The consequence that matters. Without the fold this text reads "claude exited with code 1", Classify
        // returns null, and the format-fault respawn (fresh conversation + thinking disabled) never fires for a
        // death whose ONLY witness was stderr.
        var error = new ClaudeCodeHarness().BuildResult(Array.Empty<AgentEvent>(), exitCode: 1, diagnostics: "API Error: Content block is not a thinking block\n").Error;

        AgentRetryCauses.Classify(error).ShouldBe(AgentRetryCauses.GatewayFormatFault);
    }

    [Fact]
    public void Only_the_last_few_non_empty_lines_are_folded_in()
    {
        var diagnostics = string.Join('\n', new[] { "boot", "", "one", "two", "three", "" });

        AgentDiagnosticExcerpt.Explain("exit", diagnostics).ShouldBe("exit — stderr: one\ntwo\nthree", "the tail is where a dying process says why; the rest stays on the run's spool");
    }

    [Fact]
    public void The_excerpt_is_capped_in_characters_and_keeps_the_end()
    {
        var line = new string('x', AgentDiagnosticExcerpt.MaxChars * 3) + "FATAL";

        var error = AgentDiagnosticExcerpt.Explain("exit", line);

        error.Length.ShouldBeLessThanOrEqualTo("exit".Length + AgentDiagnosticExcerpt.Separator.Length + AgentDiagnosticExcerpt.MaxChars + 1);
        error.ShouldEndWith("FATAL", customMessage: "a truncated excerpt must keep the END — the same half the runner's own tail keeps, and where the fatal lands");
    }

    [Fact]
    public void The_capped_excerpt_never_splits_an_emoji_straddling_the_cut()
    {
        // The excerpt keeps the TAIL, so an astral char (a surrogate PAIR) whose LOW surrogate lands exactly on the
        // cut boundary must be dropped WHOLE, not split — a lone low surrogate is invalid UTF-16 (mirrors
        // AgentMetricsReader.Truncate's own back-off on the opposite, keep-the-head boundary).
        var diagnostics = "🚀" + new string('x', AgentDiagnosticExcerpt.MaxChars - 1);

        var error = AgentDiagnosticExcerpt.Explain("exit", diagnostics);

        HasUnpairedSurrogate(error).ShouldBeFalse();
    }

    [Fact]
    public void A_nul_byte_on_the_fatal_line_is_stripped_so_the_error_can_be_persisted()
    {
        // Postgres text refuses U+0000 outright: an unsanitized excerpt would take the whole completion transaction
        // with it, failing the run for a reason unrelated to its work (see PersistedText).
        var error = new CodexHarness().BuildResult(Array.Empty<AgentEvent>(), exitCode: 1, diagnostics: "fat\0al: bad config\n");

        error.Error.ShouldBe("codex exited with code 1 — stderr: fatal: bad config");
        error.Error!.ShouldNotContain("\0");
    }

    [Fact]
    public void The_stderr_marker_is_pinned()
    {
        // What a reader scans for, and what these tests key on. A silent rename would leave every assertion about
        // "the run said why" matching nothing (Rule 8).
        AgentDiagnosticExcerpt.Separator.ShouldBe(" — stderr: ");
        AgentDiagnosticExcerpt.MaxLines.ShouldBe(3);
    }

    [Fact]
    public void A_maximal_folded_error_still_fits_the_card_that_renders_it()
    {
        // AgentMetricsReader caps the journal card's error at 400 chars and truncates from the FRONT, so a ceiling
        // raised here would silently cut the folded reason off the one surface an operator reads it on. Pin the
        // relationship, not just the number: whoever raises MaxChars has to see this.
        var maximal = new ClaudeCodeHarness().BuildResult(Array.Empty<AgentEvent>(), exitCode: 137, diagnostics: new string('x', AgentDiagnosticExcerpt.MaxChars * 4));

        maximal.Error!.Length.ShouldBeLessThanOrEqualTo(400, "the whole folded error — exit text, separator and excerpt — has to clear AgentMetricsReader's card cap");
    }

    private static bool HasUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return true;
            }
            else if (char.IsLowSurrogate(value[i])) return true;
        }

        return false;
    }
}
