using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins B1 of the L4 arc — the model-authored per-agent dispatch noun (<see cref="SupervisorAgentDispatch"/>)
/// carried optionally on the <c>spawn</c> payload. Load-bearing guarantee: a spawn WITHOUT per-agent specs serializes to
/// the EXACT pre-field bytes (the idempotency-key input), so the optional <c>agents[]</c> + every optional dispatch field
/// is <c>[JsonIgnore(WhenWritingNull)]</c>, the schema keeps <c>required:["subtaskIds"]</c>, and the projector is unchanged.
/// Pins that PLUS the end-to-end model-authoring path (a schema-shaped JSON binds into the decision).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorAgentDispatchTests
{
    // ── Byte-identity: agents[] is invisible when absent ──────────────────────────────

    [Fact]
    public void Spawn_without_agents_projects_byte_identical_to_before()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Spawn,
            Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "s1", "s2" } },
        });

        decision.PayloadJson.ShouldBe("""{"subtaskIds":["s1","s2"]}""",
            "a spawn with no per-agent specs must serialize to the pre-field bytes — the idempotency key depends on it");
    }

    [Fact]
    public void Spawn_with_agents_appends_only_the_agents_array()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Spawn,
            Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "s1" }, Agents = new[] { new SupervisorAgentDispatch { SubtaskId = "s1", Role = "backend implementer" } } },
        });

        decision.PayloadJson.ShouldBe("""{"subtaskIds":["s1"],"agents":[{"subtaskId":"s1","role":"backend implementer"}]}""",
            "agents is appended after subtaskIds; a dispatch's null optional fields are omitted");
    }

    // ── The dispatch noun: subtaskId always present, optionals omitted when null ───────

    [Fact]
    public void A_dispatch_with_only_a_subtask_omits_every_optional_field()
    {
        var json = JsonSerializer.Serialize(new SupervisorAgentDispatch { SubtaskId = "s1" }, AgentJson.Options);

        json.ShouldBe("""{"subtaskId":"s1"}""");
        foreach (var field in new[] { "role", "goalOverride", "repositoryId", "targetRepos", "harness", "model", "autonomyLevel", "agentDefinition" })
            json.ShouldNotContain(field, Case.Insensitive, $"a null {field} must be omitted, not emitted as null (byte-identity)");
    }

    [Fact]
    public void A_dispatch_round_trips_every_field_including_raw_target_repos()
    {
        var repo = Guid.NewGuid();
        var original = new SupervisorAgentDispatch
        {
            SubtaskId = "s1",
            Role = "security reviewer",
            GoalOverride = "audit the auth flow",
            RepositoryId = repo,
            TargetRepos = JsonDocument.Parse($$"""[{"repositoryId":"{{repo}}","alias":"api","access":"write"}]""").RootElement,
            Harness = "codex-cli",
            Model = "claude-opus-4-8",
            AutonomyLevel = "trusted",
            AgentDefinition = "security-reviewer",
        };

        var back = JsonSerializer.Deserialize<SupervisorAgentDispatch>(JsonSerializer.Serialize(original, AgentJson.Options), AgentJson.Options)!;

        back.SubtaskId.ShouldBe("s1");
        back.Role.ShouldBe("security reviewer");
        back.GoalOverride.ShouldBe("audit the auth flow");
        back.RepositoryId.ShouldBe(repo);
        back.Harness.ShouldBe("codex-cli");
        back.Model.ShouldBe("claude-opus-4-8");
        back.AutonomyLevel.ShouldBe("trusted");
        back.AgentDefinition.ShouldBe("security-reviewer", "the per-agent persona slug round-trips for the server to resolve + clamp");
        back.TargetRepos!.Value.EnumerateArray().Single().GetProperty("alias").GetString().ShouldBe("api", "the raw repo subset round-trips for the shared parser to clamp later");
    }

    [Fact]
    public void Projection_is_deterministic_with_agents_present()
    {
        var model = new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Spawn,
            Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "s1" }, Agents = new[] { new SupervisorAgentDispatch { SubtaskId = "s1", Role = "impl", Harness = "codex-cli" } } },
        };

        SupervisorDecisionProjector.Project(model).PayloadJson
            .ShouldBe(SupervisorDecisionProjector.Project(model).PayloadJson, "same model decision → byte-identical canonical payload, with or without agents");
    }

    // ── End-to-end model authoring: a schema-shaped JSON binds into the decision ───────

    [Fact]
    public void A_model_emitted_spawn_with_agents_binds_through_the_schema_options()
    {
        var repo = Guid.NewGuid();
        var modelJson = $$"""
            { "kind": "spawn", "spawn": { "subtaskIds": ["s1", "s2"], "agents": [
                { "subtaskId": "s1", "role": "backend implementer", "targetRepos": [{"repositoryId":"{{repo}}","access":"write"}], "harness": "codex-cli", "autonomyLevel": "trusted" },
                { "subtaskId": "s2", "role": "frontend adapter" }
            ] } }
            """;

        var model = JsonSerializer.Deserialize<SupervisorModelDecision>(modelJson, SupervisorDecisionSchema.Options)!;

        model.Spawn!.SubtaskIds.ShouldBe(new[] { "s1", "s2" });
        model.Spawn.Agents.ShouldNotBeNull();
        model.Spawn.Agents!.Count.ShouldBe(2);
        var a1 = model.Spawn.Agents[0];
        a1.SubtaskId.ShouldBe("s1");
        a1.Role.ShouldBe("backend implementer");
        a1.Harness.ShouldBe("codex-cli");
        a1.AutonomyLevel.ShouldBe("trusted");
        a1.TargetRepos!.Value.EnumerateArray().Single().GetProperty("repositoryId").GetString().ShouldBe(repo.ToString());
        model.Spawn.Agents[1].Role.ShouldBe("frontend adapter");
    }

    [Fact]
    public void A_model_emitted_spawn_without_agents_leaves_it_null()
    {
        var model = JsonSerializer.Deserialize<SupervisorModelDecision>("""{ "kind": "spawn", "spawn": { "subtaskIds": ["s1"] } }""", SupervisorDecisionSchema.Options)!;

        model.Spawn!.Agents.ShouldBeNull("a spawn the model authors without per-agent specs carries no dispatch overrides");
    }

    // ── Schema contract: agents[] is an optional, closed, bounded array ────────────────

    [Fact]
    public void The_spawn_block_still_requires_only_subtask_ids()
    {
        SpawnBlock().GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "subtaskIds" }, "agents must stay OPTIONAL — a spawn without it validates verbatim (back-compat)");
    }

    [Fact]
    public void The_agents_array_is_closed_bounded_and_requires_a_subtask_id()
    {
        var agents = SpawnBlock().GetProperty("properties").GetProperty("agents");

        agents.GetProperty("maxItems").GetInt32().ShouldBe(20, "per-agent fan-out shares the same hard cap as subtaskIds");
        var item = agents.GetProperty("items");
        item.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse("the dispatch item rejects invented fields");
        item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldContain("subtaskId");
    }

    [Fact]
    public void Autonomy_is_constrained_to_the_four_server_honored_tiers()
    {
        // The schema must bound autonomyLevel to the closed AgentAutonomyLevel set the executor parses — a free-form
        // string would let the model emit a value the server silently floors (and steers the model away from nonsense).
        var item = SpawnBlock().GetProperty("properties").GetProperty("agents").GetProperty("items");

        item.GetProperty("properties").GetProperty("autonomyLevel").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "confined", "standard", "trusted", "unleashed" }, "autonomy is a closed tier set the server can honor");
    }

    private static JsonElement SpawnBlock() => SupervisorDecisionSchema.ResponseSchema.GetProperty("properties").GetProperty("spawn");
}
