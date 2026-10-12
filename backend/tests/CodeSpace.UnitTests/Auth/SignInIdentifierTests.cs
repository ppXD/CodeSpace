using CodeSpace.Core.Services.Users;
using Shouldly;

namespace CodeSpace.UnitTests.Auth;

/// <summary>
/// 🟢 Unit: the comparison key sign-in uses for an address and a display name, which the check that keeps a new
/// display name unambiguous must share — a name that passed one comparison and collided under the other would be
/// exactly the shadow account the check exists to refuse.
/// </summary>
[Trait("Category", "Unit")]
public class SignInIdentifierTests
{
    [Theory]
    [InlineData("  Victim@Corp.Test ", "victim@corp.test")]
    [InlineData("Maya Chen", "maya chen")]
    [InlineData("\tADMIN\n", "admin")]
    public void Identifiers_compare_trimmed_and_ignoring_case(string typed, string key)
    {
        SignInIdentifier.Normalize(typed).ShouldBe(key);
    }

    [Theory]
    [InlineData("victim@corp.test", true)]
    [InlineData("@victim", true)]
    [InlineData("Maya @ Platform", true)]
    [InlineData("Maya Chen", false)]
    [InlineData("victim.corp.test", false)]
    public void A_name_with_an_at_sign_could_stand_in_for_an_address(string displayName, bool looksLikeEmail)
    {
        SignInIdentifier.LooksLikeEmail(displayName).ShouldBe(looksLikeEmail);
    }
}
