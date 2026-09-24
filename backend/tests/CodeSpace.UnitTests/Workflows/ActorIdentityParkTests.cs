using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: pins "park, don't die" for the act-as-user seam. A node that must act AS one person's own provider
/// identity used to KILL the run the moment that person had no linked one — a chat card click returned 204 and the
/// run died in the background a minute later, with nothing to do but start over. It now parks on a poll ladder and
/// the wake re-runs the node, which succeeds as soon as the identity exists.
///
/// <para>What these pin: the wait kind + first rung, the marker naming WHO must link WHAT (a parked run nobody can
/// act on is no better than a dead one), the ladder continuing from its OWN marker only, the park preserving the
/// payload it interrupted (the rerun side-effect gate's approval rides on it), and the honest failure once the
/// whole window is spent.</para>
/// </summary>
[Trait("Category", "Unit")]
public class ActorIdentityParkTests
{
    private static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Instance = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ActorIdentityRequiredException Fault() => new(ProviderKind.GitLab, Instance, Actor);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static JsonElement Marker(NodeResult parked) => JsonDocument.Parse(parked.SuspendUntil!.Payload.GetRawText()).RootElement;

    // ── The first park ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_missing_actor_identity_parks_the_node_instead_of_failing_the_run()
    {
        var now = DateTimeOffset.UtcNow;

        var result = ActorIdentityPark.Park(resumePayload: null, Fault(), now);

        result.Status.ShouldBe(NodeStatus.Suspended, "an unlinked identity is a fact a person can change in seconds — it must never terminalize the run");
        result.SuspendUntil.ShouldNotBeNull();
        result.SuspendUntil!.Kind.ShouldBe(WorkflowWaitKinds.ActorIdentityLink);
        result.SuspendUntil.DeadlineAt.ShouldNotBeNull("the deadline IS the wake — nothing else resolves this wait");
        (result.SuspendUntil.DeadlineAt!.Value - now).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(36), "park 1 rides the 30s rung (+20% jitter)");
        (result.SuspendUntil.DeadlineAt.Value - now).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(24), "park 1 rides the 30s rung (−20% jitter)");
    }

    [Fact]
    public void The_park_names_who_must_link_what_so_the_parked_run_is_actionable()
    {
        var marker = Marker(ActorIdentityPark.Park(null, Fault(), DateTimeOffset.UtcNow));

        marker.GetProperty(ActorIdentityPark.MarkerField).GetBoolean().ShouldBeTrue();
        marker.GetProperty("actorUserId").GetGuid().ShouldBe(Actor, "the actor is NOT the person watching the run — without the id nobody knows who to ask");
        marker.GetProperty("provider").GetString().ShouldBe("GitLab");
        marker.GetProperty("providerInstanceId").GetGuid().ShouldBe(Instance);
        marker.GetProperty("prompt").GetString().ShouldContain("GitLab", Case.Sensitive, "the parked reason must name the provider to connect, not just say 'waiting'");
    }

    [Fact]
    public void The_parked_reason_rides_the_one_key_the_run_detail_can_actually_read()
    {
        // The bounded pending-wait read seam (WorkflowRunPendingWaitObservationReader) extracts exactly ONE payload
        // key in SQL — `prompt` — and the run detail's SuspendedPanel renders only that. A reason written under any
        // other name is invisible to the person who has to act on it, which is the whole point of parking.
        var marker = Marker(ActorIdentityPark.Park(null, Fault(), DateTimeOffset.UtcNow));

        marker.TryGetProperty("prompt", out var prompt).ShouldBeTrue();
        prompt.ValueKind.ShouldBe(JsonValueKind.String, "the seam classifies a non-string prompt as Invalid and shows nothing");
        prompt.GetString()!.Length.ShouldBeLessThanOrEqualTo(2048, "longer than the seam's cap and the run detail reports it Truncated");
    }

    [Fact]
    public void The_suspend_payload_and_the_timeout_payload_are_the_same_marker()
    {
        var token = ActorIdentityPark.Park(null, Fault(), DateTimeOffset.UtcNow).SuspendUntil!;

        token.TimeoutPayload.ShouldNotBeNull();
        token.TimeoutPayload!.Value.GetRawText().ShouldBe(token.Payload.GetRawText(),
            customMessage: "the reconciler re-fires a lost deadline from the wait's STORED payload — if the two ever diverged the backstop would wake the node with a different ladder position than the engine scheduled");
    }

    [Fact]
    public void The_park_keeps_the_nodes_branch_identity()
    {
        // IterationKey unset ⇒ the engine keys the wait on the node's ambient cell, so a parked node inside a
        // map branch stays in ITS branch instead of colliding with a sibling's row.
        ActorIdentityPark.Park(null, Fault(), DateTimeOffset.UtcNow).SuspendUntil!.IterationKey.ShouldBeNull();
    }

    // ── The ladder across wakes ──────────────────────────────────────────────────────

    [Fact]
    public void A_wake_that_still_finds_no_identity_advances_the_ladder_and_keeps_the_anchor()
    {
        var now = DateTimeOffset.UtcNow;

        var first = ActorIdentityPark.Park(null, Fault(), now);
        var second = ActorIdentityPark.Park(first.SuspendUntil!.TimeoutPayload, Fault(), now.AddSeconds(30));

        var marker = Marker(second);
        marker.GetProperty("parks").GetInt32().ShouldBe(2);
        marker.GetProperty("firstParkedAtUtc").GetDateTimeOffset().ShouldBe(Marker(first).GetProperty("firstParkedAtUtc").GetDateTimeOffset(),
            customMessage: "the window is measured from the FIRST park — a moving anchor would let a run wait forever");

        (second.SuspendUntil!.DeadlineAt!.Value - now.AddSeconds(30)).ShouldBeGreaterThan(TimeSpan.FromSeconds(36),
            "park 2 must ride the SECOND rung — a ladder that never advanced would poll every 30s for 24h");
    }

    [Fact]
    public void A_ladder_only_ever_continues_from_its_own_marker()
    {
        // An agent-run result, a human answer, the OTHER park's marker: every foreign resume shape starts a fresh
        // ladder rather than inheriting a park count that was never this wait's.
        var foreign = Json("""{"infraPark":true,"parks":4,"firstParkedAtUtc":"2020-01-01T00:00:00+00:00"}""");

        Marker(ActorIdentityPark.Park(foreign, Fault(), DateTimeOffset.UtcNow)).GetProperty("parks").GetInt32().ShouldBe(1);
    }

    // ── The park is transparent ──────────────────────────────────────────────────────

    [Fact]
    public void The_park_preserves_the_resume_payload_it_interrupted()
    {
        // The regression this guards: on a from-node RERUN the side-effect gate reads `approved: true` off the
        // resume payload before it lets a side-effecting node run. A park that REPLACED that payload would make the
        // deadline wake look un-approved, and the gate would silently SKIP the node — losing the very write the
        // operator had just approved.
        var approval = Json("""{"approved":true,"by":"ops@example.com"}""");

        var marker = Marker(ActorIdentityPark.Park(approval, Fault(), DateTimeOffset.UtcNow));

        marker.GetProperty("approved").GetBoolean().ShouldBeTrue("the park must not discard the state the node was resumed with");
        marker.GetProperty("by").GetString().ShouldBe("ops@example.com");
        marker.GetProperty(ActorIdentityPark.MarkerField).GetBoolean().ShouldBeTrue("…while still being recognisable as this park's own wake");
    }

    // ── The honest ending ────────────────────────────────────────────────────────────

    [Fact]
    public void An_exhausted_window_fails_honestly_rather_than_parking_forever()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = Json($$"""{"{{ActorIdentityPark.MarkerField}}":true,"parks":6,"firstParkedAtUtc":"{{now.ToString("o")}}"}""");

        var result = ActorIdentityPark.Park(stale, Fault(), now + ActorIdentityPark.MaxParkWindow);

        result.Status.ShouldBe(NodeStatus.Failure, "a run can never park forever — the window ends it");
        result.Retryable.ShouldBeFalse("re-running immediately cannot conjure the identity; only a person can");
        result.Error.ShouldContain("GitLab", Case.Sensitive, "the ending must still name what was never connected");
        result.Error.ShouldContain("24h", Case.Sensitive, "and how long it waited");
    }

    // ── Rule 8: the constants are the contract ───────────────────────────────────────

    [Fact]
    public void The_ladder_and_the_window_are_pinned()
    {
        // Tightening the head or widening the window changes how long a real person has to connect their account
        // before their teammate's run gives up — a product decision, never an invisible refactor.
        ActorIdentityPark.Schedule.ShouldBe(new[]
        {
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60),
        });
        ActorIdentityPark.MaxParkWindow.ShouldBe(TimeSpan.FromHours(24));
        ActorIdentityPark.MarkerField.ShouldBe("actorIdentityLink", "the marker rides in durable wait rows — renaming it would orphan every parked run");
    }
}
