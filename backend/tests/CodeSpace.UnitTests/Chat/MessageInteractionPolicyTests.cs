using System.Text.Json;
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

    private static MessageInteraction FormCard(string fieldsJson) => new()
    {
        Component = new FormComponent { Fields = JsonDocument.Parse(fieldsJson).RootElement.Clone() },
        Target = new WorkflowWaitTarget { Token = "t" },
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

    [Fact]
    public void IsAllowedResponder_treats_an_empty_allow_list_as_nobody()
    {
        // A NON-null empty list means "explicitly nobody" (distinct from null = anyone). chat.post_message
        // collapses an empty actions allow-list to null, so this only arises for a directly-built card —
        // pin the strict semantics so a future caller can't accidentally open a card to everyone.
        var card = Card(allowed: Array.Empty<Guid>());

        MessageInteractionPolicy.IsAllowedResponder(card, Guid.NewGuid(), isConversationMember: true).ShouldBeFalse();
    }

    [Fact]
    public void IsValidResponse_for_a_form_accepts_only_the_submit_key()
    {
        var card = FormCard("""{"type":"object"}""");

        MessageInteractionPolicy.IsValidResponse(card, MessageInteractionPolicy.FormSubmitKey).ShouldBeTrue();
        MessageInteractionPolicy.IsValidResponse(card, "approve").ShouldBeFalse("a form has no button keys — only its submit");
    }

    [Fact]
    public void MissingRequiredFields_lists_absent_or_empty_required_form_fields()
    {
        var card = FormCard("""{"type":"object","required":["channel","note"]}""");

        var values = new Dictionary<string, JsonElement>
        {
            ["channel"] = JsonSerializer.SerializeToElement("c1"),
            ["note"] = JsonSerializer.SerializeToElement("   "),   // whitespace counts as empty
        };

        MessageInteractionPolicy.MissingRequiredFields(card, values).ShouldBe(new[] { "note" });
        MessageInteractionPolicy.MissingRequiredFields(card, null).ShouldBe(new[] { "channel", "note" }, customMessage: "no values ⇒ every required field is missing");
    }

    [Fact]
    public void MissingRequiredFields_is_empty_for_a_non_form_component()
    {
        MessageInteractionPolicy.MissingRequiredFields(Card(), null).ShouldBeEmpty("required-field validation only applies to a form");
    }

    [Fact]
    public void RequiresComment_is_true_only_for_a_button_that_demands_one()
    {
        var card = new MessageInteraction
        {
            Component = new ActionButtonsComponent
            {
                Buttons = new List<InteractionButton>
                {
                    new() { Key = "approve", Label = "Approve" },
                    new() { Key = "reject", Label = "Reject", RequiresComment = true },
                },
            },
            Target = new WorkflowWaitTarget { Token = "t" },
        };

        MessageInteractionPolicy.RequiresComment(card, "reject").ShouldBeTrue();
        MessageInteractionPolicy.RequiresComment(card, "approve").ShouldBeFalse();
        MessageInteractionPolicy.RequiresComment(card, "nonexistent").ShouldBeFalse();
    }

    [Fact]
    public void IsComment_recognises_only_the_reserved_comment_key()
    {
        MessageInteractionPolicy.IsComment(MessageInteractionPolicy.CommentKey).ShouldBeTrue();
        MessageInteractionPolicy.IsComment("approve").ShouldBeFalse();
        MessageInteractionPolicy.IsComment("").ShouldBeFalse();
    }

    [Fact]
    public void The_comment_key_is_not_a_component_option_so_it_can_never_be_mistaken_for_a_resolve()
    {
        // IsValidResponse gates the RESOLVE path; the comment key must NOT pass it (a comment is
        // non-terminal discussion, handled before validation), so it can't be taken for a button.
        var card = new MessageInteraction
        {
            Component = new ActionButtonsComponent { Buttons = new List<InteractionButton> { new() { Key = "approve", Label = "Approve" } } },
            Target = new WorkflowWaitTarget { Token = "t" },
        };

        MessageInteractionPolicy.IsValidResponse(card, MessageInteractionPolicy.CommentKey).ShouldBeFalse();
    }
}
