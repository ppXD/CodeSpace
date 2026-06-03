using System.Text.Json;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Chat.Interactions;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class ChatPostMessageNodeTests
{
    /// <summary>The real component-factory registry — the node builds its card through it (action_buttons + form).</summary>
    private static readonly IInteractionComponentRegistry Components =
        new InteractionComponentRegistry(new IInteractionComponentFactory[] { new ActionButtonsComponentFactory(), new FormComponentFactory() });

    private static ChatPostMessageNode Node(IChatBotService bot) => new(bot, Components);

    /// <summary>Hand-rolled stub (this suite uses no mocking lib) — records what was posted, returns a canned view.</summary>
    private sealed class StubChatBot : IChatBotService
    {
        public MessageInteraction? Interaction;

        public Task<Guid> GetOrCreateTeamBotAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());

        public Task<MessageView> PostAsBotAsync(Guid conversationId, string body, MessageInteraction? interaction, CancellationToken cancellationToken)
        {
            Interaction = interaction;
            return Task.FromResult(new MessageView
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ConversationId = conversationId,
                AuthorUserId = Guid.NewGuid(),
                Body = body,
                CreatedDate = DateTimeOffset.UnixEpoch,
                IsDeleted = false,
                References = Array.Empty<MessageReferenceView>(),
            });
        }
    }

    [Fact]
    public async Task Posts_an_interactive_card_and_outputs_the_same_token_it_put_on_the_card()
    {
        var bot = new StubChatBot();
        var ctx = BuildContext("11111111-1111-1111-1111-111111111111", "Review?",
            """[{"key":"approve","label":"Approve","style":"Primary"},{"key":"reject","label":"Reject","style":"Danger","requiresComment":true}]""");

        var result = await Node(bot).RunAsync(ctx, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);

        var component = bot.Interaction.ShouldNotBeNull().Component.ShouldBeOfType<ActionButtonsComponent>();
        component.Buttons.Select(b => b.Key).ShouldBe(new[] { "approve", "reject" });
        component.Buttons[1].Style.ShouldBe(InteractionButtonStyle.Danger);
        component.Buttons[1].RequiresComment.ShouldBeTrue();

        // The coupling that makes the loop work: the node's `token` output MUST equal the token it put
        // on the card's wait target, so a downstream flow.wait_action parks on exactly this card's wait.
        var target = bot.Interaction!.Target.ShouldBeOfType<WorkflowWaitTarget>();
        result.Outputs["token"].GetString().ShouldBe(target.Token);
        result.Outputs["messageId"].GetString().ShouldBe("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    }

    [Fact]
    public async Task Carries_an_optional_button_description_onto_the_component()
    {
        var bot = new StubChatBot();
        var ctx = BuildContext("11111111-1111-1111-1111-111111111111", "Review?",
            """[{"key":"approve","label":"Approve","description":"Approve + merge"},{"key":"reject","label":"Reject"}]""");

        await Node(bot).RunAsync(ctx, CancellationToken.None);

        var component = bot.Interaction.ShouldNotBeNull().Component.ShouldBeOfType<ActionButtonsComponent>();
        component.Buttons[0].Description.ShouldBe("Approve + merge", "an author-written button description carries onto the card for the responder's tooltip");
        component.Buttons[1].Description.ShouldBeNull("description is optional");
    }

    [Fact]
    public async Task Posts_a_plain_message_with_a_null_token_when_no_actions()
    {
        var bot = new StubChatBot();

        var result = await Node(bot).RunAsync(BuildContext("11111111-1111-1111-1111-111111111111", "Deployed", actionsJson: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        bot.Interaction.ShouldBeNull("no actions ⇒ a plain announcement, not a card");
        result.Outputs["token"].ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Fails_when_conversation_id_is_missing()
    {
        var result = await Node(new StubChatBot())
            .RunAsync(BuildContext(conversationId: null, body: "x", actionsJson: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("conversationId");
    }

    [Fact]
    public async Task Fails_with_a_type_aware_error_when_body_is_the_wrong_type()
    {
        // The slip that bit a real user: wiring body to a {{ref}} that resolves to an ARRAY (a file
        // diff list). The error must name the actual type, not the misleading "missing or empty".
        var inputs = new Dictionary<string, JsonElement>
        {
            ["conversationId"] = JsonSerializer.SerializeToElement("11111111-1111-1111-1111-111111111111"),
            ["body"] = JsonDocument.Parse("""[{"Patch":"@@ ..."}]""").RootElement.Clone(),
        };

        var result = await Node(new StubChatBot()).RunAsync(ContextFromInputs(inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("must be a string");
        result.Error.ShouldContain("an array", customMessage: "name the actual wrong type so a ref that resolved to an array is obvious");
    }

    [Fact]
    public async Task Fails_with_required_when_body_is_missing()
    {
        var inputs = new Dictionary<string, JsonElement> { ["conversationId"] = JsonSerializer.SerializeToElement("11111111-1111-1111-1111-111111111111") };

        var result = await Node(new StubChatBot()).RunAsync(ContextFromInputs(inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("'body' is required");
    }

    [Fact]
    public async Task Fails_with_empty_when_body_is_a_blank_string()
    {
        var inputs = new Dictionary<string, JsonElement>
        {
            ["conversationId"] = JsonSerializer.SerializeToElement("11111111-1111-1111-1111-111111111111"),
            ["body"] = JsonSerializer.SerializeToElement(""),
        };

        var result = await Node(new StubChatBot()).RunAsync(ContextFromInputs(inputs), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("must not be empty");
    }

    [Fact]
    public async Task Posts_a_form_card_and_outputs_the_same_token_it_put_on_the_card()
    {
        var bot = new StubChatBot();
        var ctx = BuildContext("11111111-1111-1111-1111-111111111111", "Pick a target", actionsJson: null,
            formJson: """{"fields":{"type":"object","properties":{"channel":{"type":"string"}},"required":["channel"]},"submitLabel":"Send"}""");

        var result = await Node(bot).RunAsync(ctx, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);

        var form = bot.Interaction.ShouldNotBeNull().Component.ShouldBeOfType<FormComponent>();
        form.SubmitLabel.ShouldBe("Send");
        form.Fields.GetProperty("properties").TryGetProperty("channel", out _).ShouldBeTrue("the authored field schema is carried verbatim onto the card");

        var target = bot.Interaction!.Target.ShouldBeOfType<WorkflowWaitTarget>();
        result.Outputs["token"].GetString().ShouldBe(target.Token, "a form card couples to its wait by the same token as a button card");
    }

    [Fact]
    public async Task A_form_input_wins_over_actions_when_both_are_supplied()
    {
        var bot = new StubChatBot();
        var ctx = BuildContext("11111111-1111-1111-1111-111111111111", "x",
            actionsJson: """[{"key":"approve","label":"Approve"}]""",
            formJson: """{"fields":{"type":"object","properties":{"v":{"type":"string"}}}}""");

        await Node(bot).RunAsync(ctx, CancellationToken.None);

        bot.Interaction.ShouldNotBeNull().Component.ShouldBeOfType<FormComponent>("a card is one kind — a form takes precedence over buttons");
    }

    [Fact]
    public async Task Posts_then_suspends_on_the_cards_own_token_when_wait_for_response_is_on()
    {
        var bot = new StubChatBot();
        var ctx = BuildContext("11111111-1111-1111-1111-111111111111", "Review?",
            """[{"key":"approve","label":"Approve"}]""", waitForResponse: true);

        var result = await Node(bot).RunAsync(ctx, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended, "with waitForResponse on, the node posts the card THEN parks for the response — one node, no separate flow.wait_action");
        bot.Interaction.ShouldNotBeNull("the card is posted BEFORE suspending — the side-effect runs on the first pass");

        var token = bot.Interaction!.Target.ShouldBeOfType<WorkflowWaitTarget>().Token;
        result.SuspendUntil.ShouldNotBeNull();
        result.SuspendUntil!.Kind.ShouldBe(WorkflowWaitKinds.Action);
        result.SuspendUntil.CorrelationToken.ShouldBe(token, "it parks on the SAME token the card carries, so a click resolves exactly this card");
    }

    [Fact]
    public async Task Does_not_wait_when_wait_for_response_is_on_but_there_is_no_interaction()
    {
        var bot = new StubChatBot();

        var result = await Node(bot)
            .RunAsync(BuildContext("11111111-1111-1111-1111-111111111111", "Deployed", actionsJson: null, waitForResponse: true), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, "a plain message has nothing to respond to ⇒ it can't wait; it completes immediately");
        result.Outputs["token"].ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Resumed_pass_outputs_the_decision_and_does_not_repost()
    {
        var bot = new StubChatBot();
        var decision = JsonDocument.Parse("""{ "action": "approve", "by": "u-1", "comment": "lgtm", "values": { "channel": "ops" } }""").RootElement;
        var ctx = ContextFromInputs(new Dictionary<string, JsonElement>(), resumePayload: decision);

        var result = await Node(bot).RunAsync(ctx, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        bot.Interaction.ShouldBeNull("the resumed pass must NOT post again — the card is already up (ResumePayload guards the one-time side-effect)");
        result.Outputs["action"].GetString().ShouldBe("approve");
        result.Outputs["by"].GetString().ShouldBe("u-1");
        result.Outputs["comment"].GetString().ShouldBe("lgtm");
        result.Outputs["values"].GetProperty("channel").GetString().ShouldBe("ops", "a form submission's field values surface under `values`");
    }

    [Fact]
    public async Task Carries_per_action_resolve_flags_onto_the_card()
    {
        var bot = new StubChatBot();
        var ctx = BuildContext("11111111-1111-1111-1111-111111111111", "Review?",
            """[{"key":"approve","label":"Approve"},{"key":"note","label":"Comment","resolvesWait":false},{"key":"reject","label":"Reject","vetoes":true}]""");

        await Node(bot).RunAsync(ctx, CancellationToken.None);

        var buttons = bot.Interaction.ShouldNotBeNull().Component.ShouldBeOfType<ActionButtonsComponent>().Buttons;
        buttons.Single(b => b.Key == "approve").ResolvesWait.ShouldBeTrue("a button is a terminal decision by default");
        buttons.Single(b => b.Key == "note").ResolvesWait.ShouldBeFalse("resolvesWait:false ⇒ non-terminal discussion");
        buttons.Single(b => b.Key == "reject").Vetoes.ShouldBeTrue("vetoes short-circuits the wait");
    }

    [Fact]
    public async Task Authors_the_quorum_resolve_policy_from_config()
    {
        var bot = new StubChatBot();
        var ctx = BuildContext("11111111-1111-1111-1111-111111111111", "Review?",
            """[{"key":"approve","label":"Approve"}]""", resolveJson: """{ "mode": "quorum", "count": 3 }""");

        await Node(bot).RunAsync(ctx, CancellationToken.None);

        bot.Interaction.ShouldNotBeNull().Resolve.Kind.ShouldBe(ResolvePolicyKind.Quorum);
        bot.Interaction!.Resolve.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Defaults_the_resolve_policy_to_first_when_no_config()
    {
        var bot = new StubChatBot();

        await Node(bot).RunAsync(BuildContext("11111111-1111-1111-1111-111111111111", "Review?", """[{"key":"approve","label":"Approve"}]"""), CancellationToken.None);

        bot.Interaction.ShouldNotBeNull().Resolve.Kind.ShouldBe(ResolvePolicyKind.First, "no resolve config ⇒ first-click, backward compatible");
    }

    [Fact]
    public async Task Builds_via_the_generic_component_input_which_wins_over_legacy_actions()
    {
        var bot = new StubChatBot();
        var inputs = new Dictionary<string, JsonElement>
        {
            ["conversationId"] = JsonSerializer.SerializeToElement("11111111-1111-1111-1111-111111111111"),
            ["body"] = JsonSerializer.SerializeToElement("Review?"),
            ["component"] = JsonDocument.Parse("""{ "kind": "action_buttons", "buttons": [ { "key": "ship", "label": "Ship" } ] }""").RootElement.Clone(),
            ["actions"] = JsonDocument.Parse("""[ { "key": "legacy", "label": "Legacy" } ]""").RootElement.Clone(),
        };

        await Node(bot).RunAsync(ContextFromInputs(inputs), CancellationToken.None);

        bot.Interaction.ShouldNotBeNull().Component.ShouldBeOfType<ActionButtonsComponent>()
            .Buttons.Select(b => b.Key).ShouldBe(new[] { "ship" }, "the explicit generic component wins over the legacy actions shorthand");
    }

    private static NodeRunContext BuildContext(string? conversationId, string body, string? actionsJson, string? formJson = null, bool waitForResponse = false, string? resolveJson = null)
    {
        var inputs = new Dictionary<string, JsonElement> { ["body"] = JsonSerializer.SerializeToElement(body) };
        if (conversationId != null) inputs["conversationId"] = JsonSerializer.SerializeToElement(conversationId);
        if (actionsJson != null) inputs["actions"] = JsonDocument.Parse(actionsJson).RootElement.Clone();
        if (formJson != null) inputs["form"] = JsonDocument.Parse(formJson).RootElement.Clone();

        var config = new Dictionary<string, JsonElement>();
        if (waitForResponse) config["waitForResponse"] = JsonSerializer.SerializeToElement(true);
        if (resolveJson != null) config["resolve"] = JsonDocument.Parse(resolveJson).RootElement.Clone();

        return ContextFromInputs(inputs, config.Count > 0 ? config : null);
    }

    private static NodeRunContext ContextFromInputs(Dictionary<string, JsonElement> inputs, Dictionary<string, JsonElement>? config = null, JsonElement? resumePayload = null) => new()
    {
        Inputs = inputs,
        Config = config ?? new Dictionary<string, JsonElement>(),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
        ResumePayload = resumePayload,
    };
}
