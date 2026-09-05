using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: <see cref="SupervisorDecisionCoherence"/> — the pre-projection check for the ONE invariant the
/// decision schema cannot express (a per-kind conditional <c>required</c>): the chosen kind's payload sub-object
/// must be present. Pins each defective shape's named defect, each coherent/exempt shape's null, and — the
/// drift detector — that the set of kinds this class demands a payload for is derived from
/// <see cref="SupervisorDecisionSchema.ResponseSchema"/>'s own per-verb <c>required</c> declarations, so the
/// schema and the coherence check cannot disagree silently.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDecisionCoherenceTests
{
    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan, "plan")]
    [InlineData(SupervisorDecisionKinds.Spawn, "spawn")]
    [InlineData(SupervisorDecisionKinds.Retry, "retry")]
    [InlineData(SupervisorDecisionKinds.AskHuman, "askHuman")]
    [InlineData(SupervisorDecisionKinds.Stop, "stop")]
    [InlineData(SupervisorDecisionKinds.AmendAcceptance, "amendAcceptance")]
    public void A_kind_whose_payload_sub_object_is_missing_is_named_incoherent(string kind, string property)
    {
        var defect = SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = kind });

        defect.ShouldNotBeNull($"kind '{kind}' without its payload is unexecutable and must be repairable before projection substitutes an empty payload");
        defect.ShouldContain($"'{property}'", customMessage: "the defect names the exact sub-object the payload must ride in, so the repair prompt tells the model where to nest it");
        defect.ShouldContain("anywhere else", Case.Insensitive, "the defect warns against the live-observed flattening (fields at the top level are never read)");
    }

    [Theory]
    [InlineData("", "r", true, "subtaskId")]       // blank target
    [InlineData("s1", "", true, "reason")]         // blank evidence
    [InlineData("s1", "r", false, "neither")]      // no waive and no replacement — unexecutable proposal
    public void An_amendment_missing_its_target_evidence_or_proposal_is_named_incoherent(string subtaskId, string reason, bool waive, string expectedFragment)
    {
        var model = new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.AmendAcceptance,
            AmendAcceptance = new SupervisorAmendAcceptancePayload { SubtaskId = subtaskId, Reason = reason, Waive = waive },
        };

        SupervisorDecisionCoherence.MissingPayload(model).ShouldNotBeNull().ShouldContain(expectedFragment);
    }

    [Fact]
    public void A_coherent_waive_and_a_coherent_replacement_amendment_pass()
    {
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.AmendAcceptance,
            AmendAcceptance = new SupervisorAmendAcceptancePayload { SubtaskId = "s1", Reason = "r", Waive = true },
        }).ShouldBeNull();

        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.AmendAcceptance,
            AmendAcceptance = new SupervisorAmendAcceptancePayload { SubtaskId = "s1", Reason = "r", Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } } },
        }).ShouldBeNull();
    }

    [Fact]
    public void A_spawn_with_an_EMPTY_subtaskIds_array_is_named_incoherent()
    {
        // The one shape that slips every earlier net: the sub-object is present (no bind error), minItems is not
        // validated client-side, and the projector passes it through — the executor then rejects it a full turn
        // later. At decide time the dependency clamp has not run yet, so an empty spawn is always the model's own
        // authorship, never a server-emptied fan-out.
        var model = new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Spawn, Spawn = new SupervisorSpawnPayload() };

        SupervisorDecisionCoherence.MissingPayload(model).ShouldNotBeNull().ShouldContain("EMPTY");
    }

    [Fact]
    public void A_plan_with_an_EMPTY_subtasks_array_is_named_incoherent()
    {
        // The empty-spawn arm's sibling, and the shape SupervisorPlanValidator cannot see: it validates DependsOn
        // EDGES, and a plan with no subtasks has none. Left unnamed, a subtask-less plan projects cleanly and the
        // run spins on empty spawns until the no-progress bound instead of the model being asked once for a plan.
        var model = new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = new SupervisorPlanPayload { Goal = "ship" } };

        SupervisorDecisionCoherence.MissingPayload(model).ShouldNotBeNull().ShouldContain("EMPTY");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_retry_with_a_blank_subtaskId_is_named_incoherent(string blank)
    {
        var model = new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Retry, Retry = new SupervisorRetryPayload { SubtaskId = blank } };

        SupervisorDecisionCoherence.MissingPayload(model).ShouldNotBeNull().ShouldContain("BLANK");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_ask_human_with_a_blank_question_is_named_incoherent(string blank)
    {
        var model = new SupervisorModelDecision { Kind = SupervisorDecisionKinds.AskHuman, AskHuman = new SupervisorAskHumanPayload { Question = blank } };

        SupervisorDecisionCoherence.MissingPayload(model).ShouldNotBeNull().ShouldContain("BLANK");
    }

    [Fact]
    public void A_decision_carrying_the_payload_its_kind_names_is_coherent()
    {
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Plan, Plan = new SupervisorPlanPayload { Subtasks = new[] { new SupervisorPlannedSubtask { Id = "st-1", Title = "A", Instruction = "do a" } } } }).ShouldBeNull();
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Spawn, Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "st-1" } } }).ShouldBeNull();
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Retry, Retry = new SupervisorRetryPayload { SubtaskId = "st-1" } }).ShouldBeNull();
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.AskHuman, AskHuman = new SupervisorAskHumanPayload { Question = "which db?" } }).ShouldBeNull();
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Stop, Stop = new SupervisorStopPayload { Outcome = "completed", Summary = "done" } }).ShouldBeNull();
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Merge)]     // schema: required [] — an empty merge legitimately means "merge everything mergeable"
    [InlineData(SupervisorDecisionKinds.Resolve)]   // schema: no payload sub-object at all
    [InlineData("")]                                // blank kind — the bind-check flow owns it, never this
    [InlineData("wat")]                             // unknown kind — the projector fail-closes it to a stop, never this
    public void An_exempt_kind_is_never_repaired(string kind)
    {
        SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = kind }).ShouldBeNull();
    }

    [Fact]
    public void The_demanded_kind_set_is_the_schema_own_required_declarations()
    {
        // THE drift detector: derive, from the schema itself, which kinds declare a payload sub-object with at
        // least one required field — exactly those (and only those) must be demanded by the coherence check. A
        // schema edit (a new verb, a verb's payload going fully-optional) that is not mirrored here fails THIS
        // test, never silently diverges (the conformance-matrix lesson: assert per cell, not in aggregate).
        var properties = SupervisorDecisionSchema.ResponseSchema.GetProperty("properties");

        foreach (var kindElement in properties.GetProperty("kind").GetProperty("enum").EnumerateArray())
        {
            var kind = kindElement.GetString()!;

            // The kind→payload-property convention has ONE authority (the lift derives it off the schema itself); a
            // switch copied here would be a second one, and the drift this test exists to catch could hide in it.
            var property = SupervisorDecisionPayloadLift.PayloadPropertyFor(kind) ?? kind;

            var schemaRequired = properties.TryGetProperty(property, out var sub) && sub.TryGetProperty("required", out var r) ? r : default;
            var schemaDemandsPayload = schemaRequired.ValueKind == JsonValueKind.Array && schemaRequired.GetArrayLength() > 0;
            var defect = SupervisorDecisionCoherence.MissingPayload(new SupervisorModelDecision { Kind = kind });

            (defect is not null).ShouldBe(schemaDemandsPayload, $"kind '{kind}': the schema {(schemaDemandsPayload ? "declares required payload fields" : "declares no required payload field")}, so the coherence check must {(schemaDemandsPayload ? "demand" : "not demand")} the '{property}' sub-object");

            if (!schemaDemandsPayload) continue;

            // Per CELL, not in aggregate: the defect the repair prompt quotes must name EVERY field the schema
            // declares required for that kind — a field added to a `required` array and not to the defect's sentence
            // sends the model a correction that is silently incomplete.
            foreach (var field in schemaRequired.EnumerateArray())
                defect!.ShouldContain($"'{field.GetString()}'", customMessage: $"kind '{kind}': the schema requires '{field.GetString()}' inside '{property}', so the named defect must tell the model to carry it");
        }
    }

    // ── The retry's TARGET: a retry may not re-run a unit that is already done while other units are still failed ──

    [Fact]
    public void A_retry_aimed_at_a_finished_unit_while_another_has_failed_is_named_incoherent()
    {
        // LIVE shape (golden 'five-subtask-middle-failed', main runs 33945398336 + 33946934743): four units
        // succeeded, s3 failed, and the brain retried s1. Nothing below can see it — the payload is whole and the
        // id is plan-declared — so the turn is spent re-running finished work and s3 is left untouched.
        var defect = SupervisorDecisionCoherence.MisdirectedRetry(Retry("s1"), FanOut(("s1", Succeeded), ("s2", Succeeded), ("s3", Failed)));

        defect.ShouldNotBeNull();
        defect.ShouldContain("'s1'", customMessage: "the defect names the target the model chose");
        defect.ShouldContain("s3", customMessage: "…and quotes the unit ids a retry is actually owed, so the correction can hand them over");
        defect.ShouldNotContain("s2", customMessage: "a succeeded unit is not a failure the retry is owed");
    }

    [Fact]
    public void A_retry_of_the_failed_unit_is_never_re_asked()
    {
        SupervisorDecisionCoherence.MisdirectedRetry(Retry("s3"), FanOut(("s1", Succeeded), ("s2", Succeeded), ("s3", Failed))).ShouldBeNull();
    }

    [Fact]
    public void A_retry_of_an_ACCEPTANCE_FAILED_unit_is_never_re_asked()
    {
        // The unit's agent reported success but its own objective check REJECTED the work — the branch is not
        // mergeable and this is the textbook legitimate retry. Re-asking here would spend a round-trip telling a
        // model that got it right to think again.
        var priors = FanOut(("s1", Succeeded), ("s2", Rejected), ("s3", Failed));

        SupervisorDecisionCoherence.MisdirectedRetry(Retry("s2"), priors).ShouldBeNull("a unit whose acceptance check failed is unfinished work, not finished work");
    }

    [Fact]
    public void A_retry_of_a_WAIVED_unit_is_never_re_asked()
    {
        // WAIVED ≠ PASSED: the unit is withheld from the head and never counted as objectively verified, so a
        // retry that goes back for a real verdict is legitimate.
        SupervisorDecisionCoherence.MisdirectedRetry(Retry("s1"), FanOut(("s1", Waived), ("s3", Failed))).ShouldBeNull();
    }

    [Fact]
    public void A_retry_of_a_unit_whose_check_an_approved_amendment_superseded_is_never_re_asked()
    {
        // The recitation itself tells the model to RETRY this unit — its recorded verdict is stale under the new
        // check. Re-asking would contradict the run's own instruction in the same prompt.
        var card = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = "s1", Reason = "the check invokes tooling this repository does not have",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "verify.sh" } },
        });

        var priors = FanOut(("s1", Succeeded), ("s3", Failed)).ToList();
        priors.Add(new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 9, DecisionKind = SupervisorDecisionKinds.AskHuman, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = card.PayloadJson, OutcomeJson = JsonSerializer.Serialize(new { question = "q", answer = "approve" }, AgentJson.Options),
        });

        SupervisorAmendObligation.IsOutstanding(priors, "s1").ShouldBeTrue("fixture check — the obligation must actually be outstanding for this case to test anything");
        SupervisorDecisionCoherence.MisdirectedRetry(Retry("s1"), priors).ShouldBeNull("the amendment made the verdict STALE — re-grading under the new check is exactly what the recitation asks for");
    }

    [Fact]
    public void A_retry_on_a_run_with_NO_failed_unit_is_never_re_asked()
    {
        // Narrow on purpose: with nothing failed there is no better target to point at, so the model's judgement
        // (a flaky check, a unit it wants re-run) stands rather than costing a round-trip that offers nothing.
        SupervisorDecisionCoherence.MisdirectedRetry(Retry("s1"), FanOut(("s1", Succeeded), ("s2", Succeeded))).ShouldBeNull();
    }

    [Fact]
    public void A_retry_of_a_unit_that_reported_failure_but_PASSED_its_own_check_is_not_a_failure_a_retry_is_owed()
    {
        // The P4-1 under-claim: the agent gave up on work its own oracle passed. The recitation says "do not retry,
        // merge it" — so it must not arm this rule either, or the two would contradict each other.
        SupervisorDecisionCoherence.MisdirectedRetry(Retry("s1"), FanOut(("s1", Succeeded), ("s2", UnderClaim))).ShouldBeNull();
    }

    [Fact]
    public void A_blank_target_and_a_non_retry_kind_are_never_this_defect()
    {
        var priors = FanOut(("s1", Succeeded), ("s3", Failed));

        SupervisorDecisionCoherence.MisdirectedRetry(Retry(""), priors).ShouldBeNull("MissingPayload already owns the blank target — two gates naming one defect would re-ask twice for it");
        SupervisorDecisionCoherence.MisdirectedRetry(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Spawn, Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "s1" } } }, priors).ShouldBeNull();
        SupervisorDecisionCoherence.MisdirectedRetry(Retry("s1"), Array.Empty<SupervisorPriorDecision>()).ShouldBeNull("a plan-less / un-staged run has no finished unit to protect");
    }

    private static SupervisorModelDecision Retry(string subtaskId) =>
        new() { Kind = SupervisorDecisionKinds.Retry, Retry = new SupervisorRetryPayload { SubtaskId = subtaskId } };

    private const string Succeeded = "succeeded";
    private const string Failed = "failed";
    private const string Rejected = "rejected";
    private const string Waived = "waived";
    private const string UnderClaim = "under-claim";

    /// <summary>A plan over the given units plus the one spawn that staged them all, with each unit's outcome folded — the same positional subtaskIds[i] ↔ agentResults[i] join production writes.</summary>
    private static IReadOnlyList<SupervisorPriorDecision> FanOut(params (string Id, string Outcome)[] units)
    {
        var subtasks = units.Select(u => new SupervisorPlannedSubtask { Id = u.Id, Title = u.Id, Instruction = $"do {u.Id}" }).ToArray();
        var results = units.Select(u => Result(u.Outcome)).ToArray();

        return new[]
        {
            new SupervisorPriorDecision
            {
                Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
                PayloadJson = JsonSerializer.Serialize(new SupervisorPlanPayload { Goal = "ship", Subtasks = subtasks }, AgentJson.Options),
            },
            new SupervisorPriorDecision
            {
                Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
                PayloadJson = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = units.Select(u => u.Id).ToArray() }, AgentJson.Options),
                OutcomeJson = JsonSerializer.Serialize(new { agentRunIds = results.Select(r => r.AgentRunId), agentCount = results.Length, agentResults = results }, AgentJson.Options),
            },
        };
    }

    private static SupervisorAgentResult Result(string outcome) => outcome switch
    {
        Succeeded => new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", AcceptancePassed = true, AcceptanceDetail = "tests-passed" },
        Failed => new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Failed", Error = "build failed: missing symbol" },
        Rejected => new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", AcceptancePassed = false, AcceptanceDetail = "tests-failed-exit-1" },
        Waived => new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", AcceptanceVerdict = VerificationDisposition.Waived },
        UnderClaim => new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Failed", AcceptancePassed = true, AcceptanceDetail = "tests-passed" },
        _ => throw new ArgumentException($"no fixture for outcome '{outcome}'"),
    };
}
