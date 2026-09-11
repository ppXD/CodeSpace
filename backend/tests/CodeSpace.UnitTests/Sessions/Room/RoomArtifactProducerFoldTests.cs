using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// Launch Extremis P21-8b — the per-artifact PRODUCER fold. An artifact's card could say what was delivered and
/// whether a check graded it, but never who made it, in what execution state, under what confinement, or at what
/// cost. These pin <see cref="RoomProjector.PostureOf"/> / <see cref="RoomProjector.ProducerOf"/> /
/// <see cref="RoomProjector.ProducersOf"/> — and, above all, that every unrecorded fact reports as an explicit
/// absence rather than as a value that reads better than its evidence.
///
/// <para>Tier: Unit — every fold under test is pure over in-memory agent-run rows.</para>
/// </summary>
[Trait("Category", "Unit")]
public class RoomArtifactProducerFoldTests
{
    /// <summary>Mirrors CodeSpace.Api's <c>AddJsonOptions</c> — web defaults plus the string enum converter.</summary>
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly Guid Agent = Guid.NewGuid();

    [Theory]
    [InlineData(SandboxConfinementOutcome.Confined, true, RoomConfinementPosture.ConfinedNetworkSevered)]
    [InlineData(SandboxConfinementOutcome.Confined, false, RoomConfinementPosture.Confined)]
    [InlineData(SandboxConfinementOutcome.Unconfined, false, RoomConfinementPosture.Unconfined)]
    [InlineData(SandboxConfinementOutcome.NotApplicable, false, RoomConfinementPosture.Unconfined)]
    public void One_producers_posture_names_the_same_three_strengths_the_run_level_fold_ranks(SandboxConfinementOutcome outcome, bool severed, RoomConfinementPosture expected)
    {
        RoomProjector.PostureOf(new SandboxConfinement { Outcome = outcome, NetworkSevered = severed }).ShouldBe(expected);
    }

    [Fact]
    public void A_producer_that_recorded_no_posture_reads_Unknown_never_Confined()
    {
        // The mutation this exists to catch: defaulting an absent record to Confined paints an operator a safety
        // nobody evidenced, on exactly the runs (older, or unstamped) that most need the hedge.
        RoomProjector.PostureOf(null).ShouldBe(RoomConfinementPosture.Unknown);

        RoomProjector.ProducerOf(Row(confinementJson: null), logs: null, costUsd: null)
            .Confinement.ShouldBe(RoomConfinementPosture.Unknown, "an absent column must never resolve to a confinement the row does not carry");
    }

    [Fact]
    public void An_unparseable_confinement_column_degrades_to_Unknown_rather_than_failing_the_turn()
    {
        RoomProjector.ProducerOf(Row(confinementJson: "{not json"), logs: null, costUsd: null)
            .Confinement.ShouldBe(RoomConfinementPosture.Unknown);
    }

    [Fact]
    public void Unknown_is_the_zero_value_so_a_default_constructed_producer_claims_nothing()
    {
        // The enum's ordinal contract: every producer that skips the field (an older projection read back off the
        // wire) lands on the weakest claim, not on the first alphabetical one.
        ((int)RoomConfinementPosture.Unknown).ShouldBe(0);
        new RoomArtifactProducer { AgentRunId = Agent, Status = "Running" }.Confinement.ShouldBe(RoomConfinementPosture.Unknown);
    }

