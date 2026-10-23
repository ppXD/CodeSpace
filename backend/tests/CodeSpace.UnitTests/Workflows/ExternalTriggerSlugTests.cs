using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class ExternalTriggerSlugTests
{
    [Fact]
    public void Type_key_is_the_external_trigger_identifier() =>
        ExternalTriggerSlug.TypeKey.ShouldBe("trigger.external");

    [Theory]
    [InlineData("prod 错误 报警", "prod")]
    [InlineData("../../etc/passwd", "etc-passwd")]
    [InlineData("Prod-Alerts", "prod-alerts")]
    [InlineData("日本語", "")]
    public void Derive_normalises_author_input(string input, string expected) =>
        ExternalTriggerSlug.Derive(input).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_unusable_name_is_not_a_slug(string? derived) =>
        ExternalTriggerSlug.IsUsable(derived).ShouldBeFalse();

    [Fact]
    public void A_nonblank_derived_slug_is_usable() =>
        ExternalTriggerSlug.IsUsable("prod-alerts").ShouldBeTrue();
}
