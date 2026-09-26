using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeSpace.SandboxTests;

/// <summary>
/// The model a REAL coding CLI talks to in a test, placed behind the production model-credential broker as its
/// upstream. It plays one scripted agent: the first turns each call the CLI's own shell tool with one command, and
/// the turn after the last command's result answers with <see cref="FinalText"/>. So what a test observes is what the
/// CLI itself decided — whether its permission mode ran the command, what it fed back — never the model's judgement.
///
/// <para>Two wire shapes, each the one the pinned CLI actually speaks (observed against Claude Code 2.1.263 and Codex
/// 0.142.2 with a dummy key): Anthropic Messages SSE for <c>POST …/messages</c> (a <c>tool_use</c> block for the
/// <c>Bash</c> tool), and OpenAI Responses SSE for <c>POST …/responses</c> (a <c>function_call</c> item for the shell
/// tool the request offers). Anything else the CLI sends on the side — a non-streaming side query, a token count, a
/// model list — gets the smallest well-formed answer, so it never stalls the run.</para>
/// </summary>
internal sealed class ScriptedModelUpstream(IReadOnlyList<string> commands, string finalText) : HttpMessageHandler
{
    /// <summary>The prefix every scripted tool call's id carries, so a later turn can count how many it has already answered.</summary>
    private const string ToolIdPrefix = "toolu_review_";

    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public string FinalText { get; } = finalText;

    /// <summary>Every request the broker relayed, in arrival order — the model-side ground truth of what the CLI sent.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var path = request.RequestUri?.AbsolutePath ?? "";

        _requests.Enqueue(new RecordedRequest(request.Method.Method, path, body));

        if (request.Method == HttpMethod.Get) return Json("""{"models":[],"data":[]}""");

        var json = TryParse(body);

        if (path.EndsWith("/responses", StringComparison.Ordinal)) return ResponsesTurn(json);

        if (path.EndsWith("/messages", StringComparison.Ordinal)) return MessagesTurn(json);

