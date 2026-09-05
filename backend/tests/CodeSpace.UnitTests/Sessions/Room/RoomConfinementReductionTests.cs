using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// The fold from a turn's MANY agent-run confinement records to the ONE posture its sentence may claim
/// (<c>RoomProjector.LeastConfined</c>). A turn's agents can land on different workers, so the reader's question —
/// "could any of these agents reach the network?" — is answered by the WEAKEST record, never by the strongest.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RoomConfinementReductionTests
{
    private static string Json(SandboxConfinementOutcome outcome, bool severed = false, string? reason = null) =>
        JsonSerializer.Serialize(new SandboxConfinement { Outcome = outcome, NetworkSevered = severed, Reason = reason }, AgentJson.Options);

    [Fact]
    public void No_records_reduce_to_no_posture_so_the_reader_keeps_the_hedge()
    {
        RoomProjector.LeastConfined(Array.Empty<string>()).ShouldBeNull(
            "a turn whose agents recorded nothing must fall back to the caveat, never to an invented enforced posture");
    }

    [Theory]
    // One unconfined agent decides the turn, whichever order the rows come back in — "some were confined" is not an
    // answer to "could anything here reach the network".
    [InlineData(true)]
    [InlineData(false)]
    public void One_unconfined_agent_decides_the_turn(bool unconfinedFirst)
    {
        var confined = Json(SandboxConfinementOutcome.Confined, severed: true);
        var unconfined = Json(SandboxConfinementOutcome.Unconfined, reason: SandboxConfinement.ReasonNoUserNamespaces);

        var rows = unconfinedFirst ? new[] { unconfined, confined } : new[] { confined, unconfined };

        var reduced = RoomProjector.LeastConfined(rows).ShouldNotBeNull();

        reduced.Outcome.ShouldBe(SandboxConfinementOutcome.Unconfined);
        reduced.Reason.ShouldBe(SandboxConfinement.ReasonNoUserNamespaces, "the surviving record must keep the actionable reason");
    }

    [Fact]
    public void A_confined_but_unsevered_agent_outranks_an_unconfined_one_and_loses_to_a_severed_one()
    {
        RoomProjector.LeastConfined(new[] { Json(SandboxConfinementOutcome.Confined, severed: true), Json(SandboxConfinementOutcome.Confined) })!
            .NetworkSevered.ShouldBeFalse("a turn is only as severed as its least-severed agent");

        RoomProjector.LeastConfined(new[] { Json(SandboxConfinementOutcome.Confined), Json(SandboxConfinementOutcome.NotApplicable) })!
            .Outcome.ShouldBe(SandboxConfinementOutcome.NotApplicable, "a runner that confines nothing is weaker than one that confines without severing");
    }

    [Fact]
    public void Every_agent_confined_and_severed_reduces_to_the_strong_posture()
    {
        var reduced = RoomProjector.LeastConfined(new[] { Json(SandboxConfinementOutcome.Confined, severed: true), Json(SandboxConfinementOutcome.Confined, severed: true) }).ShouldNotBeNull();

        reduced.Outcome.ShouldBe(SandboxConfinementOutcome.Confined);
        reduced.NetworkSevered.ShouldBeTrue();
    }

    [Fact]
    public void A_malformed_row_is_skipped_rather_than_failing_the_turn()
    {
        // A column this projector cannot read drops the room back to the hedge — one unreadable row must not take a
        // whole turn's rendering down, and an all-unreadable set must read as "no record", not as a posture.
        RoomProjector.LeastConfined(new[] { "{not json", Json(SandboxConfinementOutcome.Confined, severed: true) })!
            .Outcome.ShouldBe(SandboxConfinementOutcome.Confined);

        RoomProjector.LeastConfined(new[] { "{not json" }).ShouldBeNull();
    }
}
