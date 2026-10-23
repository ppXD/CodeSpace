using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class ExternalTriggerIdempotencyTests
{
    [Theory]
    [InlineData("abc-123_XYZ.~")]
    public void A_well_formed_key_passes(string value) => ExternalTriggerIdempotency.IsWellFormed(value).ShouldBeTrue();

    [Theory]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("\u4e2d\u6587")]
    [InlineData("")]
    [InlineData(null)]
    public void A_key_outside_the_charset_or_absent_is_refused(string? value) => ExternalTriggerIdempotency.IsWellFormed(value).ShouldBeFalse();

    [Fact]
    public void An_over_long_key_is_refused() => ExternalTriggerIdempotency.IsWellFormed(new string('a', 201)).ShouldBeFalse();

    [Fact]
    public void A_maximum_length_key_passes() => ExternalTriggerIdempotency.IsWellFormed(new string('a', 200)).ShouldBeTrue();

    [Fact]
    public void The_key_scopes_to_the_trigger_so_two_triggers_never_share_it()
    {
        var a = ExternalTriggerIdempotency.KeyFor("evt-1", Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var b = ExternalTriggerIdempotency.KeyFor("evt-1", Guid.Parse("22222222-2222-2222-2222-222222222222"));
        a.ShouldNotBe(b);
        a.ShouldBe("trigger.external:evt-1:11111111111111111111111111111111");
    }
}