        return Json("""{"input_tokens":11}""");
    }

    private HttpResponseMessage MessagesTurn(JsonObject? request)
    {
        var model = Text(request?["model"]) ?? "scripted-model";
        var hasTools = request?["tools"] is JsonArray { Count: > 0 };
        var streaming = request?["stream"] is JsonValue stream && stream.TryGetValue<bool>(out var on) && on;

        // A side query (no tools: a title, a summary) answers plainly; only the main loop runs the script.
        if (!hasTools) return streaming ? Sse(AnthropicText(model, "ok")) : Json(AnthropicMessage(model, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok" }), "end_turn"));

        var step = AnsweredToolCalls(request!["messages"] as JsonArray);

        if (step < commands.Count)
        {
            var input = new JsonObject { ["command"] = commands[step], ["description"] = "Read the change under review" };

            return streaming ? Sse(AnthropicToolUse(model, step, input)) : Json(AnthropicMessage(model, new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = ToolIdPrefix + step, ["name"] = "Bash", ["input"] = input }), "tool_use"));
        }

        return streaming ? Sse(AnthropicText(model, FinalText)) : Json(AnthropicMessage(model, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = FinalText }), "end_turn"));
    }

    private HttpResponseMessage ResponsesTurn(JsonObject? request)
    {
        var answered = (request?["input"] as JsonArray)?.Count(item => Text(item?["type"]) == "function_call_output") ?? 0;

        if (answered >= commands.Count) return Sse(ResponsesFinal(answered));

        var (tool, arguments) = ShellCall(request?["tools"] as JsonArray, commands[answered]);

        return Sse(ResponsesToolCall(answered, tool, arguments));
    }

    /// <summary>The CLI's own shell tool and its argument shape, read from the tools the request offers rather than assumed — a CLI release that renames it fails loudly here instead of silently not running the command.</summary>
    private static (string Tool, JsonObject Arguments) ShellCall(JsonArray? tools, string command)
    {
        var names = tools?.Select(tool => Text(tool?["name"])).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];

        if (names.Contains("exec_command")) return ("exec_command", new JsonObject { ["cmd"] = command });

        if (names.Contains("shell")) return ("shell", new JsonObject { ["command"] = new JsonArray("bash", "-lc", command) });

        throw new InvalidOperationException($"The CLI offered no shell tool this script knows how to call; it offered: {string.Join(", ", names)}");
    }

    private static int AnsweredToolCalls(JsonArray? messages) =>
        messages?.Count(message => Text(message?["role"]) == "assistant" && message!["content"] is JsonArray blocks && blocks.Any(block => Text(block?["type"]) == "tool_use" && Text(block!["id"])?.StartsWith(ToolIdPrefix, StringComparison.Ordinal) == true)) ?? 0;

    private static IEnumerable<(string Event, JsonObject Data)> AnthropicToolUse(string model, int step, JsonObject input)
    {
        yield return AnthropicStart(model);
        yield return ("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = ToolIdPrefix + step, ["name"] = "Bash", ["input"] = new JsonObject() } });
        yield return ("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = input.ToJsonString() } });
        yield return ("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 });
        yield return ("message_delta", new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "tool_use", ["stop_sequence"] = null }, ["usage"] = new JsonObject { ["output_tokens"] = 7 } });
        yield return ("message_stop", new JsonObject { ["type"] = "message_stop" });
    }

    private static IEnumerable<(string Event, JsonObject Data)> AnthropicText(string model, string text)
    {
        yield return AnthropicStart(model);
        yield return ("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } });
        yield return ("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text } });
        yield return ("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 });
        yield return ("message_delta", new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "end_turn", ["stop_sequence"] = null }, ["usage"] = new JsonObject { ["output_tokens"] = 5 } });
        yield return ("message_stop", new JsonObject { ["type"] = "message_stop" });
    }

    private static (string Event, JsonObject Data) AnthropicStart(string model) =>
        ("message_start", new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = "msg_" + Guid.NewGuid().ToString("N"), ["type"] = "message", ["role"] = "assistant", ["model"] = model, ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null, ["usage"] = new JsonObject { ["input_tokens"] = 11, ["output_tokens"] = 1 } } });

    private static string AnthropicMessage(string model, JsonArray content, string stopReason) =>
        new JsonObject { ["id"] = "msg_" + Guid.NewGuid().ToString("N"), ["type"] = "message", ["role"] = "assistant", ["model"] = model, ["content"] = content, ["stop_reason"] = stopReason, ["stop_sequence"] = null, ["usage"] = new JsonObject { ["input_tokens"] = 11, ["output_tokens"] = 5 } }.ToJsonString();

    private static IEnumerable<(string Event, JsonObject Data)> ResponsesToolCall(int turn, string tool, JsonObject arguments)
    {
        var item = new JsonObject { ["type"] = "function_call", ["id"] = $"fc_{turn}", ["call_id"] = $"call_{turn}", ["name"] = tool, ["arguments"] = arguments.ToJsonString(), ["status"] = "completed" };
        var added = new JsonObject { ["type"] = "function_call", ["id"] = $"fc_{turn}", ["call_id"] = $"call_{turn}", ["name"] = tool, ["arguments"] = "", ["status"] = "in_progress" };

        yield return ("response.created", new JsonObject { ["type"] = "response.created", ["response"] = new JsonObject { ["id"] = $"resp_{turn}" } });
        yield return ("response.output_item.added", new JsonObject { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = added });
        yield return ("response.function_call_arguments.delta", new JsonObject { ["type"] = "response.function_call_arguments.delta", ["item_id"] = $"fc_{turn}", ["output_index"] = 0, ["delta"] = arguments.ToJsonString() });
        yield return ("response.output_item.done", new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = item });
        yield return ("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["id"] = $"resp_{turn}", ["usage"] = ResponsesUsage() } });
    }

    private IEnumerable<(string Event, JsonObject Data)> ResponsesFinal(int turn)
    {
        var item = new JsonObject { ["type"] = "message", ["role"] = "assistant", ["id"] = $"msg_{turn}", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = FinalText }) };

        yield return ("response.created", new JsonObject { ["type"] = "response.created", ["response"] = new JsonObject { ["id"] = $"resp_{turn}" } });
        yield return ("response.output_item.done", new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = item });
        yield return ("response.completed", new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["id"] = $"resp_{turn}", ["usage"] = ResponsesUsage() } });
    }

    private static JsonObject ResponsesUsage() => new() { ["input_tokens"] = 10, ["input_tokens_details"] = null, ["output_tokens"] = 5, ["output_tokens_details"] = null, ["total_tokens"] = 15 };

    private static HttpResponseMessage Sse(IEnumerable<(string Event, JsonObject Data)> events)
    {
        var text = new StringBuilder();

        foreach (var (name, data) in events) text.Append("event: ").Append(name).Append('\n').Append("data: ").Append(data.ToJsonString()).Append("\n\n");

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject? TryParse(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One request the broker relayed to the scripted model.</summary>
internal sealed record RecordedRequest(string Method, string Path, string Body);