    [Theory]
    [InlineData(AgentRunStatus.Queued)]
    [InlineData(AgentRunStatus.Running)]
    [InlineData(AgentRunStatus.Succeeded)]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.Cancelled)]
    public void The_producers_status_is_its_own_rows_status_word(AgentRunStatus status)
    {
        RoomProjector.ProducerOf(Row(status: status), logs: null, costUsd: null).Status.ShouldBe(status.ToString(),
            customMessage: "the artifact and the agent card above it must speak one status for one agent");
    }

    [Fact]
    public void An_unpriced_producers_cost_stays_null_never_zero()
    {
        // 0 reads as a free agent; null reads as an unpriceable one. A Codex model absent from the price table is
        // the second, and the difference is the whole point of the field.
        RoomProjector.ProducerOf(Row(), logs: null, costUsd: null).CostUsd.ShouldBeNull();
        RoomProjector.ProducerOf(Row(), logs: null, costUsd: 0.42m).CostUsd.ShouldBe(0.42m);
    }

    [Theory]
    [InlineData(RoomAgentLogStatus.Verified)]
    [InlineData(RoomAgentLogStatus.Captured)]
    [InlineData(RoomAgentLogStatus.Finalizing)]
    [InlineData(RoomAgentLogStatus.Incomplete)]
    [InlineData(RoomAgentLogStatus.Stalled)]
    public void The_producer_carries_the_log_status_itself_not_a_collapsed_boolean(RoomAgentLogStatus status)
    {
        // LogsComplete answers "did it settle"; a reader of a still-running producer needs the difference between
        // finalizing, stalled behind a storage outage, and genuinely lost.
        RoomProjector.ProducerOf(Row(), new RoomAgentLogSummary(status, 1, "detail"), costUsd: null).Logs.ShouldBe(status);
    }

    [Fact]
    public void A_producer_with_no_declared_stream_leaves_its_log_status_unsaid()
    {
        RoomProjector.ProducerOf(Row(), logs: null, costUsd: null).Logs.ShouldBeNull();
    }

    [Fact]
    public void Every_agent_of_the_turn_is_indexed_and_an_absent_row_is_the_only_missing_producer()
    {
        var other = Guid.NewGuid();
        var logs = new Dictionary<Guid, RoomAgentLogSummary> { [Agent] = new(RoomAgentLogStatus.Verified, 1, "detail") };
        var costs = new Dictionary<Guid, decimal?> { [Agent] = 1.5m, [other] = null };

        var producers = RoomProjector.ProducersOf([Row(), Row(agentRunId: other, status: AgentRunStatus.Running)], logs, costs);

        producers[Agent].CostUsd.ShouldBe(1.5m);
        producers[Agent].Logs.ShouldBe(RoomAgentLogStatus.Verified);
        producers[other].CostUsd.ShouldBeNull("an agent the phases never priced reports cost unknown, not free");
        producers[other].Logs.ShouldBeNull();
        producers.ContainsKey(Guid.NewGuid()).ShouldBeFalse("a gone agent-run row is the ONE reason an artifact reports no producer");
    }

    [Fact]
    public void A_producer_serializes_its_posture_as_a_word_the_frontend_switches_on()
    {
        var wire = JsonSerializer.Serialize(RoomProjector.ProducerOf(Row(confinementJson: null), logs: null, costUsd: null), ApiJson);

        wire.ShouldContain("\"confinement\":\"Unknown\"", Case.Sensitive, "the frontend reads posture words, never ordinals");
        wire.ShouldContain("\"status\":\"Succeeded\"", Case.Sensitive);
        wire.ShouldContain("\"costUsd\":null", Case.Sensitive, "an unpriceable producer must reach the wire as null, never as an omitted field a renderer reads as 0");
    }

    [Fact]
    public void A_producers_log_status_reaches_the_wire_as_its_word_too()
    {
        var producer = RoomProjector.ProducerOf(Row(), new RoomAgentLogSummary(RoomAgentLogStatus.Stalled, 1, "detail"), costUsd: null);

        JsonSerializer.Serialize(producer, ApiJson).ShouldContain("\"logs\":\"Stalled\"", Case.Sensitive);
    }

    private static RoomProjector.AgentProducerRow Row(Guid? agentRunId = null, AgentRunStatus status = AgentRunStatus.Succeeded, string? confinementJson = null) =>
        new(agentRunId ?? Agent, status, confinementJson);
}
