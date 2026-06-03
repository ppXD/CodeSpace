using System.Text.Json;
using CodeSpace.Core.Services.Chat.Interactions;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using Shouldly;

namespace CodeSpace.UnitTests.Chat;

/// <summary>
/// The component-factory registry: chat.post_message stays a thin shell — building a card is dispatched by
/// <c>kind</c> to a pluggable factory. Pins each factory's parse (incl. the resolve flags) + the registry's
/// dispatch / graceful-null behaviour, so a new kind is a new factory with no node change.
/// </summary>
[Trait("Category", "Unit")]
public class InteractionComponentRegistryTests
{
    private static readonly IInteractionComponentRegistry Registry =
        new InteractionComponentRegistry(new IInteractionComponentFactory[] { new ActionButtonsComponentFactory(), new FormComponentFactory() });

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    // ── Action-buttons factory ──────────────────────────────────────────────────

    [Fact]
    public void Builds_action_buttons_with_resolve_flags_defaulting_terminal()
    {
        var component = Registry.Build(Json("""
            { "kind": "action_buttons", "buttons": [
                { "key": "approve", "label": "Approve" },
                { "key": "note", "label": "Comment", "resolvesWait": false },
                { "key": "reject", "label": "Reject", "style": "Danger", "vetoes": true, "requiresComment": true }
            ] }
            """));

        var buttons = component.ShouldBeOfType<ActionButtonsComponent>().Buttons;
        buttons.Count.ShouldBe(3);
        var approve = buttons.Single(b => b.Key == "approve");
        approve.ResolvesWait.ShouldBeTrue("a button is terminal by default");
        approve.Vetoes.ShouldBeFalse();
        buttons.Single(b => b.Key == "note").ResolvesWait.ShouldBeFalse();
        var reject = buttons.Single(b => b.Key == "reject");
        reject.Vetoes.ShouldBeTrue();
        reject.RequiresComment.ShouldBeTrue();
        reject.Style.ShouldBe(InteractionButtonStyle.Danger);
    }

    [Fact]
    public void Skips_buttons_missing_a_key_or_label()
    {
        var component = Registry.Build(Json("""
            { "kind": "action_buttons", "buttons": [ { "key": "ok", "label": "OK" }, { "key": "x" }, { "label": "no key" } ] }
            """));

        component.ShouldBeOfType<ActionButtonsComponent>().Buttons.Select(b => b.Key).ShouldBe(new[] { "ok" });
    }

    [Fact]
    public void Returns_null_for_action_buttons_with_no_valid_buttons() =>
        Registry.Build(Json("""{ "kind": "action_buttons", "buttons": [] }""")).ShouldBeNull();

    // ── Form factory ────────────────────────────────────────────────────────────

    [Fact]
    public void Builds_a_form_with_its_fields_and_submit_label()
    {
        var form = Registry.Build(Json("""
            { "kind": "form", "fields": { "type": "object", "properties": { "env": { "type": "string" } } }, "submitLabel": "Deploy" }
            """)).ShouldBeOfType<FormComponent>();

        form.SubmitLabel.ShouldBe("Deploy");
        form.Fields.GetProperty("properties").TryGetProperty("env", out _).ShouldBeTrue();
    }

    [Fact]
    public void Defaults_the_form_submit_label_and_rejects_a_form_without_fields()
    {
        Registry.Build(Json("""{ "kind": "form", "fields": { "type": "object" } }""")).ShouldBeOfType<FormComponent>().SubmitLabel.ShouldBe("Submit");
        Registry.Build(Json("""{ "kind": "form" }""")).ShouldBeNull("a form with no fields can't render");
    }

    // ── Registry dispatch ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{ "kind": "poll", "options": [] }""")]   // unknown kind (no factory yet)
    [InlineData("""{ "buttons": [] }""")]                    // no kind discriminator
    [InlineData("""[ 1, 2 ]""")]                              // not an object
    public void Returns_null_when_no_factory_matches_the_kind(string raw) =>
        Registry.Build(Json(raw)).ShouldBeNull();
}
