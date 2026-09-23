using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// A context-window overflow is recognised from what the CLIs actually print, through the real harness folds.
///
/// <para>Every shape below is a terminal line a real CLI printed (Claude Code 2.1.226, Codex 0.147.0) when a local
/// endpoint answered its request with the provider's own over-long-prompt error body, trimmed to the fields the
/// fold reads. They are run through <c>ParseEvents</c> and <c>BuildResult</c> — the reader production uses — and only
/// then classified, so the markers are pinned against the text a failed run really carries, not against strings
/// written to match them.</para>
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
        AgentRetryCauses.Classify(result.Error).ShouldBe(AgentRetryCauses.ContextWindowExceeded, customMessage: $"the folded error was: {result.Error}");
    }

    [Theory]
    [MemberData(nameof(CodexOverflowLines))]
    public void A_codex_context_overflow_folds_to_an_error_classified_as_one(string terminalLine)
    {
        var harness = new CodexHarness();

        var result = harness.BuildResult(harness.ParseEvents(terminalLine), exitCode: 1, diagnostics: "");

        result.Status.ShouldBe(AgentRunStatus.Failed);
        AgentRetryCauses.Classify(result.Error).ShouldBe(AgentRetryCauses.ContextWindowExceeded, customMessage: $"the folded error was: {result.Error}");
    }

    [Fact]
    public void Codex_refusing_an_input_past_its_own_character_cap_is_classified_the_same_way()
    {
        // Codex refuses before any request, on stderr only — so the only carrier is the diagnostics excerpt the fold
        // appends to a bare exit. Pinned against codex 0.142.2 (the worker's version) as well as 0.147.0.
        const string stderr = """Error: turn/start: turn/start failed: Input exceeds the maximum length of 1048576 characters. (code -32602), data: {"input_error_code":"input_too_large","max_chars":1048576,"actual_chars":1100000}""";

        var result = new CodexHarness().BuildResult(Array.Empty<AgentEvent>(), exitCode: 1, diagnostics: stderr);

        AgentRetryCauses.Classify(result.Error).ShouldBe(AgentRetryCauses.ContextWindowExceeded, customMessage: $"the folded error was: {result.Error}");
    }

    [Theory]
    [InlineData("claude exited with code 1")]
    [InlineData("patch did not apply")]
    [InlineData("API Error: 529 Overloaded")]
    [InlineData("codex exited with code 1 — stderr: npm WARN deprecated glob@7")]
    public void An_ordinary_failure_is_not_mistaken_for_an_overflow(string error)
    {
        AgentRetryCauses.Classify(error).ShouldBeNull(customMessage: "an ordinary death keeps the default resume-and-retry semantics");
    }

    [Fact]
    public void The_format_fault_keeps_its_own_cause()
    {
        AgentRetryCauses.Classify("API Error: 400 messages.3.content.0: Content block is not a thinking block").ShouldBe(AgentRetryCauses.GatewayFormatFault);
    }
}
