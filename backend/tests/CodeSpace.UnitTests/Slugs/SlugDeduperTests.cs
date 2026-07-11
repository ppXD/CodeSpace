using CodeSpace.Core.Services.Slugs;
using Shouldly;

namespace CodeSpace.UnitTests.Slugs;

/// <summary>
/// Pins <see cref="SlugDeduper.DeriveAvailable"/> — the one auto-suffix loop shared by Workflow / Agent / Skill.
/// The trim-to-fit math is coupled to the 64-char slug column + unique index, so it is asserted here once instead
/// of trusting three hand-copies to stay in lockstep.
/// </summary>
[Trait("Category", "Unit")]
public class SlugDeduperTests
{
    private static IReadOnlySet<string> Set(params string[] s) => new HashSet<string>(s, StringComparer.Ordinal);

    [Fact]
    public void Returns_the_base_when_free() =>
        SlugDeduper.DeriveAvailable("deploy", Set()).ShouldBe("deploy");

    [Fact]
    public void Appends_2_when_the_base_is_taken() =>
        SlugDeduper.DeriveAvailable("deploy", Set("deploy")).ShouldBe("deploy-2");

    [Fact]
    public void Skips_to_3_when_the_base_and_2_are_taken() =>
        SlugDeduper.DeriveAvailable("deploy", Set("deploy", "deploy-2")).ShouldBe("deploy-3");

    [Fact]
    public void A_reserved_base_is_pushed_to_a_suffix_even_when_not_taken() =>
        SlugDeduper.DeriveAvailable("runs", Set(), reserved: Set("runs")).ShouldBe("runs-2");

    [Fact]
    public void A_reserved_variant_is_skipped_too() =>
        SlugDeduper.DeriveAvailable("runs", Set("runs"), reserved: Set("runs", "runs-2")).ShouldBe("runs-3");

    [Fact]
    public void Trims_the_base_so_the_numeric_suffix_stays_within_64()
    {
        var base64 = new string('a', Slug.MaxLength);   // exactly the column max

        var result = SlugDeduper.DeriveAvailable(base64, Set(base64));

        result.Length.ShouldBeLessThanOrEqualTo(Slug.MaxLength);
        result.ShouldEndWith("-2");
    }
}
