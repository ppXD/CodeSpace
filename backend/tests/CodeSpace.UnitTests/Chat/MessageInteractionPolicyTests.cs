using CodeSpace.Messages.Dtos.Chat.Interactions;
using Shouldly;

namespace CodeSpace.UnitTests.Chat;

[Trait("Category", "Unit")]
public class MessageInteractionPolicyTests
{
    private static MessageInteraction Card(IReadOnlyList<Guid>? allowed = null) => new()
    {
        Component = new ActionButtonsComponent
        {
            Buttons = new List<InteractionButton> { new() { Key = "approve", Label = "Approve" }, new() { Key = "reject", Label = "Reject" } },
        },
        Target = new WorkflowWaitTarget { Token = "t" },
        AllowedResponderUserIds = allowed,
    };

    [Theory]
    [InlineData("approve", true)]
    [InlineData("reject", true)]
    [InlineData("snooze", false)]
    [InlineData("", false)]
    public void IsValidResponse_matches_a_button_key(string key, bool expected)
    {
        MessageInteractionPolicy.IsValidResponse(Card(), key).ShouldBe(expected);
    }

    [Fact]
    public void IsAllowedResponder_requires_conversation_membership()
    {
        MessageInteractionPolicy.IsAllowedResponder(Card(), Guid.NewGuid(), isConversationMember: false)
            .ShouldBeFalse("a non-member can never respond, even with a null allow-list");

        MessageInteractionPolicy.IsAllowedResponder(Card(), Guid.NewGuid(), isConversationMember: true)
            .ShouldBeTrue("a null allow-list ⇒ any active conversation member may respond");
    }

    [Fact]
    public void IsAllowedResponder_restricts_to_the_allow_list_when_set()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var card = Card(allowed: new[] { alice });

        MessageInteractionPolicy.IsAllowedResponder(card, alice, isConversationMember: true).ShouldBeTrue();
        MessageInteractionPolicy.IsAllowedResponder(card, bob, isConversationMember: true)
            .ShouldBeFalse("a restricted card may only be answered by a listed user (e.g. the picked reviewer)");
    }
}
