using System.Text.Json;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Posts a message into a conversation AS the team's CodeSpace bot (via <see cref="IChatBotService"/>,
/// which derives the team from the conversation + auto-joins the bot). With an <c>actions</c> input it
/// posts an interactive CARD: it mints an action token, attaches it as the interaction's
/// <c>workflow_wait</c> target, AND outputs it as <c>token</c> — wire that into a downstream
/// <c>flow.wait_action</c> so a button click resolves exactly the wait this card drives. Without
/// <c>actions</c> it posts a plain announcement (no token). Side-effecting (a permanent post).
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
        Description = "Posts a message into a conversation as the CodeSpace bot. Add `actions` to attach an Action-buttons card (the interaction component — more kinds like forms/polls can be added later); wire its `token` output into a flow.wait_action to wait for a click.",
        IsSideEffecting = true,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "conversationId": { "type": "string", "format": "uuid", "x-selector": "conversation", "description": "Target conversation. Pick a channel, or switch to Expression to bind it dynamically (e.g. {{trigger.channelId}})." },
                "body": { "type": "string", "minLength": 1, "description": "The message text. Supports {{ }} references." },
                "actions": {
                  "type": "array",
                  "description": "Action buttons — each item renders as a clickable button. The clicked button's `key` is what a downstream flow.wait_action resumes with. Leave empty to post a plain message. (One interaction component; `form` is the other.)",
                  "items": {
                    "type": "object",
                    "properties": {
                      "key": { "type": "string", "description": "Returned to the workflow when this button is clicked (e.g. \"approve\")." },
                      "label": { "type": "string", "description": "Button text shown to the responder." },
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
                "allowedResponderUserIds": { "type": "array", "items": { "type": "string", "format": "uuid" }, "description": "Restrict who may respond (user ids). Empty = any member of the conversation may respond." },
                "requireResponderIdentityForRepositoryId": { "type": "string", "format": "uuid", "x-selector": "repository", "description": "When set, the responder must have a linked identity on THIS repository's provider before they can respond — because the resumed run will act AS them on it (e.g. a downstream git.pr_review). Surfaces a 428 link prompt on the click instead of failing the run in the background. Wire it to the same repo the act-as-user node uses (e.g. {{input.repo}})." }
              },
              "required": ["conversationId","body"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "messageId": { "type": "string" },
                "token": { "type": ["string","null"] }
              }
            }
            """),
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
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

        var outputs = new Dictionary<string, JsonElement>
        {
            ["messageId"] = JsonSerializer.SerializeToElement(posted.Id.ToString()),
            ["token"] = JsonSerializer.SerializeToElement(token),
        };

        return NodeResult.Ok(outputs);
    }

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
            Target = new WorkflowWaitTarget
            {
                Token = token,
                RequiresResponderIdentityForRepositoryId = ReadOptionalGuid(context, "requireResponderIdentityForRepositoryId"),
            },
            AllowedResponderUserIds = ReadGuidList(context, "allowedResponderUserIds"),
        };
    }

    /// <summary>Read an optional uuid input → Guid?, ignoring absent / null / unparseable values.</summary>
    private static Guid? ReadOptionalGuid(NodeRunContext context, string key) =>
        TryGetPresentInput(context, key, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : null;

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

            var style = a.TryGetProperty("style", out var s) && s.ValueKind == JsonValueKind.String && Enum.TryParse<InteractionButtonStyle>(s.GetString(), ignoreCase: true, out var parsed) ? parsed : InteractionButtonStyle.Default;
            var requiresComment = a.TryGetProperty("requiresComment", out var rc) && rc.ValueKind == JsonValueKind.True;

            buttons.Add(new InteractionButton { Key = key!, Label = label!, Style = style, RequiresComment = requiresComment });
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
