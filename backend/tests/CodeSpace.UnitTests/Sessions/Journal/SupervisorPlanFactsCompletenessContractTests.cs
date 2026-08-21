using System.Text.Json;
using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Core.Services.Supervisor.Observation;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

[Trait("Category", "Unit")]
public sealed class SupervisorPlanFactsCompletenessContractTests
{
    [Fact]
    public void Shared_page_bundle_is_hard_capped_at_one_maximum_page()
    {
        SupervisorPlanObservationPageBundle.PageLimit.ShouldBe(SupervisorDecisionObservationPageLimits.MaximumLimit);
        SupervisorPlanObservationPageBundle.PageLimit.ShouldBe(500);
        JournalObservationCoverageLimits.MaximumEntriesPerStep.ShouldBe(3, "page + subtask + modelUsage is the maximum honest combination on one boundary step");
    }

    [Fact]
    public void Coverage_reason_is_closed_and_unknown_values_are_not_complete()
    {
        Enum.GetValues<JournalObservationCoverageReason>().ShouldBe(new[]
        {
            JournalObservationCoverageReason.OlderItemsOmitted,
            JournalObservationCoverageReason.InvalidLeaf,
            JournalObservationCoverageReason.TruncatedLeaf,
            JournalObservationCoverageReason.CorruptLeaf,
            JournalObservationCoverageReason.CorruptDecisionStatus,
        });
    }

    [Fact]
    public void Healthy_journal_step_wire_does_not_gain_a_null_coverage_field()
    {
        var step = new JournalStep
        {
            Id = "supervisor-1", At = DateTimeOffset.UnixEpoch, Kind = "decision", Title = "Supervisor planned the work",
            Beat = true, Milestone = true,
        };

        var wire = JsonSerializer.Serialize(step, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        wire.ShouldNotContain("observationCoverage", customMessage: "the healthy journal wire remains byte-compatible");
    }

    [Fact]
    public void Coverage_identity_keeps_story_order_as_an_exact_decimal_string()
    {
        var coverage = new JournalObservationCoverage
        {
            SourceKind = JournalObservationCoverageSourceKinds.SupervisorPlanPage,
            Reason = JournalObservationCoverageReason.OlderItemsOmitted,
            ObservedCount = 500,
            OmittedCount = 1,
            OmittedCountIsLowerBound = true,
            DecisionId = Guid.NewGuid(),
            StoryOrder = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        coverage.ValidateShape().ShouldBeEmpty();
        coverage.StoryOrder.ShouldBe("9223372036854775807", "the browser never rounds a 64-bit story identity");
    }

    [Fact]
    public void Incomplete_step_wire_carries_the_closed_reason_counts_and_exact_identity()
    {
        var decisionId = Guid.NewGuid();
        var step = new JournalStep
        {
            Id = $"supervisor-{decisionId:N}", At = DateTimeOffset.UnixEpoch, Kind = "decision", Title = "Supervisor planned the work",
            Beat = true, Milestone = true,
            ObservationCoverage = [new JournalObservationCoverage
            {
                SourceKind = JournalObservationCoverageSourceKinds.SupervisorPlanPage,
                Reason = JournalObservationCoverageReason.OlderItemsOmitted,
                ObservedCount = 500,
                OmittedCount = 1,
                OmittedCountIsLowerBound = true,
                DecisionId = decisionId,
                StoryOrder = "501",
            }],
        };

        var wire = JsonSerializer.Serialize(step, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        wire.ShouldContain("\"observationCoverage\"");
        wire.ShouldContain("\"reason\":\"OlderItemsOmitted\"");
        wire.ShouldContain("\"observedCount\":500");
        wire.ShouldContain("\"storyOrder\":\"501\"");
    }

    [Fact]
    public void Coverage_merge_rejects_a_fourth_entry_instead_of_silently_truncating_truth()
    {
        var decisionId = Guid.NewGuid();
        JournalObservationCoverage Gap(int storyOrder) => new()
        {
            SourceKind = JournalObservationCoverageSourceKinds.SupervisorPlanSubtasks,
            Reason = JournalObservationCoverageReason.InvalidLeaf,
            ObservedCount = 0,
            OmittedCount = 1,
            OmittedCountIsLowerBound = false,
            DecisionId = decisionId,
            StoryOrder = storyOrder.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var three = new JournalStepFacts { ObservationCoverage = [Gap(1), Gap(2), Gap(3)] };

        Should.Throw<InvalidOperationException>(() => three.Merge(new JournalStepFacts { ObservationCoverage = [Gap(4)] }));
    }

    [Fact]
    public async Task Two_sources_share_one_exact_bounded_page_per_request_scope()
    {
        var reader = new RecordingLeafReader();
        await using var bundle = new SupervisorPlanObservationPageBundle(reader, new HttpContextAccessor());
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await new PlanFactsSource(bundle).GatherAsync(runId, teamId, CancellationToken.None);
        await new SupervisorPlanModelCallFactsSource(bundle).GatherAsync(runId, teamId, CancellationToken.None);
        await bundle.GetForRunAsync(Guid.NewGuid(), teamId, CancellationToken.None);

        reader.Requests.Count.ShouldBe(2, "the same team/run shares one success while a distinct identity reads independently");
        reader.Requests.ShouldAllBe(request => request.Mode == SupervisorDecisionObservationStoryPageMode.Tail
            && request.Cursor == null && request.Limit == SupervisorPlanObservationPageBundle.PageLimit);
    }

    [Fact]
    public async Task Contradictory_reader_identity_fails_closed_before_any_fact_source_can_consume_it()
    {
        var reader = new RecordingLeafReader { ReturnedRunId = Guid.NewGuid() };
        await using var bundle = new SupervisorPlanObservationPageBundle(reader, new HttpContextAccessor());

        var error = await Should.ThrowAsync<InvalidOperationException>(() => bundle.GetForRunAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));

        error.Message.ShouldContain("contradictory");
    }

    private sealed class RecordingLeafReader : ISupervisorPlanObservationLeafReader
    {
        public List<SupervisorPlanObservationPageRequest> Requests { get; } = [];
        public Guid? ReturnedRunId { get; init; }

        public Task<SupervisorPlanObservationPage?> ReadPageAsync(SupervisorPlanObservationPageRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult<SupervisorPlanObservationPage?>(new SupervisorPlanObservationPage
            {
                SupervisorRunId = ReturnedRunId ?? request.SupervisorRunId,
                Mode = request.Mode.ToString(),
                Limit = request.Limit,
                SnapshotRevision = 0,
                HeadRevision = 0,
                Items = [],
                HasMore = false,
                NextNewerCursor = "test-only",
            });
        }
    }
}
