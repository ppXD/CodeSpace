using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSpace.Messages.Dtos.Invitations;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Contracts;

/// <summary>
/// The invitation preview as the SPA reads it. The page that shipped before <c>ForSignedInAccount</c> reads
/// <c>accountExists</c>, and the API is always replaced before the SPA, so for the length of a rollout (and for any tab
/// opened before it) that page meets this response. Without the old name it reads <c>undefined</c> and asks a signed-in
/// invitee to "Create your account" for an address that already has one.
/// </summary>
[Trait("Category", "Unit")]
public sealed class InvitationPreviewWireTests
{
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_old_name_is_on_the_wire_and_says_exactly_what_the_new_one_says(bool forSignedInAccount)
    {
        var wire = JsonSerializer.SerializeToElement(Preview(forSignedInAccount), Api);

        wire.GetProperty("forSignedInAccount").GetBoolean().ShouldBe(forSignedInAccount, "fixture check: the new name carries the fact");
        wire.GetProperty("accountExists").GetBoolean().ShouldBe(forSignedInAccount, "the old name is the new fact, never the answer to 'does this address have an account', which was an existence oracle");
    }

    [Fact]
    public void The_old_name_is_marked_obsolete_so_the_next_release_removes_it_on_purpose()
    {
        var alias = typeof(InvitationPreview).GetProperty("AccountExists", BindingFlags.Public | BindingFlags.Instance).ShouldNotBeNull("the alias the previous SPA bundle reads");

        alias.GetCustomAttribute<ObsoleteAttribute>().ShouldNotBeNull("an alias nobody is told is temporary is a second field forever");
        alias.CanWrite.ShouldBeFalse("it is derived from ForSignedInAccount, so the two can never disagree");
    }

    private static InvitationPreview Preview(bool forSignedInAccount) => new()
    {
        TeamName = "Platform", InvitedByName = "Mars", Role = TeamRole.Member, Email = "maya@team.dev", ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), ForSignedInAccount = forSignedInAccount,
    };
}
