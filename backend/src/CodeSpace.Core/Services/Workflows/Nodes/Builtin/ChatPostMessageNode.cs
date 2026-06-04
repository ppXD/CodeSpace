using System.Text.Json;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Chat.Interactions;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Posts a message into a conversation AS the team's CodeSpace bot (via <see cref="IChatBotService"/>,
/// which derives the team from the conversation + auto-joins the bot). With an <c>actions</c> or
/// <c>form</c> input it posts an interactive CARD and — by default (<c>waitForResponse</c>) — PARKS the
/// run until someone responds, surfacing their choice as THIS node's own outputs
/// <c>{ action, by, comment, values }</c>. So the common "ask a question, branch on the answer" is ONE
/// node: no separate <c>flow.wait_action</c> needed.
///
/// Turn <c>waitForResponse</c> off to post-and-continue (fire-and-forget), or to wait ELSEWHERE — then it
/// outputs the <c>token</c> instead, which you wire into a <c>flow.wait_action</c> (kept for advanced
/// topologies: wait in another branch, fan-in / quorum). Without an interaction it's a plain announcement.
///
/// The wait reuses the engine's generic suspend/resume: the first pass posts + returns
/// <c>Suspend(Action, token)</c>; a click resumes the SAME node with the decision as its
/// <c>ResumePayload</c> (the <c>ResumePayload</c> guard means the card is posted exactly once).
/// Side-effecting (a permanent post).
/// </summary>
public sealed class ChatPostMessageNode : INodeRuntime
{
    private readonly IChatBotService _bot;
    private readonly IInteractionComponentRegistry _components;

    public ChatPostMessageNode(IChatBotService bot, IInteractionComponentRegistry components)
    {
        _bot = bot;
        _components = components;
    }

