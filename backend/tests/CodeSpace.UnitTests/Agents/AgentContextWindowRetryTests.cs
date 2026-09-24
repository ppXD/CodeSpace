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
/// <para>The shapes below are terminal lines real CLIs printed (Claude Code 2.1.226 and the pinned 2.1.263, Codex
/// 0.147.0 and the pinned 0.142.2) when a local endpoint answered with a provider's own error body, trimmed to the
/// fields the fold reads — except where a case says it is representative or synthesized. They are run through <c>ParseEvents</c> and <c>BuildResult</c> — the reader production uses — and the
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
        // The CLI's own local refusal: its token estimate is past the window, so it sent no request at all (pinned 2.1.263, a 1.2 MB goal).
        """{"type":"result","subtype":"success","is_error":true,"num_turns":1,"terminal_reason":"blocking_limit","api_error_status":null,"session_id":"ff8b4b95-8351-4462-ada3-c302b4f526d8","result":"Prompt is too long"}""",
    };

    public static TheoryData<string, string> ClaudeConnectionFailureLines => new()
    {
        // Pinned 2.1.263 against a dead port and a peer that resets: the status is null because no answer ever came.
        { """{"type":"result","subtype":"success","is_error":true,"num_turns":1,"terminal_reason":"api_error","api_error_status":null,"session_id":"54818d4e-66e4-4f1b-b3ee-73de94f3b9cc","result":"API Error: Connection refused — a firewall or proxy may be blocking it (ConnectionRefused)"}""", "API Error: Connection refused — a firewall or proxy may be blocking it (ConnectionRefused)" },
        { """{"type":"result","subtype":"success","is_error":true,"num_turns":1,"terminal_reason":"api_error","api_error_status":null,"session_id":"f01a6528-1c83-4400-af5d-4ac6d322c7f9","result":"API Error: Connection dropped (ECONNRESET)"}""", "API Error: Connection dropped (ECONNRESET)" },
    };

    public static TheoryData<string> CodexOverflowLines => new()
    {
        // OpenAI Responses API 400 context_length_exceeded.
        """{"type":"turn.failed","error":{"message":"{\"error\": {\"message\": \"Your input exceeds the context window of this model. Please adjust your input and try again.\", \"type\": \"invalid_request_error\", \"param\": \"input\", \"code\": \"context_length_exceeded\"}}"}}""",
        // The same refusal delivered as a streaming response.failed, which Codex rewords.
        """{"type":"turn.failed","error":{"message":"Codex ran out of room in the model's context window. Start a new thread or clear earlier history before retrying."}}""",
        // A vLLM gateway's refusal, relayed verbatim by the pinned 0.142.2: the code is the number 400, the reason only in the message.
        """{"type":"turn.failed","error":{"message":"{\"error\": {\"message\": \"This model's maximum context length is 131072 tokens. However, you requested 161234 tokens (161234 in the messages, 0 in the completion). Please reduce the length of the messages or completion.\", \"type\": \"BadRequestError\", \"param\": null, \"code\": 400}}"}}""",
        // A LiteLLM proxy in front of a Claude model: its class name and Anthropic's own words, neither OpenAI-shaped.
        // Representative of LiteLLM's exception mapping (not byte-captured from a live proxy), relayed the way 0.142.2 relays any 400 body.
        """{"type":"turn.failed","error":{"message":"{\"error\": {\"message\": \"litellm.ContextWindowExceededError: litellm.BadRequestError: AnthropicException - {\\\"type\\\":\\\"error\\\",\\\"error\\\":{\\\"type\\\":\\\"invalid_request_error\\\",\\\"message\\\":\\\"prompt is too long: 215000 tokens > 200000 maximum\\\"}}\", \"type\": null, \"param\": null, \"code\": \"400\"}}"}}""",
        // A LiteLLM proxy's refusal, relayed verbatim by the pinned 0.142.2: the code is the string "400".
        """{"type":"turn.failed","error":{"message":"{\"error\": {\"message\": \"litellm.ContextWindowExceededError: litellm.BadRequestError: ContextWindowExceededError: OpenAIException - Error code: 400 - {'error': {'message': \\\"This model's maximum context length is 128000 tokens. However, your messages resulted in 161234 tokens.\\\", 'type': 'invalid_request_error', 'param': 'messages', 'code': 'context_length_exceeded'}}\", \"type\": null, \"param\": null, \"code\": \"400\"}}"}}""",
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

    [Theory]
    [MemberData(nameof(ClaudeConnectionFailureLines))]
    public void A_claude_connection_failure_folds_to_its_own_cause(string terminalLine, string cliError)
    {
        // The status is null when no answer came. Reading it as a number threw out of the fold, so the run landed as
        // executor-error with a .NET message, and its diff, transcript and session were never captured.
        var harness = new ClaudeCodeHarness();

        var result = harness.BuildResult(harness.ParseEvents(terminalLine), exitCode: 1, diagnostics: "");

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.ExitReason.ShouldBe("non-zero-exit");
        result.Error.ShouldBe(cliError);
        result.SessionId.ShouldNotBeNullOrEmpty(customMessage: "the fold completed, so the session a retry resumes is still there");
    }

    public static TheoryData<string> CodexNonRefusalLines => new()
    {
        // What the pinned 0.142.2 really prints for a status it does not relay verbatim: a 503, 413 and 422 whose bodies
        // name the window. It rewraps them as prose, so they are not a relayed refusal, and a 5xx is worth a retry.
        """{"type":"turn.failed","error":{"message":"unexpected status 503 Service Unavailable: upstream prefill timed out; maximum context length is 131072 tokens, url: http://127.0.0.1:19703/v1/responses"}}""",
        """{"type":"turn.failed","error":{"message":"unexpected status 413 Payload Too Large: This model's maximum context length is 131072 tokens. However, you requested 161234 tokens., url: http://127.0.0.1:19719/v1/responses"}}""",
        """{"type":"turn.failed","error":{"message":"unexpected status 422 Unprocessable Entity: This model's maximum context length is 131072 tokens. However, you requested 161234 tokens., url: http://127.0.0.1:19891/v1/responses"}}""",
        // Belt and braces for the 5xx guard: a JSON body with a 5xx code, which the pin does not print today.
        """{"type":"turn.failed","error":{"message":"{\"error\": {\"message\": \"upstream timed out after prefilling; maximum context length is 131072 tokens\", \"type\": \"ServiceUnavailableError\", \"code\": 503}}"}}""",
    };

    [Theory]
    [MemberData(nameof(CodexNonRefusalLines))]
    public void A_codex_failure_that_is_not_a_relayed_refusal_is_not_an_overflow_whatever_it_mentions(string line)
    {
        var harness = new CodexHarness();

        var result = harness.BuildResult(harness.ParseEvents(line), exitCode: 1, diagnostics: "");

        result.ExitReason.ShouldBe("non-zero-exit");
    }

    public static TheoryData<string, string> CodexUnreadableBodyLines => new()
    {
        // The pinned 0.142.2 relaying a gateway 400 whose message holds an unpaired surrogate escape (a preview cut in
        // the middle of an emoji). The body parses and then throws on read; the rest of it still says what it says.
        { """{"type":"turn.failed","error":{"message":"{\"error\": {\"message\": \"Invalid value for input[0]: preview \\ud83d ... (truncated)\", \"type\": \"BadRequestError\", \"param\": null, \"code\": 400}}"}}""", "non-zero-exit" },
        { """{"type":"turn.failed","error":{"message":"{\"error\": {\"message\": \"This model's maximum context length is 131072 tokens. However, you requested 161234 tokens. Prompt starts: \\ud83d\", \"type\": \"BadRequestError\", \"param\": null, \"code\": 400}}"}}""", AgentTerminalOutcomeReader.ContextWindowExceededExitReason },
        // OpenAI's typed code beside an unreadable message, as the pin relays it: the code alone settles it.
        { """{"type":"turn.failed","error":{"message":"{\"error\": {\"message\": \"Your input exceeds the context window of this model. Input preview: \\ud83d\", \"type\": \"invalid_request_error\", \"param\": \"input\", \"code\": \"context_length_exceeded\"}}"}}""", AgentTerminalOutcomeReader.ContextWindowExceededExitReason },

    };

    [Theory]
    [MemberData(nameof(CodexUnreadableBodyLines))]
    public void A_relayed_body_that_is_not_valid_text_never_throws_and_still_says_what_it_says(string failedLine, string expectedExitReason)
    {
        // A throw here lands the run as executor-error and drops its session, diff and transcript — the fold is never
        // the place that happens. And one bad character must not hide a refusal the rest of the body states plainly.
        var harness = new CodexHarness();
        var events = harness.ParseEvents("""{"type":"thread.started","thread_id":"01a0d18d-0000-7000-8000-000000000001"}""").Concat(harness.ParseEvents(failedLine)).ToList();

        var result = harness.BuildResult(events, exitCode: 1, diagnostics: "");

        result.ExitReason.ShouldBe(expectedExitReason);
        result.SessionId.ShouldNotBeNullOrEmpty(customMessage: "the fold completed, so the thread a retry resumes is kept");
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
