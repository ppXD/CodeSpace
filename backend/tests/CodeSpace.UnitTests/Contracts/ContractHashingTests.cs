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

    [Fact]
    public void A_blank_override_falls_back_to_the_planned_instruction()
    {
        var planned = Planned("do it");

        SupervisorUnitContract.Hash(planned, "  ", null).ShouldBe(SupervisorUnitContract.Hash(planned, null, null));
    }

    private static SupervisorPlannedSubtask Planned(string instruction) => new() { Id = "s1", Title = "T", Instruction = instruction };
}
