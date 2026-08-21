using System.ComponentModel.DataAnnotations;
using CodeSpace.Core.Handlers.QueryHandlers.Workflows;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.ToolCalls;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows.ToolCalls;
using CodeSpace.Messages.Queries.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.ToolCalls;

[Trait("Category", "Unit")]
public sealed class WorkflowRunToolCallReaderContractTests
{
    [Fact]
    public void Cursor_round_trips_the_exact_utc_instant_and_stable_id_and_rejects_every_open_shape()
    {
        var expected = new WorkflowRunToolCallPageCursor(
            new DateTimeOffset(638908128000000000, TimeSpan.Zero),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        WorkflowRunToolCallPageCursor.Decode(expected.Encode()).ShouldBe(expected);
        WorkflowRunToolCallPageCursor.Decode(null).ShouldBeNull();
        WorkflowRunToolCallPageCursor.TryDecode("not-a-cursor", out _).ShouldBeFalse();
        WorkflowRunToolCallPageCursor.TryDecode("", out _).ShouldBeFalse();
        WorkflowRunToolCallPageCursor.TryDecode(" ", out _).ShouldBeFalse();
        WorkflowRunToolCallPageCursor.TryDecode(new string('a', WorkflowRunToolCallPageCursor.MaximumEncodedLength + 1), out _).ShouldBeFalse();
        WorkflowRunToolCallPageCursor.TryDecode(new WorkflowRunToolCallPageCursor(DateTimeOffset.UnixEpoch, Guid.Empty).Encode(), out _).ShouldBeFalse();
    }

    [Fact]
    public void Page_query_has_closed_cursor_and_hard_limit_validation()
    {
        Validate(new ListWorkflowRunToolCallsQuery { RunId = Guid.NewGuid() }).ShouldBeEmpty();
        Validate(new ListWorkflowRunToolCallsQuery { RunId = Guid.NewGuid(), Limit = 1 }).ShouldBeEmpty();
        Validate(new ListWorkflowRunToolCallsQuery { RunId = Guid.NewGuid(), Limit = ListWorkflowRunToolCallsQuery.MaximumPageSize }).ShouldBeEmpty();
        Validate(new ListWorkflowRunToolCallsQuery { RunId = Guid.NewGuid(), Limit = 0 }).ShouldHaveSingleItem();
        Validate(new ListWorkflowRunToolCallsQuery { RunId = Guid.NewGuid(), Limit = ListWorkflowRunToolCallsQuery.MaximumPageSize + 1 }).ShouldHaveSingleItem();
        Validate(new ListWorkflowRunToolCallsQuery { RunId = Guid.NewGuid(), Cursor = "forged" }).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Queries_require_team_membership_and_handlers_source_exact_scope_from_current_team()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var callId = Guid.NewGuid();
        var reader = new RecordingReader();
        var team = new StubCurrentTeam(teamId);
        var list = new ListWorkflowRunToolCallsQuery { RunId = runId, Cursor = null, Limit = 17 };
        var detail = new GetWorkflowRunToolCallQuery { RunId = runId, ToolCallId = callId };
        list.ShouldBeAssignableTo<IRequireTeamMembership>();
        detail.ShouldBeAssignableTo<IRequireTeamMembership>();

        await new ListWorkflowRunToolCallsQueryHandler(reader, team).Handle(list, CancellationToken.None);
        await new GetWorkflowRunToolCallQueryHandler(reader, team).Handle(detail, CancellationToken.None);

        reader.PageRequest.ShouldBe(new WorkflowRunToolCallPageRequest(teamId, runId, null, 17));
        reader.DetailRequest.ShouldBe(new WorkflowRunToolCallDetailRequest(teamId, runId, callId));
        typeof(IWorkflowRunToolCallReader).GetInterfaces().Select(value => value.Name).ShouldContain("IScopedDependency");
    }

    [Fact]
    public void Future_or_blank_database_strings_degrade_to_typed_evidence_states_instead_of_throwing()
    {
        WorkflowRunToolCallWire.DecodeEffect("SideEffecting").ShouldBe(WorkflowRunToolCallEffectClass.SideEffecting);
        WorkflowRunToolCallWire.DecodeEffect("future-effect").ShouldBe(WorkflowRunToolCallEffectClass.Corrupt);
        WorkflowRunToolCallWire.DecodeEffect(null).ShouldBe(WorkflowRunToolCallEffectClass.LegacyUnknown);
        WorkflowRunToolCallWire.DecodeState("Completed").ShouldBe(WorkflowRunToolCallObservationState.Completed);
        WorkflowRunToolCallWire.DecodeState("future-state").ShouldBe(WorkflowRunToolCallObservationState.Corrupt);
        WorkflowRunToolCallWire.DecodeState("").ShouldBe(WorkflowRunToolCallObservationState.LegacyUnknown);
        WorkflowRunToolCallWire.DecodeAttemptStatus("Indeterminate").ShouldBe(WorkflowRunToolCallAttemptObservationStatus.Indeterminate);
        WorkflowRunToolCallWire.DecodeAttemptStatus("future-status").ShouldBe(WorkflowRunToolCallAttemptObservationStatus.Corrupt);
        WorkflowRunToolCallWire.DecodeAttemptStatus(null).ShouldBe(WorkflowRunToolCallAttemptObservationStatus.LegacyUnknown);
        WorkflowRunToolCallWire.DecodeCapture("RedactedExact").ShouldBe(WorkflowRunCaptureCompleteness.RedactedExact);
        WorkflowRunToolCallWire.DecodeCapture("future-capture").ShouldBe(WorkflowRunCaptureCompleteness.Corrupt);
        WorkflowRunToolCallWire.DecodeCapture(" ").ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown);
        WorkflowRunToolCallWire.DecodeErrorCode("approval-expired").ShouldBe(WorkflowRunToolCallObservationErrorCode.ApprovalExpired);
        WorkflowRunToolCallWire.DecodeErrorCode("future-error").ShouldBe(WorkflowRunToolCallObservationErrorCode.Corrupt);
        WorkflowRunToolCallWire.DecodeErrorCode(" ").ShouldBe(WorkflowRunToolCallObservationErrorCode.LegacyUnknown);
        WorkflowRunToolCallWire.DecodeErrorCode(null).ShouldBeNull();
    }

