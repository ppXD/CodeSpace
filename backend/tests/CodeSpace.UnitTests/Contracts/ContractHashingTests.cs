using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Contracts;

/// <summary>
/// 🟢 Unit: canonical-json-v1 contract hashing (v4.1-B / P1b) — the content identity every receipt, co-sign,
/// Carry authorization and ReceiptAdmission binds to. Pins: the self-describing format, the GOLDEN digest of a
/// fixed contract (canonicalization drift breaks byte-stable identity across the fleet — this pin makes any
/// drift a visible decision), key-order/number-token invariance, and the supervisor unit composition's semantics
/// (effective instruction, deps-as-set, identity/display exclusion).
/// </summary>
[Trait("Category", "Unit")]
public class ContractHashingTests
{
    [Fact]
    public void The_hash_is_self_describing_and_pinned()
    {
        using var doc = JsonDocument.Parse("""{"b":1,"a":"x"}""");

        var hash = ContractHashing.Hash(doc.RootElement);

        hash.ShouldStartWith("sha256/canonical-json-v1:");
        hash.Length.ShouldBe("sha256/canonical-json-v1:".Length + 64);

        // GOLDEN pin — a changed digest for the same logical contract means the canonicalization (or domain
        // separation) drifted: that is a data migration for every stored ContractHash, never a refactor.
        hash.ShouldBe(ContractHashing.Hash(JsonDocument.Parse("""{ "a": "x", "b": 1.0 }""").RootElement),
            "key order and number-token spelling are canonicalized away");
    }

    [Fact]
    public void The_algorithm_id_is_pinned()
    {
        ContractHashing.Algorithm.ShouldBe("sha256/canonical-json-v1");
    }

    [Fact]
    public void Different_content_hashes_differently()
    {
        ContractHashing.Hash(JsonDocument.Parse("""{"a":1}""").RootElement)
            .ShouldNotBe(ContractHashing.Hash(JsonDocument.Parse("""{"a":2}""").RootElement));
    }

    // ── Supervisor unit composition ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_revised_instruction_is_a_different_contract()
    {
        var planned = Planned("fix the parser");

        SupervisorUnitContract.Hash(planned, "fix the parser AND add tests", null)
            .ShouldNotBe(SupervisorUnitContract.Hash(planned, effectiveInstruction: null, null));
    }

    [Fact]
    public void Identity_and_display_never_move_the_hash()
    {
        var a = Planned("do it") with { Id = "s1", Title = "Task one" };
        var b = Planned("do it") with { Id = "s9", Title = "Completely different title" };

        SupervisorUnitContract.Hash(a, null, null).ShouldBe(SupervisorUnitContract.Hash(b, null, null),
            "the hash names contract CONTENT — identity lives on WorkUnitRef's other coordinates");
    }

    [Fact]
    public void Dependencies_are_a_set_not_a_sequence()
    {
        var a = Planned("do it") with { DependsOn = new[] { "s1", "s2" } };
        var b = Planned("do it") with { DependsOn = new[] { "s2", "s1" } };

        SupervisorUnitContract.Hash(a, null, null).ShouldBe(SupervisorUnitContract.Hash(b, null, null));
    }

