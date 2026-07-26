using System.Text.Json;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: pins A2's generic model-plane park — the ladder the supervisor has ridden since P1.1, now available to
/// any node that calls a model. The planner and synthesizer had NO retry policy and NO park, so the same provider
/// blip a Deep run sleeps through killed a Standard run in minutes.
///
/// <para>What these pin: the fault classes worth parking for (and the ones that must stay fail-fast), the ladder
/// continuing across wakes from its OWN marker only, the honest failure once the window is spent, and the
/// iteration-key choice that keeps a parked map-branch node inside its own branch cell.</para>
/// </summary>
[Trait("Category", "Unit")]
public class InfraParkTests
{
    private static NodeRunContext Context(JsonElement? resumePayload = null) => new()
    {
        Inputs = new Dictionary<string, JsonElement>(),
        Config = new Dictionary<string, JsonElement>(),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>(), Sys = new Dictionary<string, JsonElement>() },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
        NodeId = "synth",
        ResumePayload = resumePayload,
    };

    private static LlmApiException Fault(LlmErrorCategory category) => new("Anthropic", 503, category, "upstream unavailable");

    // ── Which faults park, and which must never ──────────────────────────────────────

    [Theory]
    [InlineData(LlmErrorCategory.Transient, true)]
    [InlineData(LlmErrorCategory.RateLimited, true)]
    [InlineData(LlmErrorCategory.AuthFailed, false)]
    public void Only_a_genuinely_transient_class_is_worth_parking_for(LlmErrorCategory category, bool parkable)
    {
        // An auth failure is operator-actionable NOW; parking one would hide it behind a 24h ladder.
        InfraPark.IsParkable(Fault(category)).ShouldBe(parkable);
    }

    // ── The first park ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_first_fault_parks_on_the_shared_wait_kind_with_a_deadline_wake()
    {
        var result = InfraPark.Park(Context(), Fault(LlmErrorCategory.Transient), DateTimeOffset.UtcNow);

        result.Status.ShouldBe(NodeStatus.Suspended, "a provider outage must not terminalize the run");
        result.SuspendUntil.ShouldNotBeNull();
        result.SuspendUntil!.Kind.ShouldBe(WorkflowWaitKinds.SupervisorInfraPark, "the SAME wait kind the supervisor uses — which is how the stranded-wait reconciler backstops this park with no new code");
        result.SuspendUntil.DeadlineAt.ShouldNotBeNull("the deadline IS the wake; nothing else resolves this wait");
        result.SuspendUntil.TimeoutPayload.ShouldNotBeNull("the wake must carry the ladder position forward");
    }

    [Fact]
    public void The_park_keeps_the_nodes_ambient_cell_so_a_map_branch_stays_in_its_branch()
    {
        // The supervisor overrides IterationKey because its node is top-level. A generic node must NOT: the engine
        // falls back to the ambient cell key, which for a node inside a fan-out is its own branch.
        InfraPark.Park(Context(), Fault(LlmErrorCategory.Transient), DateTimeOffset.UtcNow)
            .SuspendUntil!.IterationKey.ShouldBeNullOrEmpty();
    }

    // ── The ladder across wakes ──────────────────────────────────────────────────────

    [Fact]
    public void A_wake_that_faults_again_advances_the_ladder_from_its_own_marker()
    {
        var now = DateTimeOffset.UtcNow;
        var first = InfraPark.Park(Context(), Fault(LlmErrorCategory.Transient), now);

        var second = InfraPark.Park(Context(first.SuspendUntil!.TimeoutPayload), Fault(LlmErrorCategory.Transient), now.AddMinutes(1));

        var parks = second.SuspendUntil!.TimeoutPayload!.Value.GetProperty("parks").GetInt32();
        parks.ShouldBe(2, "the ladder position rides the marker, so the second park waits longer than the first");
        second.SuspendUntil.TimeoutPayload.Value.GetProperty("firstParkedAtUtc").GetString()
            .ShouldBe(first.SuspendUntil!.TimeoutPayload!.Value.GetProperty("firstParkedAtUtc").GetString(), "the 24h window is anchored at the FIRST park, so a run can never park forever");
    }

    [Fact]
    public void A_non_park_resume_payload_starts_the_ladder_fresh()
    {
        // Load-bearing: a node resuming from a human answer / an agent barrier / a self-advance must not inherit
        // some other mechanism's park count. The marker read is self-identifying for exactly this reason.
        var foreign = JsonSerializer.SerializeToElement(new { answeredBy = "someone", parks = 3 });

        InfraPark.Park(Context(foreign), Fault(LlmErrorCategory.Transient), DateTimeOffset.UtcNow)
            .SuspendUntil!.TimeoutPayload!.Value.GetProperty("parks").GetInt32().ShouldBe(1);
    }

    // ── The honest ending ────────────────────────────────────────────────────────────

    [Fact]
    public void Past_the_whole_window_the_node_fails_honestly_instead_of_parking_forever()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = InfraPark.Park(Context(), Fault(LlmErrorCategory.Transient), now).SuspendUntil!.TimeoutPayload;

        var result = InfraPark.Park(Context(stale), Fault(LlmErrorCategory.Transient), now + SupervisorInfraPark.MaxParkWindow);

        result.Status.ShouldBe(NodeStatus.Failure, "a 24h outage is a real failure — parking past the window would hide an outage nobody is coming to fix");
        result.Error.ShouldContain("model plane", Case.Insensitive);
        result.Retryable.ShouldBeFalse("re-running the node cannot reach a provider that has been down for a day");
    }
}
