using System.ComponentModel.DataAnnotations;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public sealed class AgentRunEventPageContractTests
{
    [Theory]
    [InlineData(AgentRunEventPageDirection.Tail, null, 200, true)]
    [InlineData(AgentRunEventPageDirection.Older, "1", 1, true)]
    [InlineData(AgentRunEventPageDirection.Older, "9223372036854775807", 500, true)]
    [InlineData(AgentRunEventPageDirection.Newer, "0", 200, true)]
    [InlineData(AgentRunEventPageDirection.Newer, "42", 200, true)]
    [InlineData(AgentRunEventPageDirection.Tail, "1", 200, false)]
    [InlineData(AgentRunEventPageDirection.Older, null, 200, false)]
    [InlineData(AgentRunEventPageDirection.Older, "0", 200, false)]
    [InlineData(AgentRunEventPageDirection.Older, "-1", 200, false)]
    [InlineData(AgentRunEventPageDirection.Newer, null, 200, false)]
    [InlineData(AgentRunEventPageDirection.Newer, "-1", 200, false)]
    [InlineData(AgentRunEventPageDirection.Newer, "+1", 200, false)]
    [InlineData(AgentRunEventPageDirection.Newer, " 1", 200, false)]
    [InlineData(AgentRunEventPageDirection.Newer, "9223372036854775808", 200, false)]
    [InlineData(AgentRunEventPageDirection.Tail, null, 0, false)]
    [InlineData(AgentRunEventPageDirection.Tail, null, 501, false)]
    [InlineData((AgentRunEventPageDirection)99, null, 200, false)]
    public void Direction_cursor_and_hard_limit_form_one_closed_request_shape(AgentRunEventPageDirection direction, string? cursor, int limit, bool valid)
    {
        var query = new PageAgentRunEventsQuery { AgentRunId = Guid.NewGuid(), Direction = direction, Cursor = cursor, Limit = limit };

        ValidationErrors(query).Count.ShouldBe(valid ? 0 : 1);
    }

    [Fact]
    public void Validated_cursor_is_the_exact_sequence_without_arithmetic_or_loss()
    {
        var query = new PageAgentRunEventsQuery
        {
            AgentRunId = Guid.NewGuid(), Direction = AgentRunEventPageDirection.Newer,
            Cursor = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        query.TryGetCursor(out var cursor).ShouldBeTrue();
        cursor.ShouldBe(long.MaxValue);
    }

    [Fact]
    public void Wire_defaults_are_bounded_and_pinned()
    {
        var query = new PageAgentRunEventsQuery { AgentRunId = Guid.NewGuid() };

        query.Direction.ShouldBe(AgentRunEventPageDirection.Tail);
        query.Limit.ShouldBe(PageAgentRunEventsQuery.DefaultPageSize);
        PageAgentRunEventsQuery.MaximumPageSize.ShouldBe(500);
        PageAgentRunEventsQuery.MaximumKindFilterLength.ShouldBe(128);
        query.KindFilter.ShouldBeNull();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("ToolCall", true)]
    [InlineData("FutureHarnessEvent", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Kind_filter_is_optional_exact_and_open_but_never_blank(string? kindFilter, bool valid)
    {
        var query = new PageAgentRunEventsQuery { AgentRunId = Guid.NewGuid(), KindFilter = kindFilter };

        ValidationErrors(query).Count.ShouldBe(valid ? 0 : 1);
    }

    [Fact]
    public void Kind_filter_has_a_hard_wire_length_cap()
    {
        var valid = new PageAgentRunEventsQuery { AgentRunId = Guid.NewGuid(), KindFilter = new string('x', PageAgentRunEventsQuery.MaximumKindFilterLength) };
        var invalid = valid with { KindFilter = new string('x', PageAgentRunEventsQuery.MaximumKindFilterLength + 1) };

        ValidationErrors(valid).ShouldBeEmpty();
        ValidationErrors(invalid).Count.ShouldBe(1);
    }

    private static List<ValidationResult> ValidationErrors(PageAgentRunEventsQuery query)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(query, new ValidationContext(query), errors, validateAllProperties: true);
        return errors;
    }
}
