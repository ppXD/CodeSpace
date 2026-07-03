using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSpace.Core.Services.Supervisor;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins <see cref="SupervisorTurnService.NarrowSpawnPayload"/> — the dependency-clamp's PURE narrowing of a
/// spawn payload to its ready subtasks. The load-bearing guarantee (a genericity-review regression): the clamp is a
/// NARROW edit of only <c>subtaskIds</c> + <c>agents</c>, so EVERY other root key the projector froze — the
/// decision-level <c>rationale</c> and any future annotation — survives verbatim. A rebuild from the typed spawn
/// payload silently dropped the rationale exactly on a partially-blocked fan-out (the case the room most needs explained).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorSpawnClampTests
{
    private static string Narrow(string payloadJson, params string[] ready) =>
        SupervisorTurnService.NarrowSpawnPayload(JsonNode.Parse(payloadJson)!.AsObject(), ready);

    [Fact]
    public void Narrowing_preserves_the_decision_level_rationale()
    {
        // THE regression: a spawn with a root rationale, clamped to a subset — the model's "why" must survive.
        const string payload = """{"subtaskIds":["s1","s2"],"rationale":{"why":"fan out the frontier now","evidence":"plan v2 accepted"}}""";

        var narrowed = Narrow(payload, "s1");

        SupervisorOutcome.ReadRationale(narrowed).ShouldBe(("fan out the frontier now", "plan v2 accepted"), "the decision-level rationale survives the dependency clamp");
        SubtaskIdsOf(narrowed).ShouldBe(new[] { "s1" }, "the spawn narrowed to the ready subtask");
    }

    [Fact]
    public void Narrowing_filters_the_per_agent_dispatch_to_the_ready_subtasks_and_keeps_the_rationale()
    {
        const string payload = """{"subtaskIds":["s1","s2"],"agents":[{"subtaskId":"s1","role":"backend"},{"subtaskId":"s2","role":"frontend"}],"rationale":{"why":"w","evidence":"e"}}""";

        var narrowed = Narrow(payload, "s1");

        var agents = JsonNode.Parse(narrowed)!["agents"]!.AsArray();
        agents.Count.ShouldBe(1, "only the ready subtask's agent is kept");
        agents[0]!["subtaskId"]!.GetValue<string>().ShouldBe("s1");
        agents[0]!["role"]!.GetValue<string>().ShouldBe("backend", "the kept agent's node survives verbatim");

        SupervisorOutcome.ReadRationale(narrowed).Why.ShouldBe("w", "and the rationale rides alongside the filtered agents");
    }

    [Fact]
    public void Narrowing_removes_the_agents_key_when_none_survive()
    {
        // Matches the [JsonIgnore(WhenWritingNull)] omission the typed rebuild produced for a null Agents.
        const string payload = """{"subtaskIds":["s1","s2"],"agents":[{"subtaskId":"s1","role":"backend"}]}""";

        var narrowed = Narrow(payload, "s2");

        JsonNode.Parse(narrowed)!.AsObject().ContainsKey("agents").ShouldBeFalse("no ready agent → the agents key is removed, not left empty");
        SubtaskIdsOf(narrowed).ShouldBe(new[] { "s2" });
    }

    [Fact]
    public void A_plain_spawn_narrows_to_just_the_ready_subtask_ids()
    {
        Narrow("""{"subtaskIds":["s1","s2","s3"]}""", "s1", "s3")
            .ShouldBe("""{"subtaskIds":["s1","s3"]}""", "a plain spawn (no agents, no rationale) narrows to a clean subtaskIds list");
    }

    [Fact]
    public void An_all_deferred_spawn_narrows_to_an_empty_fan_out_but_keeps_the_rationale()
    {
        // A cyclic / unsatisfiable DAG clamps to zero agents (a synchronous self-advance that trips the no-progress
        // bound) — but the rationale still explains WHY nothing spawned.
        const string payload = """{"subtaskIds":["s1","s2"],"agents":[{"subtaskId":"s1"}],"rationale":{"why":"both blocked on s0","evidence":"s0 not yet accepted"}}""";

        var narrowed = Narrow(payload /* ready: none */);

        SubtaskIdsOf(narrowed).ShouldBeEmpty("all deferred → an empty fan-out");
        JsonNode.Parse(narrowed)!.AsObject().ContainsKey("agents").ShouldBeFalse();
        SupervisorOutcome.ReadRationale(narrowed).Why.ShouldBe("both blocked on s0", "the rationale explains the empty fan-out");
    }

    [Fact]
    public void Narrowing_is_deterministic()
    {
        const string payload = """{"subtaskIds":["s1","s2"],"agents":[{"subtaskId":"s1","role":"a"}],"rationale":{"why":"w","evidence":"e"}}""";

        Narrow(payload, "s1").ShouldBe(Narrow(payload, "s1"), "same input → byte-identical narrowed payload (the replay-stable idempotency key)");
    }

    private static string[] SubtaskIdsOf(string json) =>
        JsonNode.Parse(json)!["subtaskIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
}
