using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins <see cref="AgentDefinitionService.DeriveSlug"/> — the pure derivation of an Agent persona's
/// @-mention handle from its name. The handle is a stable contract (an <c>agent.run</c> node and a
/// chat @-mention resolve against it), so the shape is locked here: lowercase, ASCII <c>[a-z0-9_]</c>
/// kept, every other run collapses to a single hyphen, leading/trailing hyphens trimmed, capped at 64,
/// empty when nothing usable survives (the service turns that into an actionable error).
/// </summary>
[Trait("Category", "Unit")]
public class AgentDefinitionSlugTests
{
    [Theory]
    [InlineData("Backend Architect", "backend-architect")]
    [InlineData("Code Reviewer", "code-reviewer")]
    [InlineData("My Agent 2024!", "my-agent-2024")]
    [InlineData("  Padded  Name  ", "padded-name")]
    [InlineData("Already-Kebab", "already-kebab")]
    [InlineData("snake_case_kept", "snake_case_kept")]      // underscore is a valid handle char
    [InlineData("---spaces---", "spaces")]                   // leading/trailing/duplicate separators collapse + trim
    [InlineData("UPPER", "upper")]
    [InlineData("café münch", "caf-m-nch")]                  // non-ASCII letters collapse to hyphens (handle is ASCII)
    public void DeriveSlug_produces_a_stable_kebab_handle(string name, string expected)
    {
        AgentDefinitionService.DeriveSlug(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("$$$")]      // all-punctuation → nothing survives
    [InlineData("...")]
    [InlineData("- - -")]
    [InlineData("   ")]
    [InlineData("")]
    public void DeriveSlug_returns_empty_when_no_handle_characters_survive(string name)
    {
        AgentDefinitionService.DeriveSlug(name).ShouldBe(string.Empty);
    }

    [Fact]
    public void DeriveSlug_caps_at_64_characters_and_trims_a_trailing_hyphen()
    {
        var name = new string('a', 70);

        AgentDefinitionService.DeriveSlug(name).Length.ShouldBe(64);
    }

    [Fact]
    public void DeriveSlug_does_not_leave_a_trailing_hyphen_after_the_64_char_cut()
    {
        // 63 'a's then a space then more letters: the cut lands right after the space-derived hyphen,
        // which must be trimmed so the handle never ends in '-'.
        var name = new string('a', 63) + " extra";

        var slug = AgentDefinitionService.DeriveSlug(name);

        slug.ShouldBe(new string('a', 63));
        slug.EndsWith('-').ShouldBeFalse("a derived handle must never end in a hyphen, even after the length cap");
    }
}
