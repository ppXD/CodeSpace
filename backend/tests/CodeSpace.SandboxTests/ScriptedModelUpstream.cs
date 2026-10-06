using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeSpace.SandboxTests;

/// <summary>
/// The model a REAL coding CLI talks to in a test, placed behind the production model-credential broker as its
/// upstream. It plays one scripted agent: the first turns each call the CLI's own shell tool with one command (or, for
/// Claude, each of <see cref="ClaudeCalls"/>), and the turn after the last call's result answers with
/// <see cref="FinalText"/>. So what a test observes is what the CLI itself decided — whether its permission mode ran the
/// command, what it fed back — never the model's judgement.
///
/// <para>Two wire shapes, each the one the pinned CLI actually speaks (observed against Claude Code 2.1.263 and Codex
/// 0.142.2 with a dummy key): Anthropic Messages SSE for <c>POST …/messages</c> (a <c>tool_use</c> block, for the
/// <c>Bash</c> tool unless <see cref="ClaudeCalls"/> names another), and OpenAI Responses SSE for
/// <c>POST …/responses</c> (a <c>function_call</c> item for the shell tool the request offers). Anything else the CLI
/// sends on the side — a non-streaming side query, a token count, a model list — gets the smallest well-formed answer,
/// so it never stalls the run.</para>
/// </summary>
internal sealed class ScriptedModelUpstream(IReadOnlyList<string> commands, string finalText) : HttpMessageHandler
{
    /// <summary>The prefix every scripted tool call's id carries, followed by its step, so a later turn can count how many it has already answered and a test can find each call's result.</summary>
    internal const string ToolIdPrefix = "toolu_review_";

    /// <summary>The same for the calls of <see cref="Subagent"/>'s script, which runs in a conversation of its own.</summary>
    internal const string SubagentToolIdPrefix = "toolu_subagent_";

    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public string FinalText { get; } = finalText;

    /// <summary>
    /// Claude's script when its turns must call tools other than <c>Bash</c>: each turn calls the next of these, by the
    /// name the request offers the tool under (<c>Read</c>, <c>Skill</c>, <c>Agent</c>). For what a shell command cannot
    /// reach — memory the CLI attaches when its own Read tool opens a file, a skill or an agent only the CLI invokes. Null
    /// scripts each command as a <c>Bash</c> call; the Responses wire always runs the commands. A call the request does
    /// not offer is never made: the turn is refused (<see cref="Unscriptable"/>), so a CLI release that renames a tool
    /// fails the run at once, naming the tool, instead of quietly answering a call it no longer has.
    /// </summary>
    public IReadOnlyList<ScriptedToolCall>? ClaudeCalls { get; init; }

    /// <summary>
    /// The script of a subagent the main loop starts (with an <c>Agent</c> call in <see cref="ClaudeCalls"/>): a turn whose
    /// system prompt holds its marker is one of that subagent's, and calls the next of its calls, then answers. Its
    /// conversation holds none of the main loop's calls, nor the main loop's its, so each counts its own.
    /// </summary>
    public ScriptedSubagent? Subagent { get; init; }

    /// <summary>
    /// Whether the permission classifier's side query — the one that carries the run's <c>&lt;transcript&gt;</c> — gets the
    /// verdict that lets the call through (<c>&lt;block&gt;no&lt;/block&gt;</c>), so a plan-mode run may start a read-only
    /// subagent. Off, the classifier gets no verdict it can parse and the CLI refuses the call, which other arms rely on.
    /// </summary>
    public bool ClassifierAllows { get; init; }

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

        if (path.EndsWith("/messages", StringComparison.Ordinal)) return MessagesTurn(json, body);

