using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Runtime;

[Trait("Category", "Unit")]
public sealed class LegacyPlacementAdoptionCursorTests
{
    private readonly IDataProtector _protector = new EphemeralDataProtectionProvider()
        .CreateProtector(LegacyPlacementAdoptionCursor.ProtectorPurpose);

    [Fact]
    public void Minting_cursor_round_trips_the_durable_arc_revision_and_position()
    {
        var cursor = new LegacyPlacementAdoptionCursor
        {
            ProfileId = Guid.NewGuid(), ProfileRevision = 7, Mode = LegacyPlacementAdoptionCursorMode.Minting,
            ArcId = Guid.NewGuid(), ArcRevision = 19, Position = 402,
        };

        LegacyPlacementAdoptionCursor.TryDecode(cursor.Encode(_protector), cursor.ProfileId, _protector, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(cursor);
    }

    [Fact]
    public void Evidence_and_cleaning_modes_round_trip_without_exposing_manifest_identity()
    {
        var cursor = new LegacyPlacementAdoptionCursor
        {
            ProfileId = Guid.NewGuid(), ProfileRevision = 1, Mode = LegacyPlacementAdoptionCursorMode.Evidence,
            ArcId = Guid.NewGuid(), ArcRevision = 2, Position = 0,
        };

        LegacyPlacementAdoptionCursor.TryDecode(cursor.Encode(_protector), cursor.ProfileId, _protector, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(cursor);

        var cleaning = cursor with { Mode = LegacyPlacementAdoptionCursorMode.Cleaning, ArcRevision = 3, Position = 8 };
        LegacyPlacementAdoptionCursor.TryDecode(cleaning.Encode(_protector), cursor.ProfileId, _protector, out decoded).ShouldBeTrue();
        decoded.ShouldBe(cleaning);
    }

    [Fact]
    public void Cursor_is_bound_to_the_profile_and_rejects_invalid_or_oversized_wire()
    {
        var profileId = Guid.NewGuid();
        var cursor = new LegacyPlacementAdoptionCursor
        {
            ProfileId = profileId, ProfileRevision = 1, Mode = LegacyPlacementAdoptionCursorMode.Evidence,
            ArcId = Guid.NewGuid(), ArcRevision = 2, Position = 0,
        };

        LegacyPlacementAdoptionCursor.TryDecode(cursor.Encode(_protector), Guid.NewGuid(), _protector, out _).ShouldBeFalse();
        LegacyPlacementAdoptionCursor.TryDecode("not-a-cursor", profileId, _protector, out _).ShouldBeFalse();
        LegacyPlacementAdoptionCursor.TryDecode(new string('a', LegacyPlacementAdoptionCursor.MaximumEncodedLength + 1), profileId, _protector, out _).ShouldBeFalse();

        var wire = cursor.Encode(_protector);
        var index = wire.Length / 2;
        var replacement = wire[index] == 'a' ? 'b' : 'a';
        LegacyPlacementAdoptionCursor.TryDecode(wire[..index] + replacement + wire[(index + 1)..], profileId, _protector, out _).ShouldBeFalse(
            "a structurally plausible but modified protected cursor is invalid, not a caller-controlled snapshot");
    }

    [Theory]
    [InlineData("v1\n11111111111111111111111111111111\n1\n22222222222222222222222222222222\n2\ne\n0")]
    [InlineData("v2\n11111111111111111111111111111111\n0\n22222222222222222222222222222222\n2\ne\n0")]
    [InlineData("v2\n11111111111111111111111111111111\n1\n22222222222222222222222222222222\n0\ne\n0")]
    [InlineData("v2\n11111111111111111111111111111111\n1\n22222222222222222222222222222222\n2\nx\n0")]
    [InlineData("v2\n11111111111111111111111111111111\n1\n22222222222222222222222222222222\n2\ne\n-1")]
    public void Version_revision_mode_and_position_shape_fail_closed(string raw)
    {
        var encoded = _protector.Protect(raw);

        LegacyPlacementAdoptionCursor.TryDecode(encoded, Guid.ParseExact("11111111111111111111111111111111", "N"), _protector, out _).ShouldBeFalse();
    }
}
