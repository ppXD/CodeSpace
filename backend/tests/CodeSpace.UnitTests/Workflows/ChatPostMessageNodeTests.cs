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

    private static ChatPostMessageNode Node(IChatBotService bot, IMessageInteractionService? interactions = null) =>
        new(bot, Components, new Lazy<IMessageInteractionService>(() => interactions ?? new StubInteractions()));

    /// <summary>Records a timeout-stamp call (the node's only use of the interaction service).</summary>
    private sealed class StubInteractions : IMessageInteractionService
    {
        public (Guid MessageId, string ResponseKey)? TimedOut;

        public Task RespondAsync(Guid teamId, Guid messageId, string responseKey, Guid actorUserId, string? comment, IReadOnlyDictionary<string, JsonElement>? values, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task MarkTimedOutAsync(Guid messageId, string responseKey, CancellationToken cancellationToken)
        {
            TimedOut = (messageId, responseKey);
            return Task.CompletedTask;
        }
    }

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

    [Fact]
    public void Declares_footgun_free_intent_presets_for_the_editor()
    {
        // Constructing the node runs the manifest initializer, which SchemaBuilder.Parse's every preset's
        // JSON — so this also proves all four preset blobs are valid JSON.
        var presets = Node(new StubChatBot()).Manifest.Presets.ShouldNotBeNull();

        presets.Select(p => p.Id).ShouldBe(new[] { "announcement", "approval", "quorum_review", "form" });

        // The quorum template is the footgun-free shape: a veto block + NO comment-as-vote button
        // (discussion goes through the always-on comment box, not a terminal "comment" action).
        var quorum = presets.Single(p => p.Id == "quorum_review");
        quorum.Config.GetProperty("resolve").GetProperty("mode").GetString().ShouldBe("quorum");

        var actions = quorum.Inputs.GetProperty("actions").EnumerateArray().ToList();
        actions.Select(a => a.GetProperty("key").GetString()).ShouldBe(new[] { "approve", "request_changes" });
        actions.Single(a => a.GetProperty("key").GetString() == "request_changes").GetProperty("vetoes").GetBoolean()
            .ShouldBeTrue("request changes blocks via veto, not as a competing vote");
    }

    // ─── Bounded wait: deadline + onTimeout default ─────────────────────────────────

    [Fact]
    public void ReadDeadline_returns_seconds_and_action_when_both_set()
    {
        var (seconds, onTimeout, error) = ChatPostMessageNode.ReadDeadline(JsonDocument.Parse("""{ "deadlineSeconds": 1800, "onTimeout": "reject" }""").RootElement);

        seconds.ShouldBe(1800);
        onTimeout.ShouldBe("reject");
        error.ShouldBeNull();
    }

    [Fact]
    public void ReadDeadline_is_unbounded_when_no_positive_deadline()
    {
        ChatPostMessageNode.ReadDeadline(JsonDocument.Parse("""{ "mode": "first" }""").RootElement).Seconds.ShouldBeNull("no deadline key ⇒ unbounded");
        ChatPostMessageNode.ReadDeadline(JsonDocument.Parse("""{ "deadlineSeconds": 0, "onTimeout": "x" }""").RootElement).Seconds.ShouldBeNull("a non-positive deadline ⇒ unbounded");
        ChatPostMessageNode.ReadDeadline(default).Seconds.ShouldBeNull("no resolve config at all ⇒ unbounded");
    }

    [Fact]
    public void ReadDeadline_errors_when_a_deadline_lacks_an_action()
    {
        var (seconds, onTimeout, error) = ChatPostMessageNode.ReadDeadline(JsonDocument.Parse("""{ "deadlineSeconds": 600 }""").RootElement);

        seconds.ShouldBeNull();
        onTimeout.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("On timeout");
    }

    [Fact]
    public async Task Suspends_with_a_deadline_and_timeout_payload_when_configured()
    {
        var bot = new StubChatBot();
        var ctx = BuildContext("11111111-1111-1111-1111-111111111111", "Deploy?",
            """[{"key":"approve","label":"Approve"},{"key":"reject","label":"Reject"}]""",
            waitForResponse: true, resolveJson: """{ "mode": "first", "deadlineSeconds": 1800, "onTimeout": "reject" }""");

        var result = await Node(bot).RunAsync(ctx, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        result.SuspendUntil.ShouldNotBeNull();
        result.SuspendUntil!.DeadlineAt.ShouldNotBeNull("a configured deadline parks a BOUNDED wait the engine auto-resolves");

        var timeout = result.SuspendUntil.TimeoutPayload.ShouldNotBeNull();
        timeout.GetProperty("action").GetString().ShouldBe("reject", "on timeout the wait resolves with the onTimeout action");
        timeout.GetProperty("_timedOut").GetBoolean().ShouldBeTrue();
        timeout.GetProperty("_messageId").GetString().ShouldBe("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "carries the card id so the timeout-resume can stamp it resolved");
    }

    [Fact]
    public async Task Fails_before_posting_when_a_deadline_has_no_timeout_action()
    {
        var bot = new StubChatBot();
        var ctx = BuildContext("11111111-1111-1111-1111-111111111111", "Deploy?",
            """[{"key":"approve","label":"Approve"}]""", waitForResponse: true, resolveJson: """{ "deadlineSeconds": 600 }""");

        var result = await Node(bot).RunAsync(ctx, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("On timeout");
        bot.Interaction.ShouldBeNull("the misconfig fails BEFORE posting — no orphan card left behind");
    }

    [Fact]
    public async Task Keeps_an_unbounded_wait_when_no_deadline_is_configured()
    {
        var bot = new StubChatBot();
        var ctx = BuildContext("11111111-1111-1111-1111-111111111111", "Review?",
            """[{"key":"approve","label":"Approve"}]""", waitForResponse: true);

        var result = await Node(bot).RunAsync(ctx, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);
        result.SuspendUntil!.DeadlineAt.ShouldBeNull("no deadline config ⇒ wait indefinitely, the prior behaviour");
        result.SuspendUntil.TimeoutPayload.ShouldBeNull();
    }

    [Fact]
    public async Task Timed_out_resume_stamps_the_card_and_outputs_the_timeout_action()
    {
        var bot = new StubChatBot();
        var interactions = new StubInteractions();
        var decision = JsonDocument.Parse("""{ "action": "reject", "_timedOut": true, "_messageId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" }""").RootElement;

        var result = await Node(bot, interactions).RunAsync(ContextFromInputs(new Dictionary<string, JsonElement>(), resumePayload: decision), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["action"].GetString().ShouldBe("reject", "the timeout resolves the wait with the onTimeout action");

        interactions.TimedOut.ShouldNotBeNull("a timeout-resume mirrors the resolution onto the card so it stops showing live buttons");
        interactions.TimedOut!.Value.MessageId.ShouldBe(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        interactions.TimedOut.Value.ResponseKey.ShouldBe("reject");
    }

    [Fact]
    public async Task A_normal_click_resume_does_not_stamp_a_timeout()
    {
        var bot = new StubChatBot();
        var interactions = new StubInteractions();
        var decision = JsonDocument.Parse("""{ "action": "approve", "by": "u-1" }""").RootElement;

        await Node(bot, interactions).RunAsync(ContextFromInputs(new Dictionary<string, JsonElement>(), resumePayload: decision), CancellationToken.None);

        interactions.TimedOut.ShouldBeNull("a human click already stamps the card via the respond path — the node must not double-stamp");
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
