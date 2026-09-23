using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// A context-window overflow is recognised from what the CLIs actually print, through the real harness folds.
///
/// <para>Every shape below is a terminal line a real CLI printed (Claude Code 2.1.226, Codex 0.147.0) when a local
/// endpoint answered its request with the provider's own over-long-prompt error body, trimmed to the fields the
/// fold reads. They are run through <c>ParseEvents</c> and <c>BuildResult</c> — the reader production uses — and the
/// cause is read off the exit reason the fold typed from the CLI's own fields, never off the error text: a text scan
/// also matched a rubric that talks about context windows, and switched model escalation off for its failed grade.</para>
///
/// <para>Why it matters: an overflow used to be an ordinary retryable failure. A retry warm-resumes, so the next
/// request carries the goal twice and overflows harder; a task-launched agent node spends all three default
/// attempts that way and ends on three identical refusals.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentContextWindowRetryTests
{
    public static TheoryData<string> ClaudeOverflowLines => new()
    {
        // Anthropic "prompt is too long: N tokens > M maximum" — the CLI's own rewording, stamped terminal_reason=prompt_too_long.
        """{"type":"result","subtype":"success","is_error":true,"num_turns":1,"terminal_reason":"prompt_too_long","api_error_status":400,"session_id":"8d014f31-0881-46c2-8311-2ad17d852c4f","result":"Prompt is too long · the request is ~215000 tokens (limit 200000) but this conversation is only ~30286 tokens — the rest is system prompt, tool definitions, and attachment content. A single-exchange conversation cannot be compacted; reduce attached files/tools or start with less context."}""",
        // Anthropic "input length and max_tokens exceed context limit".
        """{"type":"result","subtype":"success","is_error":true,"num_turns":1,"terminal_reason":"prompt_too_long","api_error_status":400,"session_id":"58110d54-f8b2-43f3-9153-dcd32a2d58dc","result":"Prompt is too long · this conversation is a single exchange and cannot be compacted — the request size comes mostly from system prompt, tool definitions, or attachments."}""",
        // An OpenAI-compatible gateway (vLLM/LiteLLM) body the CLI passes through verbatim as terminal_reason=api_error.
        """{"type":"result","subtype":"success","is_error":true,"num_turns":1,"terminal_reason":"api_error","api_error_status":400,"session_id":"6406b570-40de-4274-a586-ab044dbf5d48","result":"API Error: 400 This model's maximum context length is 131072 tokens. However, you requested 161234 tokens. Please reduce the length of the messages."}""",
    };

    public static TheoryData<string> CodexOverflowLines => new()
    {
        // OpenAI Responses API 400 context_length_exceeded.
        """{"type":"turn.failed","error":{"message":"{\"error\": {\"message\": \"Your input exceeds the context window of this model. Please adjust your input and try again.\", \"type\": \"invalid_request_error\", \"param\": \"input\", \"code\": \"context_length_exceeded\"}}"}}""",
        // The same refusal delivered as a streaming response.failed, which Codex rewords.
        """{"type":"turn.failed","error":{"message":"Codex ran out of room in the model's context window. Start a new thread or clear earlier history before retrying."}}""",
    };

    [Theory]
    [MemberData(nameof(ClaudeOverflowLines))]
    public void A_claude_context_overflow_folds_to_an_error_classified_as_one(string terminalLine)
    {
        var harness = new ClaudeCodeHarness();

        var result = harness.BuildResult(harness.ParseEvents(terminalLine), exitCode: 1, diagnostics: "");

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.ExitReason.ShouldBe(AgentTerminalOutcomeReader.ContextWindowExceededExitReason, customMessage: "the CLI's own terminal_reason / status is the evidence — typed on the exit reason, never re-read from prose");
        AgentRetryCauses.Classify(result.ExitReason, result.Error).ShouldBe(AgentRetryCauses.ContextWindowExceeded, customMessage: $"the folded error was: {result.Error}");
    }

    [Theory]
    [MemberData(nameof(CodexOverflowLines))]
    public void A_codex_context_overflow_folds_to_an_error_classified_as_one(string terminalLine)
    {
        var harness = new CodexHarness();

        var result = harness.BuildResult(harness.ParseEvents(terminalLine), exitCode: 1, diagnostics: "");

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.ExitReason.ShouldBe(AgentTerminalOutcomeReader.ContextWindowExceededExitReason);
        AgentRetryCauses.Classify(result.ExitReason, result.Error).ShouldBe(AgentRetryCauses.ContextWindowExceeded, customMessage: $"the folded error was: {result.Error}");
    }

    [Fact]
    public void Codex_refusing_an_input_past_its_own_character_cap_is_that_refusal_not_a_context_overflow()
    {
        // Codex refuses before any request, on stderr only. It is the CLI's own character cap — the condition the
        // harness preflight refuses up front — not the model's window, so "choose a larger-window model" would be
        // advice that cannot help. It folds to the same terminal code as the preflight.
        const string stderr = """Error: turn/start: turn/start failed: Input exceeds the maximum length of 1048576 characters. (code -32602), data: {"input_error_code":"input_too_large","max_chars":1048576,"actual_chars":1100000}""";

        var result = new CodexHarness().BuildResult(Array.Empty<AgentEvent>(), exitCode: 1, diagnostics: stderr);

        result.ExitReason.ShouldBe(FailureCodes.SandboxArgumentTooLong);
        result.Error.ShouldContain("Input exceeds the maximum length of 1048576 characters", customMessage: "the CLI's own sentence is the cause the author reads");
    }

    [Fact]
    public void A_rubric_that_talks_about_context_windows_does_not_switch_escalation_off()
    {
        // The regression a text scan made: a fail-closed acceptance verdict overwrites Error with the rubric's own
        // requirement and the judge's evidence. A review whose rubric is ABOUT context windows then read as an
        // overflow, and the escalation trigger — which stands down for any classified cause — stopped offering the
        // stronger model the failed grade was evidence for. A cause is typed by the harness now; prose never is one.
        const string graderProse = "The acceptance check did not pass: requirement 'returns context_length_exceeded when the input exceeds the context window' — evidence: the handler throws instead.";

        AgentRetryCauses.Classify(AgentAcceptanceContract.FailClosedExitReason, graderProse).ShouldBeNull();
        AgentModelEscalationTrigger.Reason(AgentContradiction.OverClaim, acceptanceFailed: true, acceptanceDetail: "requirement not met", workPresent: true, error: graderProse, exitReason: AgentAcceptanceContract.FailClosedExitReason)
            .ShouldNotBeNull(customMessage: "a failed grade on a claimed success is escalation evidence, whatever words the rubric uses");
    }

    [Fact]
    public void A_crashed_run_whose_last_words_mention_an_overflow_is_not_one()
    {
        // No result line: the harness reports the agent's own last message as the error. That is the agent's prose.
        var harness = new ClaudeCodeHarness();
        var lastWords = """{"type":"assistant","message":{"content":[{"type":"text","text":"The docs say a request fails with 'Prompt is too long' past the context window; let me check."}]}}""";

        var result = harness.BuildResult(harness.ParseEvents(lastWords), exitCode: 137, diagnostics: "");

        result.ExitReason.ShouldNotBe(AgentTerminalOutcomeReader.ContextWindowExceededExitReason);
        AgentRetryCauses.Classify(result.ExitReason, result.Error).ShouldBeNull(customMessage: "a crash stays a retryable crash");
    }

    [Fact]
    public void A_transient_gateway_error_whose_body_mentions_context_length_is_not_an_overflow()
    {
        // Only the model REFUSING the request (a 4xx) is an overflow; a 5xx is the gateway, and it is worth a retry.
        var harness = new ClaudeCodeHarness();
        var line = """{"type":"result","subtype":"success","is_error":true,"num_turns":1,"terminal_reason":"api_error","api_error_status":503,"session_id":"s","result":"API Error: 503 upstream timed out after prefilling; maximum context length is 131072 tokens"}""";

        var result = harness.BuildResult(harness.ParseEvents(line), exitCode: 1, diagnostics: "");

        result.ExitReason.ShouldNotBe(AgentTerminalOutcomeReader.ContextWindowExceededExitReason);
    }

    [Theory]
    [InlineData("claude exited with code 1")]
    [InlineData("patch did not apply")]
    [InlineData("API Error: 529 Overloaded")]
    [InlineData("codex exited with code 1 — stderr: npm WARN deprecated glob@7")]
    [InlineData("Prompt is too long")]
    [InlineData("the handler must return context_length_exceeded when the input exceeds the context window")]
    public void Text_alone_is_never_an_overflow(string error)
    {
        AgentRetryCauses.Classify(error).ShouldBeNull(customMessage: "only a harness-typed exit reason is an overflow; words are not");
        AgentRetryCauses.Classify("non-zero-exit", error).ShouldBeNull();
    }

    [Fact]
    public void The_overflow_exit_reason_is_pinned()
    {
        // Both harness folders stamp it and the node's retry verdict keys on it; a rename that missed either side
        // would silently turn an overflow back into three identical, billed refusals.
        AgentTerminalOutcomeReader.ContextWindowExceededExitReason.ShouldBe("context-window-exceeded");
    }

    [Fact]
    public void The_format_fault_keeps_its_own_cause()
    {
        AgentRetryCauses.Classify("API Error: 400 messages.3.content.0: Content block is not a thinking block").ShouldBe(AgentRetryCauses.GatewayFormatFault);
    }
}
