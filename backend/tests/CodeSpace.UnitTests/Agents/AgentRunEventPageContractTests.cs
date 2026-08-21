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
    }

    private static List<ValidationResult> ValidationErrors(PageAgentRunEventsQuery query)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(query, new ValidationContext(query), errors, validateAllProperties: true);
        return errors;
    }
}
