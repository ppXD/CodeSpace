using System.Buffers.Text;
using System.Globalization;
using System.Text;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Dtos.Sessions;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

[Trait("Category", "Unit")]
public sealed class SessionRunMetadataCursorTests
{
    [Fact]
    public void Request_defaults_and_hard_limits_are_explicit()
    {
        var request = new SessionRunMetadataPageRequest { TeamId = Guid.NewGuid(), Selector = new SessionRunMetadataSelector { Kind = SessionRunMetadataSelectorKind.Session, SessionId = Guid.NewGuid() } };

        request.Limit.ShouldBe(128);
        SessionRunMetadataPageRequest.MaximumLimit.ShouldBe(256);
        SessionRunMetadataPageRequest.MaximumCursorLength.ShouldBe(512);
    }

    [Fact]
    public void V1_round_trips_every_membership_coordinate_across_cultures()
    {
        var cursor = new SessionRunMetadataCursor(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), MembershipHeadRunNumber: 12_345, BeforeRunNumber: 12_000);
        var previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-EG");
            SessionRunMetadataCursor.Decode(cursor.Encode()).ShouldBe(cursor);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void V1_round_trips_a_direct_session_selector_without_an_anchor()
    {
        var cursor = new SessionRunMetadataCursor(Guid.NewGuid(), Guid.NewGuid(), RunAnchorId: null, MembershipHeadRunNumber: 8, BeforeRunNumber: 3);

        SessionRunMetadataCursor.Decode(cursor.Encode()).ShouldBe(cursor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64")]
    public void Invalid_wire_fails_closed(string wire) => Should.Throw<InvalidOperationException>(() => SessionRunMetadataCursor.Decode(wire));

    [Fact]
    public void Overlength_wire_fails_before_decode() =>
        Should.Throw<InvalidOperationException>(() => SessionRunMetadataCursor.Decode(new string('a', SessionRunMetadataPageRequest.MaximumCursorLength + 1)));

    [Theory]
    [InlineData("v2\n11111111111111111111111111111111\n22222222222222222222222222222222\n-\n8\n3")]
    [InlineData("v1\n11111111111111111111111111111111\n22222222222222222222222222222222\n-\n0\n0")]
    [InlineData("v1\n11111111111111111111111111111111\n22222222222222222222222222222222\n-\n8\n9")]
    public void Structurally_invalid_v1_wire_fails_closed(string raw)
    {
        var wire = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));

        Should.Throw<InvalidOperationException>(() => SessionRunMetadataCursor.Decode(wire));
    }
}