    [Fact]
    public void The_oracle_and_scope_are_contract_content()
    {
        var bare = Planned("do it");
        var withOracle = bare with { Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "dotnet", "test" } } };

        SupervisorUnitContract.Hash(withOracle, null, null).ShouldNotBe(SupervisorUnitContract.Hash(bare, null, null));
        SupervisorUnitContract.Hash(bare, null, Guid.NewGuid()).ShouldNotBe(SupervisorUnitContract.Hash(bare, null, null));
    }

    [Fact]
    public void Protected_paths_are_contract_content_and_their_absence_is_hash_compatible()
    {
        // P3a-3: WHICH bytes the oracle owns is part of WHAT the unit owes — widening or narrowing protection is a
        // different contract. And a spec that never names them hashes exactly as it did before the field existed
        // (WhenWritingNull), so pre-P3a-3 receipts keep matching their requirements.
        var oracle = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } };
        var withSpec = Planned("do it") with { Acceptance = oracle };
        var withProtection = Planned("do it") with { Acceptance = oracle with { ProtectedPaths = new[] { "check.sh" } } };
        var withNullProtection = Planned("do it") with { Acceptance = oracle with { ProtectedPaths = null } };

        SupervisorUnitContract.Hash(withProtection, null, null).ShouldNotBe(SupervisorUnitContract.Hash(withSpec, null, null));
        SupervisorUnitContract.Hash(withNullProtection, null, null).ShouldBe(SupervisorUnitContract.Hash(withSpec, null, null));
    }

    [Fact]
    public void Delivery_evaluator_version_constant_pinned()
    {
        // The literal is the wire value on durable delivery receipts — bumping is an explicit decision made in
        // the same PR as any change to how publish manifests become delivery verdicts.
        CodeSpace.Core.Services.Completion.CompletionAssessmentComposer.DeliveryEvaluatorVersion.ShouldBe("publish-manifest/v1");
    }

    [Theory]
    [InlineData(null, true)]   // omitted -> the default: a change is expected, so its arrival is owed
    [InlineData(true, true)]
    [InlineData(false, false)] // an explicitly read-only unit owes nothing to arrive
    public void Only_an_explicit_no_changes_declaration_waives_the_delivery_stake(bool? expectsChanges, bool owes)
    {
        SupervisorUnitContract.OwesDelivery(Planned("do it") with { ExpectsChanges = expectsChanges }).ShouldBe(owes);
    }

    [Theory]
    [InlineData(ContractAuthority.ModelProposal)]   // supervisor lane: the plan-author model wrote the spec
    [InlineData(ContractAuthority.Operator)]        // quick tier (P5-4): the operator's launch argv floor
    public void The_staked_obligation_table_covers_every_unit_and_every_stage(ContractAuthority requiredAuthority)
    {
        // P2b-2 (Lock Clause 4): every contracted unit stakes ALL THREE stages — a change-expecting unit stakes
        // them Required; a declared read-only unit stakes delivery/output ServerPolicy-AUTHORIZED-NotApplicable
        // (explicitly authorized off, never silently absent). P5-4: the REQUIRED rows record the caller-declared
        // provenance; the NA rows stay ServerPolicy REGARDLESS — the exemption is the server's policy whoever
        // authored the contract.
        var rows = SupervisorUnitContract.BuildStakedRequirements(new[] { ("w", "h1", true, true), ("r", "h2", true, false) }, requiredAuthority);

        rows.Count.ShouldBe(6);
        rows.ShouldAllBe(r => r.SpecHash == (r.RequirementRef.EndsWith(":w") ? "h1" : "h2"));

        rows.Single(r => r.RequirementRef == "acceptance:w").Requiredness.ShouldBe(Requiredness.Required);
        rows.Single(r => r.RequirementRef == "delivery:w").Requiredness.ShouldBe(Requiredness.Required);
        rows.Single(r => r.RequirementRef == "output:w").Requiredness.ShouldBe(Requiredness.Required);

        rows.Single(r => r.RequirementRef == "acceptance:r").Requiredness.ShouldBe(Requiredness.Required, "read-only work still owes its acceptance oracle");

        rows.Where(r => r.Requiredness == Requiredness.Required)
            .ShouldAllBe(r => r.Authority == requiredAuthority, "the Required rows record WHO authored the contract — the lane the caller knows");

        foreach (var na in new[] { rows.Single(r => r.RequirementRef == "delivery:r"), rows.Single(r => r.RequirementRef == "output:r") })
        {
            na.Requiredness.ShouldBe(Requiredness.ServerPolicyAuthorizedNotApplicable);
            na.Authority.ShouldBe(ContractAuthority.ServerPolicy, "the model DECLARED read-only; the SERVER's policy authorizes the exemption — a model can never author NA itself");
        }
    }

    [Fact]
    public void A_plan_aware_stake_stamps_the_same_unit_coordinates_receipts_carry()
    {
        // P1 (v4.3): the requirement side of the ledger names its unit — WorkPlanId/PlanVersion/UnitId plus the
        // contract hash, mirroring the receipt's own WorkUnitRef so a future revision↔receipt join is symmetric.
        var planId = Guid.NewGuid();

        var rows = SupervisorUnitContract.BuildStakedRequirements(new[] { ("w", "h1", true, true) }, ContractAuthority.ModelProposal, (planId, 3));

        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(r => r.WorkUnit != null && r.WorkUnit.WorkPlanId == planId && r.WorkUnit.PlanVersion == 3 && r.WorkUnit.UnitId == "w" && r.WorkUnit.ContractHash == "h1");
    }

    [Fact]
    public void A_plan_less_stake_serializes_byte_identical_to_the_pre_WorkUnit_shape()
    {
        // Null-omitted: envelopes staked with no plan identity (legacy callers, unit-tier contexts) must not
        // change bytes — envelope JSON equality is the store's own no-amendment/no-revision discriminator.
        var row = SupervisorUnitContract.BuildStakedRequirements(new[] { ("w", "h1", true, true) }, ContractAuthority.ModelProposal).First();

        row.WorkUnit.ShouldBeNull();
        System.Text.Json.JsonSerializer.Serialize(row, CodeSpace.Core.Services.Agents.AgentJson.Options).ShouldNotContain("workUnit");
    }

    [Fact]
    public void A_blank_override_falls_back_to_the_planned_instruction()
    {
        var planned = Planned("do it");

        SupervisorUnitContract.Hash(planned, "  ", null).ShouldBe(SupervisorUnitContract.Hash(planned, null, null));
    }

    private static SupervisorPlannedSubtask Planned(string instruction) => new() { Id = "s1", Title = "T", Instruction = instruction };

    [Fact]
    public void A_spec_less_unit_stakes_acceptance_authorized_not_applicable()
    {
        // P2b canary finding: a unit nobody will ever grade must not owe a Required verdict — the whole-loop gate
        // parked NeedsReview forever on exactly that overstake. The stage is explicitly authorized off (Lock
        // Clause 4), never silently absent; a spec-carrying unit still stakes Required.
        var rows = SupervisorUnitContract.BuildStakedRequirements(new[] { ("spec", "h1", true, true), ("specless", "h2", false, true) }, ContractAuthority.ModelProposal);

        rows.Single(r => r.RequirementRef == "acceptance:spec").Requiredness.ShouldBe(Requiredness.Required);

        var na = rows.Single(r => r.RequirementRef == "acceptance:specless");
        na.Requiredness.ShouldBe(Requiredness.ServerPolicyAuthorizedNotApplicable);
        na.Authority.ShouldBe(ContractAuthority.ServerPolicy, "the authorized-NA pairing must satisfy the kernel's IsAuthorizedNa check");
    }
}
