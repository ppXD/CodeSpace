using System.Buffers.Text;
using System.Text;
using CodeSpace.Core.Services.Supervisor.Observation;
using CodeSpace.Core.Services.Supervisor.Observation.Exceptions;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

[Trait("Category", "Unit")]
public sealed class SupervisorDecisionObservationMetadataContractTests
{
    [Theory]
    [InlineData("Pending", SupervisorDecisionObservationStatus.Pending)]
    [InlineData("AwaitingApproval", SupervisorDecisionObservationStatus.AwaitingApproval)]
    [InlineData("Running", SupervisorDecisionObservationStatus.Running)]
    [InlineData("Succeeded", SupervisorDecisionObservationStatus.Succeeded)]
    [InlineData("Failed", SupervisorDecisionObservationStatus.Failed)]
    [InlineData("Expired", SupervisorDecisionObservationStatus.Expired)]
    [InlineData(null, SupervisorDecisionObservationStatus.LegacyUnknown)]
    [InlineData("", SupervisorDecisionObservationStatus.LegacyUnknown)]
    [InlineData(" ", SupervisorDecisionObservationStatus.Corrupt)]
    [InlineData("FutureTerminal", SupervisorDecisionObservationStatus.Corrupt)]
    public void Persisted_status_decoder_is_closed_without_hiding_unknown_kinds(string? raw, SupervisorDecisionObservationStatus expected)
    {
        SupervisorDecisionObservationWire.DecodeStatus(raw).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, 0, SupervisorDecisionObservationErrorState.None)]
    [InlineData("", 0, SupervisorDecisionObservationErrorState.Complete)]
    [InlineData("error", 5, SupervisorDecisionObservationErrorState.Complete)]
    [InlineData("界", 3, SupervisorDecisionObservationErrorState.Complete)]
    [InlineData("prefix", 100, SupervisorDecisionObservationErrorState.Truncated)]
    [InlineData(null, 1, SupervisorDecisionObservationErrorState.Corrupt)]
    [InlineData("too-long", 1, SupervisorDecisionObservationErrorState.Corrupt)]
    [InlineData("error", -1, SupervisorDecisionObservationErrorState.Corrupt)]
    public void Error_metadata_decoder_is_closed_and_never_turns_inconsistent_storage_into_complete_text(string? prefix, int totalBytes, SupervisorDecisionObservationErrorState expected)
    {
        SupervisorDecisionObservationWire.DecodeError(prefix, totalBytes).ShouldBe(expected);
    }

    [Fact]
    public void Story_and_change_cursors_are_versioned_scope_bound_and_accept_long_max_without_overflow()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var story = new SupervisorDecisionObservationStoryCursor(teamId, runId, long.MaxValue, long.MaxValue);
        var change = new SupervisorDecisionObservationChangeCursor(teamId, runId, long.MaxValue);

        SupervisorDecisionObservationStoryCursor.TryDecode(story.Encode(), teamId, runId, out var decodedStory).ShouldBeTrue();
        decodedStory.ShouldBe(story);
        SupervisorDecisionObservationChangeCursor.TryDecode(change.Encode(), teamId, runId, out var decodedChange).ShouldBeTrue();
        decodedChange.ShouldBe(change);

        SupervisorDecisionObservationStoryCursor.TryDecode(story.Encode(), Guid.NewGuid(), runId, out _).ShouldBeFalse();
        SupervisorDecisionObservationStoryCursor.TryDecode(story.Encode(), teamId, Guid.NewGuid(), out _).ShouldBeFalse();
        SupervisorDecisionObservationChangeCursor.TryDecode(story.Encode(), teamId, runId, out _).ShouldBeFalse("cursor axes must not alias");

        var overflowStory = Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"story/v1\n{teamId:N}\n{runId:N}\n9223372036854775808\n0"));
        var overflowChange = Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"change/v1\n{teamId:N}\n{runId:N}\n9223372036854775808"));
        SupervisorDecisionObservationStoryCursor.TryDecode(overflowStory, teamId, runId, out _).ShouldBeFalse();
        SupervisorDecisionObservationChangeCursor.TryDecode(overflowChange, teamId, runId, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(SupervisorDecisionObservationStoryPageMode.Tail, null, 128, true)]
    [InlineData(SupervisorDecisionObservationStoryPageMode.Older, "cursor", 1, true)]
    [InlineData(SupervisorDecisionObservationStoryPageMode.Newer, "cursor", 500, true)]
    [InlineData(SupervisorDecisionObservationStoryPageMode.Tail, "cursor", 128, false)]
    [InlineData(SupervisorDecisionObservationStoryPageMode.Older, null, 128, false)]
    [InlineData(SupervisorDecisionObservationStoryPageMode.Newer, null, 128, false)]
    [InlineData(SupervisorDecisionObservationStoryPageMode.Tail, null, 0, false)]
    [InlineData(SupervisorDecisionObservationStoryPageMode.Tail, null, 501, false)]
    public void Story_request_shape_is_mutually_exclusive_and_hard_bounded(SupervisorDecisionObservationStoryPageMode mode, string? cursor, int limit, bool valid)
    {
        var request = new SupervisorDecisionObservationStoryPageRequest(Guid.NewGuid(), Guid.NewGuid(), mode, cursor, limit);

        var action = request.ValidateShape;

        if (valid) action.ShouldNotThrow();
        else action.ShouldThrow<SupervisorDecisionObservationReadRequestException>();
    }

    [Fact]
    public void Story_and_change_requests_default_to_128_and_change_limits_are_hard_bounded()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        new SupervisorDecisionObservationStoryPageRequest(teamId, runId).Limit.ShouldBe(128);
        new SupervisorDecisionObservationChangePageRequest(teamId, runId).Limit.ShouldBe(128);
        Should.NotThrow(() => new SupervisorDecisionObservationChangePageRequest(teamId, runId, Limit: 1).ValidateShape());
        Should.NotThrow(() => new SupervisorDecisionObservationChangePageRequest(teamId, runId, Limit: 500).ValidateShape());
        Should.Throw<SupervisorDecisionObservationReadRequestException>(() => new SupervisorDecisionObservationChangePageRequest(teamId, runId, Limit: 0).ValidateShape());
        Should.Throw<SupervisorDecisionObservationReadRequestException>(() => new SupervisorDecisionObservationChangePageRequest(teamId, runId, Limit: 501).ValidateShape());
    }

    [Fact]
    public void Reader_sql_is_limit_keyset_only_and_structurally_cannot_materialize_large_bodies()
    {
        var sql = string.Join('\n',
            SupervisorDecisionObservationMetadataReader.TailSql,
            SupervisorDecisionObservationMetadataReader.OlderSql,
            SupervisorDecisionObservationMetadataReader.NewerSql,
            SupervisorDecisionObservationMetadataReader.ChangesSql);

        sql.ShouldContain("LIMIT @take");
        sql.ShouldNotContain("OFFSET", Case.Insensitive);
        sql.ShouldNotContain("COUNT(", Case.Insensitive);
        sql.ShouldNotContain("payload_jsonb", Case.Insensitive);
        sql.ShouldNotContain("outcome_jsonb", Case.Insensitive);
        typeof(SupervisorDecisionObservationMetadata).GetProperties().Select(property => property.Name)
            .ShouldNotContain(name => name.Contains("Payload", StringComparison.OrdinalIgnoreCase) || name.Contains("Outcome", StringComparison.OrdinalIgnoreCase));
    }
}
