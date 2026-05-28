using CodeSpace.Core.Services.Chat;
using Shouldly;

namespace CodeSpace.UnitTests.Chat;

/// <summary>
/// Pins the message-body character cap. The number is a product/operator-visible contract — a
/// silent shrink would start rejecting messages that used to post fine, and a silent removal
/// would reopen the unbounded-body risk. Hard-pin so any change to it is a deliberate edit.
/// </summary>
[Trait("Category", "Unit")]
public class MessageServiceLimitsTests
{
    [Fact]
    public void MaxBodyLength_is_pinned()
    {
        MessageService.MaxBodyLength.ShouldBe(16_000);
    }
}