        return Json("""{"input_tokens":11}""");
    }

    private HttpResponseMessage MessagesTurn(JsonObject? request, string body)
    {
        var model = Text(request?["model"]) ?? "scripted-model";
        var hasTools = request?["tools"] is JsonArray { Count: > 0 };
        var streaming = request?["stream"] is JsonValue stream && stream.TryGetValue<bool>(out var on) && on;

        // A side query (no tools: a title, a summary, a classifier's verdict) answers plainly; only the main loop runs the script.
        if (!hasTools) return SideAnswer(model, streaming, ClassifierAllows && body.Contains("<transcript>", StringComparison.Ordinal) ? "<block>no</block>" : "ok");

        var (script, prefix) = Subagent is { } subagent && (request!["system"]?.ToJsonString() ?? "").Contains(subagent.SystemMarker, StringComparison.Ordinal) ? (subagent.Calls, SubagentToolIdPrefix) : (ClaudeCalls ?? commands.Select(BashCall).ToList(), ToolIdPrefix);
        var step = AnsweredToolCalls(request!["messages"] as JsonArray, prefix);

        if (step < script.Count)
        {
            var call = script[step];
            var offered = ToolNames(request["tools"] as JsonArray);

            if (!offered.Contains(call.Name)) return Unscriptable($"The CLI offered no {call.Name} tool for this script to call; it offered: {string.Join(", ", offered)}");

            return streaming ? Sse(AnthropicToolUse(model, prefix + step, call)) : Json(AnthropicMessage(model, new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = prefix + step, ["name"] = call.Name, ["input"] = call.Input.DeepClone() }), "tool_use"));
        }

        return streaming ? Sse(AnthropicText(model, FinalText)) : Json(AnthropicMessage(model, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = FinalText }), "end_turn"));
    }

    private static HttpResponseMessage SideAnswer(string model, bool streaming, string text) =>
        streaming ? Sse(AnthropicText(model, text)) : Json(AnthropicMessage(model, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), "end_turn"));

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
        var names = ToolNames(tools);

        if (names.Contains("exec_command")) return ("exec_command", new JsonObject { ["cmd"] = command });

        if (names.Contains("shell")) return ("shell", new JsonObject { ["command"] = new JsonArray("bash", "-lc", command) });

        throw new InvalidOperationException($"The CLI offered no shell tool this script knows how to call; it offered: {string.Join(", ", names)}");
    }

    /// <summary>The name of every tool the request offers the model.</summary>
    private static HashSet<string> ToolNames(JsonArray? tools) => tools?.Select(tool => Text(tool?["name"])).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];

    private static ScriptedToolCall BashCall(string command) => new("Bash", new JsonObject { ["command"] = command, ["description"] = "Read the change under review" });

    private static int AnsweredToolCalls(JsonArray? messages, string prefix) =>
        messages?.Count(message => Text(message?["role"]) == "assistant" && message!["content"] is JsonArray blocks && blocks.Any(block => Text(block?["type"]) == "tool_use" && Text(block!["id"])?.StartsWith(prefix, StringComparison.Ordinal) == true)) ?? 0;

    private static IEnumerable<(string Event, JsonObject Data)> AnthropicToolUse(string model, string id, ScriptedToolCall call)
    {
        yield return AnthropicStart(model);
        yield return ("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = call.Name, ["input"] = new JsonObject() } });
        yield return ("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = call.Input.ToJsonString() } });
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

    /// <summary>A request the script cannot answer, refused the way the Messages API refuses a malformed one: a 400 the CLI does not retry and prints with its message, so the run fails at once and its output says why.</summary>
    private static HttpResponseMessage Unscriptable(string message) =>
        new(HttpStatusCode.BadRequest) { Content = new StringContent(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "invalid_request_error", ["message"] = message } }.ToJsonString(), Encoding.UTF8, "application/json") };

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

/// <summary>One call a scripted Claude turn makes: the CLI tool's name as the request offers it, and the tool's input.</summary>
internal sealed record ScriptedToolCall(string Name, JsonObject Input);

/// <summary>A subagent's script: the text its system prompt carries, which tells its turns from the main loop's, and the calls it makes.</summary>
internal sealed record ScriptedSubagent(string SystemMarker, IReadOnlyList<ScriptedToolCall> Calls);
