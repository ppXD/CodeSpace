using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

public class WorkflowRunCellFieldDescriptorContractTests
{
    [Fact]
    public void Cursor_is_versioned_bounded_and_round_trips_empty_property_names()
    {
        var cursor = new WorkflowRunCellFieldCursor(new WorkflowRunCellRecordIdentity(Guid.NewGuid(), 17, Guid.NewGuid(), 41),
            WorkflowRunCellFieldSection.Output, string.Empty);

        var encoded = cursor.Encode();

        encoded.Length.ShouldBeLessThanOrEqualTo(WorkflowRunCellFieldCursor.MaximumEncodedLength);
        WorkflowRunCellFieldCursor.TryDecode(encoded, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(cursor);
        WorkflowRunCellFieldCursor.TryDecode(" ", out _).ShouldBeFalse();
        WorkflowRunCellFieldCursor.TryDecode("not-a-v1-cursor", out _).ShouldBeFalse();
        WorkflowRunCellFieldCursor.TryDecode(new string('x', WorkflowRunCellFieldCursor.MaximumEncodedLength + 1), out _).ShouldBeFalse();
    }

    [Fact]
    public void Descriptor_query_is_keyset_bounded_and_never_selects_a_payload_or_field_body()
    {
        WorkflowRunCellFieldReader.FieldSql.ShouldContain("COLLATE \"C\"", Case.Sensitive);
        WorkflowRunCellFieldReader.FieldSql.ShouldContain("LIMIT @take", Case.Sensitive);
        WorkflowRunCellFieldReader.FieldSql.ShouldNotContain("OFFSET", Case.Insensitive);
        WorkflowRunCellFieldReader.FieldSql.ShouldNotContain("SELECT record.payload_json", Case.Insensitive);
        WorkflowRunCellFieldReader.FieldSql.ShouldNotContain("convert_to", Case.Insensitive);
        WorkflowRunCellFieldReader.FieldSql.ShouldContain("@max_ref_id_chars", Case.Sensitive);
        WorkflowRunCellFieldReader.FieldSql.ShouldContain("@max_declared_size_chars", Case.Sensitive);
        WorkflowRunCellFieldReader.FieldSql.ShouldContain("@max_content_type_chars", Case.Sensitive);
        GetWorkflowRunCellFieldsQuery.DefaultPageSize.ShouldBe(50);
        GetWorkflowRunCellFieldsQuery.MaximumPageSize.ShouldBe(100);
    }
}
