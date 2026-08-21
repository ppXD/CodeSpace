using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

public class WorkflowRunCellFieldRangeContractTests
{
    [Fact]
    public void Cursor_is_versioned_bounded_identity_bound_and_accepts_long_max_without_overflow()
    {
        var identity = new WorkflowRunCellFieldRangeIdentity
        {
            RequestedRunId = Guid.NewGuid(), Scope = WorkflowRunViewScope.LineageMerged, SourceRunId = Guid.NewGuid(),
            NodeId = "node", IterationKey = string.Empty,
            Records = new WorkflowRunCellRecordIdentity(Guid.NewGuid(), 17, Guid.NewGuid(), 11),
            Section = WorkflowRunCellFieldSection.Output, Name = string.Empty,
        };
        var cursor = new WorkflowRunCellFieldRangeCursor(identity, long.MaxValue);

        var encoded = cursor.Encode();

        encoded.Length.ShouldBeLessThanOrEqualTo(WorkflowRunCellFieldRangeCursor.MaximumEncodedLength);
        WorkflowRunCellFieldRangeCursor.TryDecode(encoded, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(cursor);
        WorkflowRunCellFieldRangeCursor.TryDecode("not-a-cursor", out _).ShouldBeFalse();
        WorkflowRunCellFieldRangeCursor.TryDecode(new string('x', WorkflowRunCellFieldRangeCursor.MaximumEncodedLength + 1), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("A")]
    [InlineData("====")]
    [InlineData("💾")]
    [InlineData(" ")]
    public void Every_malformed_base64url_shape_is_rejected_without_escaping_the_closed_decoder(string value) =>
        WorkflowRunCellFieldRangeCursor.TryDecode(value, out _).ShouldBeFalse();

    [Fact]
    public void Inline_query_extracts_one_exact_leaf_window_and_never_returns_a_whole_payload()
    {
        WorkflowRunCellFieldRangeReader.InlineFieldSql.ShouldContain("convert_to", Case.Sensitive);
        WorkflowRunCellFieldRangeReader.InlineFieldSql.ShouldContain("substring", Case.Insensitive);
        WorkflowRunCellFieldRangeReader.InlineFieldSql.ShouldContain("@field_name", Case.Sensitive);
        WorkflowRunCellFieldRangeReader.InlineFieldSql.ShouldContain("@offset", Case.Sensitive);
        WorkflowRunCellFieldRangeReader.InlineFieldSql.ShouldContain("@take", Case.Sensitive);
        WorkflowRunCellFieldRangeReader.InlineFieldSql.ShouldNotContain("\nOFFSET ", Case.Insensitive);
        WorkflowRunCellFieldRangeReader.InlineFieldSql.ShouldNotContain("SELECT state.payload_json", Case.Insensitive);
        ReadWorkflowRunCellFieldRangeQuery.DefaultPageBytes.ShouldBe(64 * 1024);
        ReadWorkflowRunCellFieldRangeQuery.MaximumPageBytes.ShouldBe(64 * 1024);
        WorkflowRunCellFieldRangeReader.Utf8LookaheadBytes.ShouldBe(4);
    }
}
