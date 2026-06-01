using CodeSpace.Messages.Dtos.Chat.Interactions;
using Shouldly;

namespace CodeSpace.UnitTests.Chat;

/// <summary>
/// The interactive-message model is stored as polymorphic jsonb (component / target discriminated by
/// <c>kind</c>). This pins the serialize → store → deserialize contract: the right derived types come
/// back, enums are stable strings, and the discriminators are the exact wire values the frontend +
/// the respond endpoint key off. A drift here silently breaks every stored card.
/// </summary>
[Trait("Category", "Unit")]
public class MessageInteractionJsonTests
{
    private static MessageInteraction SampleButtons(InteractionState state = InteractionState.Open, InteractionResolution? resolution = null) => new()
    {
        Component = new ActionButtonsComponent
        {
            Buttons = new List<InteractionButton>
            {
                new() { Key = "approve", Label = "Approve", Style = InteractionButtonStyle.Primary },
                new() { Key = "request_changes", Label = "Request changes", Style = InteractionButtonStyle.Danger, RequiresComment = true },
                new() { Key = "snooze", Label = "Snooze" },
            },
        },
        Target = new WorkflowWaitTarget { Token = "tok-123" },
        AllowedResponderUserIds = new List<Guid> { Guid.Parse("11111111-1111-1111-1111-111111111111") },
        State = state,
        Resolution = resolution,
    };

    [Fact]
    public void Round_trips_the_polymorphic_component_and_target()
    {
        var json = MessageInteractionJson.Serialize(SampleButtons());

        json.ShouldNotBeNull();
        json.ShouldContain("\"kind\":\"action_buttons\"", customMessage: "the component discriminator is the wire contract the frontend switches on");
        json.ShouldContain("\"kind\":\"workflow_wait\"", customMessage: "the target discriminator routes the response server-side");
        json.ShouldContain("\"style\":\"Danger\"", customMessage: "enums serialize as stable strings, not ordinals");

        var back = MessageInteractionJson.Deserialize(json);

        var component = back!.Component.ShouldBeOfType<ActionButtonsComponent>();
        component.Buttons.Count.ShouldBe(3);
        component.Buttons[0].Key.ShouldBe("approve");
        component.Buttons[0].Style.ShouldBe(InteractionButtonStyle.Primary);
        component.Buttons[1].RequiresComment.ShouldBeTrue();
        component.Buttons[2].Style.ShouldBe(InteractionButtonStyle.Default, customMessage: "an omitted style defaults to Default");

        back.Target.ShouldBeOfType<WorkflowWaitTarget>().Token.ShouldBe("tok-123");
        back.State.ShouldBe(InteractionState.Open);
        back.AllowedResponderUserIds.ShouldHaveSingleItem().ShouldBe(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        back.Resolution.ShouldBeNull();
    }

    [Fact]
    public void Round_trips_a_resolved_interaction()
    {
        var resolution = new InteractionResolution
        {
            ResponseKey = "approve",
            ByUserId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Comment = "looks good",
            AtUtc = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
        };

        var back = MessageInteractionJson.Deserialize(MessageInteractionJson.Serialize(SampleButtons(InteractionState.Resolved, resolution)));

        back!.State.ShouldBe(InteractionState.Resolved);
        back.Resolution.ShouldNotBeNull();
        back.Resolution!.ResponseKey.ShouldBe("approve");
        back.Resolution.ByUserId.ShouldBe(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        back.Resolution.Comment.ShouldBe("looks good");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_round_trips_to_null(string? json)
    {
        MessageInteractionJson.Deserialize(json).ShouldBeNull();
    }

    [Fact]
    public void Null_model_serializes_to_null()
    {
        MessageInteractionJson.Serialize(null).ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"version":1,"component":{"kind":"hologram"},"target":{"kind":"workflow_wait","token":"t"},"state":"Open"}""")]  // unknown component kind (newer/forked server)
    [InlineData("""{"version":1,"component":{"kind":"action_buttons","buttons":[]},"target":{"kind":"smoke_signal"},"state":"Open"}""")] // unknown target kind
    [InlineData("{ this is not valid json")]                                                                                          // malformed
    public void TryDeserialize_degrades_to_null_for_an_unknown_kind_or_malformed_json(string json)
    {
        // A card we can't render must degrade to null on the read path — never throw + brick the
        // whole conversation's message list.
        Should.NotThrow(() => MessageInteractionJson.TryDeserialize(json).ShouldBeNull());
    }

    [Fact]
    public void TryDeserialize_returns_the_model_for_a_valid_document()
    {
        MessageInteractionJson.TryDeserialize(MessageInteractionJson.Serialize(SampleButtons())).ShouldNotBeNull();
    }
}
