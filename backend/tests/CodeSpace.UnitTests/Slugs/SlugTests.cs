using CodeSpace.Core.Services.Slugs;
using Shouldly;

namespace CodeSpace.UnitTests.Slugs;

/// <summary>
/// Pins <see cref="Slug.Slugify"/> — the one canonical transliteration now shared by Project / Workflow /
/// Agent / Skill. The corpus is the union of what the per-service tests used to assert, so a regression here
/// is a wire-contract change for <c>project.{slug}.X</c> and every @-mention handle.
/// </summary>
[Trait("Category", "Unit")]
public class SlugTests
{
    [Theory]
    [InlineData("Acme Backend", "acme-backend")]
    [InlineData("Backend Services", "backend-services")]
    [InlineData("My Product 2024!", "my-product-2024")]
    [InlineData("  Padded  Name  ", "padded-name")]
    [InlineData("Already-Kebab", "already-kebab")]
    [InlineData("snake_case_kept", "snake_case_kept")]   // underscore is a valid slug char
    [InlineData("---spaces---", "spaces")]                // leading/trailing/duplicate separators collapse + trim
    [InlineData("UPPER", "upper")]
    [InlineData("café münch", "caf-m-nch")]               // non-ASCII letters collapse to hyphens (slug is ASCII)
    [InlineData("Nightly Audit", "nightly-audit")]
    public void Slugify_produces_the_canonical_kebab_form(string name, string expected) =>
        Slug.Slugify(name).ShouldBe(expected);

    [Theory]
    [InlineData("$$$")]
    [InlineData("...")]
    [InlineData("- - -")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void Slugify_returns_empty_when_nothing_survives(string? name) =>
        Slug.Slugify(name).ShouldBe(string.Empty);

    [Fact]
    public void Slugify_caps_at_64_and_trims_a_trailing_hyphen_from_the_cut()
    {
        var name = new string('a', 40) + " " + new string('b', 40);   // 81 chars pre-cut

        var slug = Slug.Slugify(name);

        slug.Length.ShouldBeLessThanOrEqualTo(Slug.MaxLength);
        slug.ShouldNotEndWith("-");
        slug.ShouldStartWith("aaaa");
    }
}