    [Fact]
    public void Reader_sql_is_scope_exact_keyset_bounded_and_metadata_only()
    {
        var page = WorkflowRunToolCallReader.PageSql;
        page.ShouldContain("team_id = @team_id");
        page.ShouldContain("call.workflow_run_id = @run_id");
        page.ShouldContain("call.created_at < @cursor_created_at");
        page.ShouldContain("call.created_at DESC, call.id DESC");
        page.ShouldContain("LIMIT @take");
        page.ShouldNotContain("OFFSET");
        page.ShouldNotContain("COUNT(");
        page.ShouldNotContain("workflow_run_tool_call_attempt");

        var attempts = WorkflowRunToolCallReader.AttemptsSql;
        attempts.ShouldContain("team_id = @team_id");
        attempts.ShouldContain("workflow_run_id = @run_id");
        attempts.ShouldContain("tool_call_id = @call_id");
        attempts.ShouldContain("ORDER BY attempt_ordinal");
        attempts.ShouldContain("LIMIT @take");
        attempts.ShouldNotContain("OFFSET");

        var combined = WorkflowRunToolCallReader.PageSql + WorkflowRunToolCallReader.DetailSql + WorkflowRunToolCallReader.AttemptsSql;
        foreach (var forbidden in new[]
        {
            "error_message", "arguments_artifact_id", "arguments_digest", "redaction_policy", "tool_namespace",
            "endpoint_fingerprint", "invocation_id", "result_artifact_id", "result_digest", "error_artifact_id", "error_digest",
            "retry_of_attempt_id", "retry_reason",
        }) combined.ShouldNotContain(forbidden);
    }

    [Fact]
    public void Public_wire_contract_cannot_grow_sensitive_or_authority_fields_accidentally()
    {
        var properties = typeof(WorkflowRunToolCallMetadata).GetProperties().Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        properties.ShouldBe(new HashSet<string>(new[]
        {
            "ToolCallId", "RunId", "ToolAdapterKind", "ToolName", "EffectClass", "State", "CallOrdinal", "SourceKind",
            "SourceCorrelationId", "CaptureSource", "CaptureCompleteness", "CreatedAt", "LastModifiedAt", "TerminalAt", "ErrorCode",
        }, StringComparer.Ordinal));

        typeof(WorkflowRunToolCallAttemptMetadata).GetProperties().Select(value => value.Name).ToHashSet(StringComparer.Ordinal)
            .ShouldBe(new HashSet<string>(new[]
            {
                "AttemptOrdinal", "Status", "CaptureSource", "CaptureCompleteness", "StartedAt", "CompletedAt", "CreatedAt",
                "LastModifiedAt", "ErrorCode",
            }, StringComparer.Ordinal));
    }

    private static IReadOnlyList<ValidationResult> Validate(object value)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(value, new ValidationContext(value), errors, validateAllProperties: true);
        return errors;
    }

    private sealed class RecordingReader : IWorkflowRunToolCallReader
    {
        public WorkflowRunToolCallPageRequest? PageRequest { get; private set; }
        public WorkflowRunToolCallDetailRequest? DetailRequest { get; private set; }

        public Task<WorkflowRunToolCallPage?> ReadPageAsync(WorkflowRunToolCallPageRequest request, CancellationToken cancellationToken)
        {
            PageRequest = request;
            return Task.FromResult<WorkflowRunToolCallPage?>(null);
        }

        public Task<WorkflowRunToolCallDetail?> ReadDetailAsync(WorkflowRunToolCallDetailRequest request, CancellationToken cancellationToken)
        {
            DetailRequest = request;
            return Task.FromResult<WorkflowRunToolCallDetail?>(null);
        }
    }

    private sealed record StubCurrentTeam(Guid TeamId) : ICurrentTeam
    {
        public Guid? Id => TeamId;
        public bool IsSet => true;
    }
}
