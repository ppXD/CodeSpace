using System.Text.Json;
using CodeSpace.Core.Services.Chat;
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

    public ChatPostMessageNode(IChatBotService bot)
    {
        _bot = bot;
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
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "waitForResponse": { "type": "boolean", "default": true, "description": "When the message is interactive (actions/form), pause HERE until someone responds and surface their choice as this node's outputs (action / by / comment / values) — no separate flow.wait_action needed. Off = post-and-continue (fire-and-forget), or wait elsewhere via a flow.wait_action wired to the `token` output. Ignored for a plain message." }
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
                      "key": { "type": "string", "description": "What the workflow RECEIVES when this button is clicked — surfaced as outputs.action (this node when waiting, else the downstream flow.wait_action). The button only emits this signal; what it DOES is whatever you wire downstream (e.g. \"approve\" → a git.pr_review verdict)." },
                      "label": { "type": "string", "description": "Button text shown to the responder." },
                      "description": { "type": "string", "description": "Optional: what this button does. Shown to the responder as a tooltip on the button, so a click's effect isn't opaque." },
                      "style": { "type": "string", "enum": ["Default","Primary","Danger"], "description": "Visual emphasis." },
                      "requiresComment": { "type": "boolean", "description": "Require the responder to enter a comment before this button submits." }
                    },
                    "required": ["key","label"]
                  }
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
    /// Build the interaction from the <c>actions</c> input (null ⇒ a plain message). Mints the action
    /// token and returns it via <paramref name="token"/> so the caller can wire it into flow.wait_action;
    /// the card's <c>workflow_wait</c> target carries the SAME token, which is what couples them.
    /// </summary>
    private static MessageInteraction? BuildInteraction(NodeRunContext context, out string? token)
    {
        token = null;

        // A card is one component kind: a form (if given) wins over buttons; neither ⇒ a plain message.
        InteractionComponent? component = BuildFormComponent(context);
        component ??= BuildActionButtonsComponent(context);
        if (component == null) return null;

        token = Guid.NewGuid().ToString("N");

        return new MessageInteraction
        {
            Component = component,
            Target = new WorkflowWaitTarget { Token = token },
            AllowedResponderUserIds = ReadGuidList(context, "allowedResponderUserIds"),
        };
    }

    private static FormComponent? BuildFormComponent(NodeRunContext context)
    {
        if (!context.Inputs.TryGetValue("form", out var formEl) || formEl.ValueKind != JsonValueKind.Object) return null;
        if (!formEl.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object) return null;

        var submitLabel = formEl.TryGetProperty("submitLabel", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "Submit";

        return new FormComponent { Fields = fields.Clone(), SubmitLabel = submitLabel };
    }

    private static ActionButtonsComponent? BuildActionButtonsComponent(NodeRunContext context)
    {
        if (!context.Inputs.TryGetValue("actions", out var actionsEl) || actionsEl.ValueKind != JsonValueKind.Array) return null;

        var buttons = new List<InteractionButton>();
        foreach (var a in actionsEl.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) continue;

            var key = a.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            var label = a.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(label)) continue;

            var description = a.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var style = a.TryGetProperty("style", out var s) && s.ValueKind == JsonValueKind.String && Enum.TryParse<InteractionButtonStyle>(s.GetString(), ignoreCase: true, out var parsed) ? parsed : InteractionButtonStyle.Default;
            var requiresComment = a.TryGetProperty("requiresComment", out var rc) && rc.ValueKind == JsonValueKind.True;

            buttons.Add(new InteractionButton { Key = key!, Label = label!, Description = description, Style = style, RequiresComment = requiresComment });
        }

        return buttons.Count > 0 ? new ActionButtonsComponent { Buttons = buttons } : null;
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
