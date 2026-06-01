using System.Text.Json;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class ChatPostMessageNodeTests
{
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

        var result = await new ChatPostMessageNode(bot).RunAsync(ctx, CancellationToken.None);

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
    public async Task Posts_a_plain_message_with_a_null_token_when_no_actions()
    {
        var bot = new StubChatBot();

        var result = await new ChatPostMessageNode(bot).RunAsync(BuildContext("11111111-1111-1111-1111-111111111111", "Deployed", actionsJson: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        bot.Interaction.ShouldBeNull("no actions ⇒ a plain announcement, not a card");
        result.Outputs["token"].ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Fails_when_conversation_id_is_missing()
    {
        var result = await new ChatPostMessageNode(new StubChatBot())
            .RunAsync(BuildContext(conversationId: null, body: "x", actionsJson: null), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("conversationId");
    }

    private static NodeRunContext BuildContext(string? conversationId, string body, string? actionsJson)
    {
        var inputs = new Dictionary<string, JsonElement> { ["body"] = JsonSerializer.SerializeToElement(body) };
        if (conversationId != null) inputs["conversationId"] = JsonSerializer.SerializeToElement(conversationId);
        if (actionsJson != null) inputs["actions"] = JsonDocument.Parse(actionsJson).RootElement.Clone();

        return new NodeRunContext
        {
            Inputs = inputs,
            Config = new Dictionary<string, JsonElement>(),
            RawInputs = JsonDocument.Parse("{}").RootElement,
            RawConfig = JsonDocument.Parse("{}").RootElement,
            Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
            Logger = NullLogger.Instance,
            Observability = NodeObservability.NoOp,
        };
    }
}