    public string TypeKey => "chat.post_message";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Post chat message",
        Category = "Chat",
        Kind = NodeKind.Regular,
        IconKey = "message-square",
        Description = "Posts a message into a conversation as the CodeSpace bot. Add `actions` (buttons) or a `form` to make it interactive — by default it then WAITS here for the response and outputs the choice as { action, by, comment, values }, so the next node uses it directly. Turn off 'Wait for response' to post-and-continue or to wait elsewhere with a flow.wait_action wired to `token`.",
        IsSideEffecting = true,
        WaitOutputs = new WaitOutputsSpec
        {
            OutputKeys = ["action", "by", "comment", "values"],
            WaitConfigKey = "waitForResponse",
            WaitConfigDefault = true,
            WaitConfigLabel = "Wait for a response",
        },
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "waitForResponse": { "type": "boolean", "default": true, "title": "Wait for a response", "description": "Pause here until someone responds, and surface their choice as this node's outputs. Off = post and continue. Ignored for a plain message." },
                "resolve": {
                  "type": "object",
                  "title": "Decision rule",
                  "description": "How responses decide the wait. Default: the first response wins.",
                  "properties": {
                    "mode": { "type": "string", "enum": ["first","quorum"], "default": "first", "x-enumLabels": { "first": "First response wins", "quorum": "Quorum — N of the same" }, "description": "A button marked `vetoes` always decides on one click, regardless of this." },
                    "count": { "type": "integer", "minimum": 1, "default": 2, "title": "Responders needed", "description": "For quorum: how many DISTINCT people must pick the same option." }
                  }
                }
              }
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "conversationId": { "type": "string", "format": "uuid", "x-selector": "conversation", "description": "Target conversation. Pick a channel, or switch to Expression to bind it dynamically (e.g. {{trigger.channelId}})." },
                "body": { "type": "string", "minLength": 1, "description": "The message text. Supports {{ }} references." },
                "actions": {
                  "type": "array",
                  "description": "Action buttons — each item renders as a clickable button. The clicked button's `key` is surfaced as `action` (on THIS node when 'Wait for response' is on, otherwise on a downstream flow.wait_action). Leave empty to post a plain message. (One interaction component; `form` is the other.)",
                  "items": {
                    "type": "object",
                    "properties": {
                      "key": { "type": "string", "title": "Action key", "description": "The signal surfaced as outputs.action when clicked — wire it downstream (e.g. \"approve\" → a git.pr_review verdict)." },
                      "label": { "type": "string", "description": "Button text shown to the responder." },
                      "description": { "type": "string", "x-advanced": true, "description": "Optional: what this button does. Shown to the responder as a tooltip on the button, so a click's effect isn't opaque." },
                      "style": { "type": "string", "enum": ["Default","Primary","Danger"], "description": "Visual emphasis." },
                      "requiresComment": { "type": "boolean", "x-advanced": true, "description": "Require the responder to enter a comment before this button submits." },
                      "resolvesWait": { "type": "boolean", "default": true, "x-advanced": true, "description": "Whether clicking this button RESOLVES the wait (the default). False = a non-terminal action: it's recorded for everyone to see but keeps the card open for others (e.g. an \"I'm looking\" ack alongside the real decision)." },
                      "vetoes": { "type": "boolean", "x-advanced": true, "description": "When true, one click resolves the wait IMMEDIATELY regardless of the resolve mode — a short-circuit (e.g. one \"request changes\" blocks a 2-approval quorum)." }
                    },
                    "required": ["key","label"]
                  }
                },
                "component": {
                  "type": "object",
                  "x-advanced": true,
                  "description": "Generic interaction component { kind, … } — the extensible alternative to `actions`/`form`. A new kind (poll, checklist, …) plugs in as a backend factory with no change to this node. When set it wins over `actions`/`form`. (`actions`/`form` remain as convenience shorthands.)",
                  "properties": {
                    "kind": { "type": "string", "description": "Which component to render (e.g. \"action_buttons\", \"form\")." }
                  },
                  "required": ["kind"]
                },
                "form": {
                  "type": "object",
                  "description": "Form card — input fields the responder fills; the submitted values are injected into the run (surfaced as the wait node's outputs.values). Alternative to `actions` (form wins if both are set).",
                  "properties": {
                    "fields": { "type": "object", "description": "A JSON Schema describing the form's input fields (rendered by the client's schema-driven form)." },
                    "submitLabel": { "type": "string", "description": "Submit button text (default \"Submit\")." }
                  },
                  "required": ["fields"]
                },
                "allowedResponderUserIds": { "type": "array", "items": { "type": "string", "format": "uuid" }, "x-selector": "user", "description": "Restrict who may respond — pick members. Empty = any member of the conversation may respond." }
              },
              "required": ["conversationId","body"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "messageId": { "type": "string", "description": "Posted message id." },
                "token": { "type": ["string","null"], "description": "Action token — output for wiring a separate flow.wait_action when NOT waiting here." },
                "action":  { "type": "string", "description": "The clicked button's key — present when this node waited (waitForResponse)." },
                "by":      { "type": "string", "description": "Responder's user id — present when this node waited." },
                "comment": { "type": "string", "description": "Responder's comment — present when this node waited." },
                "values":  { "type": "object", "description": "A form submission's field values — present when this node waited on a form." }
              }
            }
            """),
        Presets =
        [
            new NodePreset
            {
                Id = "announcement",
                Label = "Announcement",
                Description = "Post a message. No response expected.",
                Config = SchemaBuilder.Parse("""{ "waitForResponse": false }"""),
                Inputs = SchemaBuilder.Parse("""{ "conversationId": "", "body": "" }"""),
            },
            new NodePreset
            {
                Id = "approval",
                Label = "Approval",
                Description = "One reviewer decides — approve or reject.",
                Config = SchemaBuilder.Parse("""{ "waitForResponse": true, "resolve": { "mode": "first", "count": 1 } }"""),
                Inputs = SchemaBuilder.Parse("""
                    {
                      "conversationId": "",
                      "body": "",
                      "actions": [
                        { "key": "approve", "label": "Approve", "style": "Primary" },
                        { "key": "reject", "label": "Reject", "style": "Danger", "requiresComment": true }
                      ]
                    }
                    """),
            },
            new NodePreset
            {
                Id = "quorum_review",
                Label = "Quorum review",
                Description = "N approvals to pass; any \"Request changes\" blocks.",
                Config = SchemaBuilder.Parse("""{ "waitForResponse": true, "resolve": { "mode": "quorum", "count": 2 } }"""),
                Inputs = SchemaBuilder.Parse("""
                    {
                      "conversationId": "",
                      "body": "",
                      "actions": [
                        { "key": "approve", "label": "Approve", "style": "Primary" },
                        { "key": "request_changes", "label": "Request changes", "style": "Danger", "requiresComment": true, "vetoes": true }
                      ]
                    }
                    """),
            },
            new NodePreset
            {
                Id = "form",
                Label = "Form",
                Description = "Collect structured input from one responder.",
                Config = SchemaBuilder.Parse("""{ "waitForResponse": true }"""),
                Inputs = SchemaBuilder.Parse("""
                    {
                      "conversationId": "",
                      "body": "",
                      "form": { "fields": { "type": "object", "properties": {} }, "submitLabel": "Submit" }
                    }
                    """),
            },
        ],
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Resumed pass: this node posted a card on its first pass and parked; a click / form submit resolved
        // the wait. Surface the decision as outputs — and do NOT re-post (the card is already up). The
        // ResumePayload guard is what makes the side-effecting post happen exactly once.
        if (context.ResumePayload.HasValue)
            return NodeResult.Ok(BuildDecisionOutputs(context.ResumePayload.Value));

        if (RequireGuidInput(context, "conversationId", out var conversationId) is { } conversationError) return NodeResult.Fail(conversationError);
        if (RequireStringInput(context, "body", out var body) is { } bodyError) return NodeResult.Fail(bodyError);

        var interaction = BuildInteraction(context, out var token);

        MessageView posted;
        try
        {
            posted = await _bot.PostAsBotAsync(conversationId, body, interaction, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            return NodeResult.Fail(ex.Message);
        }

        context.Logger.LogInformation("Bot posted message {MessageId} to conversation {ConversationId} (interactive={Interactive})", posted.Id, conversationId, interaction != null);

        // Opt-in: wait for the response HERE instead of forcing a separate flow.wait_action. Only when there's
        // an interaction (a card carries the token) — park on the SAME token so a click resolves exactly this
        // card; the resumed pass (above) re-runs this node and emits { action, by, comment, values }.
        if (token != null && ShouldWaitForResponse(context))
            return NodeResult.Suspend(new SuspensionToken { Kind = WorkflowWaitKinds.Action, CorrelationToken = token, Payload = JsonSerializer.SerializeToElement(new { token }) });

        var outputs = new Dictionary<string, JsonElement>
        {
            ["messageId"] = JsonSerializer.SerializeToElement(posted.Id.ToString()),
            ["token"] = JsonSerializer.SerializeToElement(token),
        };

        return NodeResult.Ok(outputs);
    }

    /// <summary>
    /// Opt-in (config) to fold the wait into this node. New nodes seed it TRUE from the ConfigSchema default;
    /// a definition saved before this field has no key and reads FALSE here, so the post → separate
    /// flow.wait_action graphs that predate it keep their exact behaviour (the runtime never injects schema
    /// defaults — same contract as LlmComplete/HttpRequest reading their own config fallbacks).
    /// </summary>
    private static bool ShouldWaitForResponse(NodeRunContext context) =>
        context.Config.TryGetValue("waitForResponse", out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Map the resumed action / form decision onto this node's response outputs (mirrors flow.wait_action).</summary>
    private static Dictionary<string, JsonElement> BuildDecisionOutputs(JsonElement decision)
    {
        var outputs = new Dictionary<string, JsonElement>
        {
            ["action"]  = ReadOr(decision, "action", JsonSerializer.SerializeToElement("")),
            ["by"]      = ReadOr(decision, "by", JsonSerializer.SerializeToElement("")),
            ["comment"] = ReadOr(decision, "comment", JsonSerializer.SerializeToElement("")),
        };

        // A form submission also carries `values` (the field object); absent for a plain button click.
        if (decision.ValueKind == JsonValueKind.Object && decision.TryGetProperty("values", out var values))
            outputs["values"] = values.Clone();

        return outputs;
    }

    private static JsonElement ReadOr(JsonElement obj, string key, JsonElement fallback) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v.Clone() : fallback;

    /// <summary>
    /// Build the interaction (null ⇒ a plain message). The component is built by the
    /// <see cref="IInteractionComponentRegistry"/> from a generic <c>component</c> config — so a new kind
    /// (poll, …) is a new factory with no edit here. Legacy <c>form</c>/<c>actions</c> inputs are shimmed to
    /// that config, so pre-existing definitions are unchanged. Mints the wait token + reads the resolve policy.
    /// </summary>
    private MessageInteraction? BuildInteraction(NodeRunContext context, out string? token)
    {
        token = null;

        var componentConfig = NormalizeComponentConfig(context);
        var component = componentConfig is { } config ? _components.Build(config) : null;
        if (component == null) return null;

        token = Guid.NewGuid().ToString("N");

        return new MessageInteraction
        {
            Component = component,
            Target = new WorkflowWaitTarget { Token = token },
            AllowedResponderUserIds = ReadGuidList(context, "allowedResponderUserIds"),
            Resolve = ParseResolvePolicy(context),
        };
    }

    /// <summary>
    /// The component config to build from: the generic <c>component</c> input wins; otherwise the legacy
    /// <c>form</c> (over <c>actions</c>) inputs are shimmed into the same <c>{ kind, … }</c> shape — so the
    /// registry sees one uniform config whether authored the new way or the old way. Null ⇒ no interaction.
    /// </summary>
    private static JsonElement? NormalizeComponentConfig(NodeRunContext context)
    {
        if (context.Inputs.TryGetValue("component", out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("kind", out _)) return c;

        if (context.Inputs.TryGetValue("form", out var form) && form.ValueKind == JsonValueKind.Object) return ShimForm(form);

        if (context.Inputs.TryGetValue("actions", out var actions) && actions.ValueKind == JsonValueKind.Array) return ShimActions(actions);

        return null;
    }

    private static JsonElement? ShimForm(JsonElement form)
    {
        if (!form.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object) return null;

        var submitLabel = form.TryGetProperty("submitLabel", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

        return JsonSerializer.SerializeToElement(new { kind = "form", fields, submitLabel });
    }

    private static JsonElement ShimActions(JsonElement actions) =>
        JsonSerializer.SerializeToElement(new { kind = "action_buttons", buttons = actions });

    /// <summary>
    /// Read the resolve policy from the <c>resolve</c> config (<c>{ mode: first|quorum, count }</c>).
    /// Default first-click; quorum count floored at 1. Absent config ⇒ the default, so existing cards resolve first-wins.
    /// </summary>
    private static ResolvePolicy ParseResolvePolicy(NodeRunContext context)
    {
        if (!context.Config.TryGetValue("resolve", out var r) || r.ValueKind != JsonValueKind.Object) return new ResolvePolicy();

        var kind = r.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String && string.Equals(m.GetString(), "quorum", StringComparison.OrdinalIgnoreCase)
            ? ResolvePolicyKind.Quorum : ResolvePolicyKind.First;

        var count = r.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) && n >= 1 ? n : 1;

        return new ResolvePolicy { Kind = kind, Count = count };
    }

    private static IReadOnlyList<Guid>? ReadGuidList(NodeRunContext context, string key)
    {
        if (!context.Inputs.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.Array) return null;

        var ids = new List<Guid>();
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var g)) ids.Add(g);

        return ids.Count > 0 ? ids : null;
    }

    /// <summary>
    /// Read a required non-empty string input, returning a SPECIFIC error (else null). Distinguishes
    /// missing / wrong-type / empty so a <c>{{ref}}</c> that resolves to an array or object (a common
    /// wiring slip) reads as "must be a string, got an array" rather than the misleading "missing".
    /// </summary>
    private static string? RequireStringInput(NodeRunContext context, string key, out string value)
    {
        value = "";

        if (!TryGetPresentInput(context, key, out var v)) return $"Input '{key}' is required.";
        if (v.ValueKind != JsonValueKind.String) return WrongTypeError(key, "a string", v.ValueKind);

        value = v.GetString() ?? "";
        return value.Length == 0 ? $"Input '{key}' must not be empty." : null;
    }

    /// <summary>Read a required id (uuid) string input, returning a specific error (else null).</summary>
    private static string? RequireGuidInput(NodeRunContext context, string key, out Guid value)
    {
        value = Guid.Empty;

        if (!TryGetPresentInput(context, key, out var v)) return $"Input '{key}' is required.";
        if (v.ValueKind != JsonValueKind.String) return WrongTypeError(key, "a conversation id (string)", v.ValueKind);

        return Guid.TryParse(v.GetString(), out value) ? null : $"Input '{key}' must be a valid id, got '{v.GetString()}'.";
    }

    private static bool TryGetPresentInput(NodeRunContext context, string key, out JsonElement value) =>
        context.Inputs.TryGetValue(key, out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string WrongTypeError(string key, string expected, JsonValueKind got) =>
        $"Input '{key}' must be {expected}, but got {DescribeKind(got)}. If you wired a {{{{reference}}}} that resolves to an array/object, point it at a single value instead.";

    private static string DescribeKind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "another type",
    };
}
