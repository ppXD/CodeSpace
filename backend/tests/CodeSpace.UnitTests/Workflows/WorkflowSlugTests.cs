using CodeSpace.Core.Services.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins <see cref="WorkflowService.SlugifyName"/> — the pure derivation of a workflow's clean-URL
/// handle from its name. Shape is locked here because it must stay byte-identical to the 0099
/// migration's backfill SQL (<c>regexp_replace(lower(name), '[^a-z0-9_]+', '-', 'g')</c> + trim +
/// cap 64): lowercase, ASCII <c>[a-z0-9_]</c> kept, every other run collapses to a single hyphen,
/// leading/trailing hyphens trimmed, capped at 64. Unlike the agent/project slug, an empty result
/// falls back to <c>"workflow"</c> (a workflow name is free text, so we never reject it).
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowSlugTests
{
    [Theory]
    [InlineData("Nightly Audit", "nightly-audit")]
    [InlineData("Deploy", "deploy")]
    [InlineData("My Product 2024!", "my-product-2024")]
    [InlineData("  Padded  Name  ", "padded-name")]
    [InlineData("Already-Kebab", "already-kebab")]
    [InlineData("snake_case_kept", "snake_case_kept")]   // underscore is a valid slug char
    [InlineData("---spaces---", "spaces")]                // leading/trailing/duplicate separators collapse + trim
    [InlineData("UPPER", "upper")]
    [InlineData("café münch", "caf-m-nch")]               // non-ASCII letters collapse to hyphens (slug is ASCII)
    public void SlugifyName_produces_a_stable_kebab_handle(string name, string expected)
    {
        WorkflowService.SlugifyName(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("$$$")]      // all-punctuation → nothing survives → fallback
    [InlineData("...")]
    [InlineData("- - -")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void SlugifyName_falls_back_to_workflow_when_no_characters_survive(string? name)
    {
        WorkflowService.SlugifyName(name).ShouldBe("workflow");
    }

    [Fact]
    public void SlugifyName_caps_at_64_characters_and_trims_a_trailing_hyphen_from_the_cut()
    {
        // 40 'a' + space + 40 'b' → "aaaa…-bbbb…" would be 81 chars; cut to 64 must not leave a
        // dangling hyphen and must stay within the DB VARCHAR(64) + CHECK bound.
        var name = new string('a', 40) + " " + new string('b', 40);

        var slug = WorkflowService.SlugifyName(name);

        slug.Length.ShouldBeLessThanOrEqualTo(64);
        slug.ShouldNotEndWith("-");
        slug.ShouldStartWith("aaaa");
    }
}
