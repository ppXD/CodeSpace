using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Buffers.Text;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Messages.Queries.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public sealed class ToolCallPageContractTests
{
    [Theory]
    [InlineData(ToolCallPageDirection.Tail, null, 128, true)]
    [InlineData(ToolCallPageDirection.Older, "opaque", 1, true)]
    [InlineData(ToolCallPageDirection.Tail, "opaque", 128, false)]
    [InlineData(ToolCallPageDirection.Older, null, 128, false)]
    [InlineData(ToolCallPageDirection.Older, "", 128, false)]
    [InlineData(ToolCallPageDirection.Older, " ", 128, false)]
    [InlineData(ToolCallPageDirection.Tail, null, 0, false)]
    [InlineData(ToolCallPageDirection.Tail, null, 501, false)]
    [InlineData((ToolCallPageDirection)99, null, 128, false)]
    public void Direction_cursor_and_hard_limit_form_one_closed_request_shape(ToolCallPageDirection direction, string? cursor, int limit, bool valid)
    {
        var query = new PageToolCallsQuery { AgentRunId = Guid.NewGuid(), Direction = direction, Cursor = cursor, Limit = limit };

        ValidationErrors(query).Count.ShouldBe(valid ? 0 : 1);
    }

    [Fact]
    public void Wire_defaults_are_bounded_and_pinned()
    {
        var query = new PageToolCallsQuery { AgentRunId = Guid.NewGuid() };

        query.Direction.ShouldBe(ToolCallPageDirection.Tail);
        query.Limit.ShouldBe(128);
        PageToolCallsQuery.MaximumPageSize.ShouldBe(500);
        PageToolCallsQuery.MaximumCursorLength.ShouldBe(256);
    }

    [Fact]
    public void Opaque_cursor_round_trips_created_date_and_postgres_uuid_tiebreaker()
    {
        var original = new ToolCallAuditCursor(new DateTimeOffset(2026, 8, 21, 4, 3, 2, 1, TimeSpan.Zero), Guid.NewGuid());

        var decoded = ToolCallAuditCursor.Decode(original.Encode());

        decoded.ShouldBe(original);
        original.Encode().ShouldNotContain(original.Id.ToString("N"), customMessage: "the wire token is opaque base64url, never a structured public tuple");
    }

    [Fact]
    public void Opaque_cursor_has_an_explicit_wire_version()
    {
        var cursor = new ToolCallAuditCursor(DateTimeOffset.UtcNow, Guid.NewGuid()).Encode();

        var wire = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));

        wire.Split('\n')[0].ShouldBe("v1");
    }

    [Fact]
    public void Opaque_cursor_round_trip_is_invariant_across_replica_cultures()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var original = new ToolCallAuditCursor(new DateTimeOffset(2026, 8, 21, 4, 3, 2, 1, TimeSpan.Zero), Guid.Parse("fd767ca1-a268-4a5a-87a8-8161f0ac8530"));

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-SA");
            var encoded = original.Encode();

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

            ToolCallAuditCursor.Decode(encoded).ShouldBe(original);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData("not-base64url!!")]
    [InlineData("Zm9v")]
    [InlineData("")]
    [InlineData("   ")]
    public void Malformed_opaque_cursor_is_rejected_not_reset_to_tail(string cursor)
    {
        Should.Throw<InvalidOperationException>(() => ToolCallAuditCursor.Decode(cursor));
    }

    [Fact]
    public void Cursor_decoder_rejects_oversized_input_before_base64_decoding()
    {
        var oversized = new string('!', PageToolCallsQuery.MaximumCursorLength + 1);

        var exception = Should.Throw<InvalidOperationException>(() => ToolCallAuditCursor.Decode(oversized));

        exception.Message.ShouldContain("too long");
    }

    [Fact]
    public void Cursor_decoder_rejects_unknown_wire_versions()
    {
        var wire = $"v2\n{DateTimeOffset.UtcNow.UtcTicks.ToString(CultureInfo.InvariantCulture)}\n{Guid.NewGuid():N}";
        var encoded = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(wire));

        Should.Throw<InvalidOperationException>(() => ToolCallAuditCursor.Decode(encoded));
    }

    private static List<ValidationResult> ValidationErrors(PageToolCallsQuery query)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(query, new ValidationContext(query), errors, validateAllProperties: true);
        return errors;
    }
}
