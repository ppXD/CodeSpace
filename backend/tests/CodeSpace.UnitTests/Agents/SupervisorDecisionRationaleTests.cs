using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins the DECISION-LEVEL rationale contract — a single root-level <c>rationale</c> (why + evidence) the
/// model authors for EVERY verb, projected uniformly onto each canonical payload's root and read back generically
/// via <see cref="SupervisorOutcome.ReadRationale"/>. The two load-bearing guarantees:
/// <list type="number">
/// <item>A decision that authored NO rationale serializes BYTE-IDENTICALLY to a plain payload serialize, so the
/// idempotency-key bytes are unchanged and every pre-rationale decision replays exactly as before.</item>
/// <item>A retry with rationale freezes the SAME root shape it did before the hoist, so historical retry rows
/// (and their idempotency keys) are unaffected — no migration, no special-casing.</item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDecisionRationaleTests
{
    private static readonly string[] EveryVerb =
    {
        SupervisorDecisionKinds.Plan,
        SupervisorDecisionKinds.Spawn,
        SupervisorDecisionKinds.Retry,
        SupervisorDecisionKinds.AskHuman,
        SupervisorDecisionKinds.Merge,
        SupervisorDecisionKinds.Resolve,
        SupervisorDecisionKinds.Stop,
    };

    // ── genericity: the rationale is authored + read uniformly for every verb ─────────

    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan)]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    [InlineData(SupervisorDecisionKinds.Retry)]
    [InlineData(SupervisorDecisionKinds.AskHuman)]
    [InlineData(SupervisorDecisionKinds.Merge)]
    [InlineData(SupervisorDecisionKinds.Resolve)]
    [InlineData(SupervisorDecisionKinds.Stop)]
    public void The_rationale_is_readable_at_the_payload_root_for_every_verb(string kind)
    {
        var rationale = new SupervisorRationale { Why = "the prior attempt only skimmed one engine", Evidence = "attempt 1 exited rate-limited" };

        var decision = SupervisorDecisionProjector.Project(ModelFor(kind, rationale));

        var (why, evidence) = SupervisorOutcome.ReadRationale(decision.PayloadJson);

        why.ShouldBe("the prior attempt only skimmed one engine", $"the {kind} decision must surface its rationale at the payload root");
        evidence.ShouldBe("attempt 1 exited rate-limited");
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan)]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    [InlineData(SupervisorDecisionKinds.Retry)]
    [InlineData(SupervisorDecisionKinds.AskHuman)]
    [InlineData(SupervisorDecisionKinds.Merge)]
    [InlineData(SupervisorDecisionKinds.Resolve)]
    [InlineData(SupervisorDecisionKinds.Stop)]
    public void A_rationale_less_decision_is_byte_identical_to_the_plain_payload_serialize_for_every_verb(string kind)
    {
        // The byte-identity guarantee for EVERY verb (not just spawn) — a strict byte compare, so a refactor that
        // dropped the null-rationale fast path (reformatting / reordering / re-escaping) fails here, not silently.
        var decision = SupervisorDecisionProjector.Project(ModelFor(kind, rationale: null));

        decision.PayloadJson.ShouldBe(ExpectedPlainPayloadJson(kind), $"a rationale-less {kind} must serialize BYTE-IDENTICALLY to its plain payload (no idempotency-key drift)");
        SupervisorOutcome.ReadRationale(decision.PayloadJson).ShouldBe((null, null));
    }

    // ── the PRODUCTION binding path: raw model JSON → SupervisorModelDecision.Rationale → projector → read ──

    [Theory]
    [InlineData("""{"kind":"retry","rationale":{"why":"w","evidence":"e"},"retry":{"subtaskId":"s1"}}""")]
    [InlineData("""{"kind":"plan","rationale":{"why":"w","evidence":"e"},"plan":{"goal":"g","subtasks":[{"id":"s1","title":"t","instruction":"do"}]}}""")]
    [InlineData("""{"kind":"spawn","rationale":{"why":"w","evidence":"e"},"spawn":{"subtaskIds":["s1"]}}""")]
    public void The_model_authored_root_rationale_binds_and_survives_to_the_frozen_payload(string modelJson)
    {
        // The ONLY link production actually uses (LlmSupervisorDecider.TryDeserialize) — raw lower-camel model JSON
        // deserialized via SupervisorDecisionSchema.Options, then projected. A future [JsonPropertyName] typo / rename /
        // Options change that broke the binding would make Rationale null and the projector's fast path silently drop
        // EVERY rationale in production while the C#-constructed tests stayed green. This pins the wire → frozen path.
        var model = JsonSerializer.Deserialize<SupervisorModelDecision>(modelJson, SupervisorDecisionSchema.Options)!;

        model.Rationale.ShouldNotBeNull("the model's root rationale must bind to SupervisorModelDecision.Rationale");
        model.Rationale!.Why.ShouldBe("w");

        SupervisorOutcome.ReadRationale(SupervisorDecisionProjector.Project(model).PayloadJson).ShouldBe(("w", "e"), "and survive projection to the frozen payload");
    }

    [Fact]
    public void The_inject_path_escapes_unicode_and_special_chars_identically_to_a_direct_serialize()
    {
        // Guarantee 2 beyond pure-ASCII: the JsonNode Parse → ToJsonString inject path must escape exactly as a single
        // direct JsonSerializer.Serialize would (the default Web encoder escapes <, >, &, +, ', and all non-ASCII as
        // \uXXXX). Compare the projected bytes to a direct serialize of the equivalent object graph.
        const string why = "重现失败 <token> & \"refresh\"";
        const string evidence = "attempt 1 → 401 (race on 'RefreshToken')";

        var projected = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Retry,
            Rationale = new SupervisorRationale { Why = why, Evidence = evidence },
            Retry = new SupervisorRetryPayload { SubtaskId = "s1" },
        }).PayloadJson;

        var expected = JsonSerializer.Serialize(new { subtaskId = "s1", rationale = new { why, evidence } }, AgentJson.Options);

        projected.ShouldBe(expected, "the inject/re-encode path must byte-match a direct serialize, unicode + JSON-special chars included");
    }

    // ── guarantee 1: rationale-less payload keeps the pre-feature idempotency key ──────

    [Fact]
    public void The_idempotency_key_of_a_rationale_less_decision_is_unchanged()
    {
        var payload = new SupervisorSpawnPayload { SubtaskIds = new[] { "s1", "s2" } };

        var projected = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Spawn, Spawn = payload }).PayloadJson;

        var keyFromProjection = SupervisorDecisionLog.DeriveIdempotencyKey(SupervisorDecisionKinds.Spawn, projected, "turn1");
        var keyFromPlainPayload = SupervisorDecisionLog.DeriveIdempotencyKey(SupervisorDecisionKinds.Spawn, JsonSerializer.Serialize(payload, AgentJson.Options), "turn1");

        keyFromProjection.ShouldBe(keyFromPlainPayload, "the rationale feature must not shift the idempotency key of a rationale-less decision (exactly-once replay unaffected)");
    }

    // ── guarantee 2: retry byte-compat with the pre-hoist frozen shape ────────────────

    [Fact]
    public void A_retry_with_rationale_freezes_the_historical_root_shape()
    {
        // Pre-hoist, retry.rationale nested in the schema still serialized the SupervisorRetryPayload directly to
        // `{"subtaskId":"s1","rationale":{"why":"w","evidence":"e"}}` (rationale at the payload ROOT). The hoist must
        // freeze BYTE-IDENTICAL bytes, so historical retry rows + their idempotency keys are untouched.
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Retry,
            Rationale = new SupervisorRationale { Why = "w", Evidence = "e" },
            Retry = new SupervisorRetryPayload { SubtaskId = "s1" },
        });

        decision.PayloadJson.ShouldBe("""{"subtaskId":"s1","rationale":{"why":"w","evidence":"e"}}""");
    }

    [Fact]
    public void A_retry_with_a_revised_instruction_and_rationale_freezes_in_declaration_order()
    {
        // The 3-key retry shape {subtaskId, revisedInstruction, rationale} — pin the property ORDER (payload fields in
        // declaration order, then the injected rationale last) matches a single direct serialize.
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Retry,
            Rationale = new SupervisorRationale { Why = "w", Evidence = "e" },
            Retry = new SupervisorRetryPayload { SubtaskId = "s1", RevisedInstruction = "use the injected clock" },
        });

        decision.PayloadJson.ShouldBe("""{"subtaskId":"s1","revisedInstruction":"use the injected clock","rationale":{"why":"w","evidence":"e"}}""");
    }

    // ── empty / partial rationale ─────────────────────────────────────────────────────

    [Fact]
    public void An_empty_rationale_is_not_injected()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Spawn,
            Rationale = new SupervisorRationale(),   // both fields null
            Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "s1" } },
        });

        decision.PayloadJson.ShouldNotContain("rationale", Case.Sensitive, "an all-null rationale is noise — omit it (byte-identical to no rationale)");
    }

    [Theory]
    [InlineData("why only", null)]
    [InlineData(null, "evidence only")]
    public void A_partial_rationale_injects_only_the_authored_field(string? why, string? evidence)
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Stop,
            Rationale = new SupervisorRationale { Why = why, Evidence = evidence },
            Stop = new SupervisorStopPayload { Outcome = "done", Summary = "s" },
        });

        SupervisorOutcome.ReadRationale(decision.PayloadJson).ShouldBe((why, evidence));
    }

    // ── determinism + fail-closed ─────────────────────────────────────────────────────

    [Fact]
    public void Injecting_the_rationale_is_deterministic()
    {
        var model = new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Plan,
            Rationale = new SupervisorRationale { Why = "decompose first", Evidence = "goal spans two subsystems" },
            Plan = new SupervisorPlanPayload { Goal = "g", Subtasks = new[] { new SupervisorPlannedSubtask { Id = "s1", Title = "t", Instruction = "do" } } },
        };

        SupervisorDecisionProjector.Project(model).PayloadJson.ShouldBe(SupervisorDecisionProjector.Project(model).PayloadJson, "same model decision → byte-identical canonical payload, rationale included");
    }

    [Fact]
    public void An_unknown_kind_still_carries_the_rationale_on_its_fail_closed_stop()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = "wat",
            Rationale = new SupervisorRationale { Why = "the model went off-vocabulary", Evidence = "kind='wat'" },
        });

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop, "an unrecognized verb fails closed to a terminal stop");
        SupervisorOutcome.ReadRationale(decision.PayloadJson).Why.ShouldBe("the model went off-vocabulary", "even a fail-closed stop keeps the model's why");
    }

    // ── injection must not corrupt the verb's own payload (nested arrays) or non-ASCII text ──

    [Fact]
    public void Injecting_the_rationale_preserves_the_verbs_own_nested_payload()
    {
        // The projector injects rationale by round-tripping the payload through JsonNode — prove that round-trip
        // leaves the plan's OWN nested structure (goal + subtasks array) intact, not just the appended rationale.
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Plan,
            Rationale = new SupervisorRationale { Why = "decompose", Evidence = "two subsystems" },
            Plan = new SupervisorPlanPayload
            {
                Goal = "ship the feature",
                Subtasks = new[]
                {
                    new SupervisorPlannedSubtask { Id = "s1", Title = "backend", Instruction = "wire the endpoint", DependsOn = new[] { "s0" } },
                    new SupervisorPlannedSubtask { Id = "s2", Title = "frontend", Instruction = "add the form" },
                },
            },
        });

        var subtasks = SupervisorOutcome.ReadPlanSubtasks(decision.PayloadJson);
        subtasks.Count.ShouldBe(2, "the plan's nested subtasks survived the rationale injection round-trip");
        subtasks[0].Id.ShouldBe("s1");
        subtasks[0].DependsOn.ShouldBe(new[] { "s0" }, "the nested DependsOn array survived intact");
        subtasks[1].Title.ShouldBe("frontend");

        SupervisorOutcome.ReadRationale(decision.PayloadJson).Why.ShouldBe("decompose", "and the injected rationale reads alongside the preserved payload");
    }

    [Fact]
    public void A_rationale_with_non_ascii_and_special_chars_round_trips()
    {
        // The JsonNode inject path re-encodes the whole payload — pin that non-ASCII (中文) + JSON-special characters
        // survive the encoder unchanged (a broken encoder would corrupt the trace's "why").
        const string why = "重现失败：token 过期后 refresh 未触发 \"single-flight\"";
        const string evidence = "attempt 1 → 401 → 无重试 (race on <RefreshToken>)";

        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Retry,
            Rationale = new SupervisorRationale { Why = why, Evidence = evidence },
            Retry = new SupervisorRetryPayload { SubtaskId = "s1" },
        });

        SupervisorOutcome.ReadRationale(decision.PayloadJson).ShouldBe((why, evidence), "non-ASCII + special chars survive the inject/re-encode round-trip");
    }

    [Fact]
    public void Every_verb_round_trips_its_rationale_and_stays_readable()
    {
        // Belt-and-suspenders over the Theory: one pass asserting the whole vocabulary is covered (a new verb added to
        // the switch without a rationale path would leave a gap this catches).
        foreach (var kind in EveryVerb)
        {
            var decision = SupervisorDecisionProjector.Project(ModelFor(kind, new SupervisorRationale { Why = $"why-{kind}", Evidence = $"ev-{kind}" }));
            SupervisorOutcome.ReadRationale(decision.PayloadJson).ShouldBe(($"why-{kind}", $"ev-{kind}"), $"verb '{kind}' must round-trip its rationale");
        }
    }

    private static SupervisorModelDecision ModelFor(string kind, SupervisorRationale? rationale) => kind switch
    {
        SupervisorDecisionKinds.Plan => new() { Kind = kind, Rationale = rationale, Plan = PlanPayload },
        SupervisorDecisionKinds.Spawn => new() { Kind = kind, Rationale = rationale, Spawn = SpawnPayload },
        SupervisorDecisionKinds.Retry => new() { Kind = kind, Rationale = rationale, Retry = RetryPayload },
        SupervisorDecisionKinds.AskHuman => new() { Kind = kind, Rationale = rationale, AskHuman = AskHumanPayload },
        SupervisorDecisionKinds.Merge => new() { Kind = kind, Rationale = rationale, Merge = MergePayload },
        SupervisorDecisionKinds.Resolve => new() { Kind = kind, Rationale = rationale, Resolve = ResolvePayload },
        SupervisorDecisionKinds.Stop => new() { Kind = kind, Rationale = rationale, Stop = StopPayload },
        _ => new() { Kind = kind, Rationale = rationale },
    };

    /// <summary>The plain (pre-rationale) canonical bytes for a verb — a direct serialize of the SAME payload instance <see cref="ModelFor"/> uses, so the byte-identity assertion can't drift from the model factory.</summary>
    private static string ExpectedPlainPayloadJson(string kind) => JsonSerializer.Serialize<object>(kind switch
    {
        SupervisorDecisionKinds.Plan => PlanPayload,
        SupervisorDecisionKinds.Spawn => SpawnPayload,
        SupervisorDecisionKinds.Retry => RetryPayload,
        SupervisorDecisionKinds.AskHuman => AskHumanPayload,
        SupervisorDecisionKinds.Merge => MergePayload,
        SupervisorDecisionKinds.Resolve => ResolvePayload,
        SupervisorDecisionKinds.Stop => StopPayload,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    }, AgentJson.Options);

    private static SupervisorPlanPayload PlanPayload => new() { Goal = "g", Subtasks = new[] { new SupervisorPlannedSubtask { Id = "s1", Title = "t", Instruction = "do" } } };
    private static SupervisorSpawnPayload SpawnPayload => new() { SubtaskIds = new[] { "s1" } };
    private static SupervisorRetryPayload RetryPayload => new() { SubtaskId = "s1" };
    private static SupervisorAskHumanPayload AskHumanPayload => new() { Question = "?" };
    private static SupervisorMergePayload MergePayload => new();
    private static SupervisorResolvePayload ResolvePayload => new();
    private static SupervisorStopPayload StopPayload => new() { Outcome = "done", Summary = "s" };
}
