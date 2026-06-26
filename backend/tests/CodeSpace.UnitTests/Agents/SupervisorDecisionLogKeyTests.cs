using CodeSpace.Core.Services.Supervisor;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the server-derived idempotency key. The key is <c>decisionKind:SHA-256(payload)</c> (+ an optional caller
/// turn discriminator) — SERVER-derived + deterministic, NEVER from a model-supplied key. Pins the determinism, the
/// per-input divergence, and the turn-discriminator effect.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDecisionLogKeyTests
{
    private const string Payload = """{"goal":"ship it","next":"spawn"}""";

    [Fact]
    public void Key_is_deterministic_for_the_same_kind_and_payload()
    {
        var a = SupervisorDecisionLog.DeriveIdempotencyKey("plan", Payload);
        var b = SupervisorDecisionLog.DeriveIdempotencyKey("plan", Payload);

        a.ShouldBe(b, "the same (kind, payload) always derives the same key — exactly-once dedup depends on it");
    }

    [Fact]
    public void Key_is_prefixed_by_the_decision_kind_and_carries_a_64_hex_sha256()
    {
        var key = SupervisorDecisionLog.DeriveIdempotencyKey("spawn", Payload);

        key.ShouldStartWith("spawn:");   // the kind prefixes the key (grep-able + namespaced per decision kind)

        var hash = key["spawn:".Length..];
        hash.Length.ShouldBe(64, "the suffix is a hex SHA-256 of the canonical payload");
        hash.ShouldAllBe(c => Uri.IsHexDigit(c) && !char.IsUpper(c));   // lower-case hex, matching ToolCallKey's convention
    }

    [Fact]
    public void A_different_payload_is_a_different_key()
    {
        var a = SupervisorDecisionLog.DeriveIdempotencyKey("plan", Payload);
        var b = SupervisorDecisionLog.DeriveIdempotencyKey("plan", """{"goal":"different"}""");

        a.ShouldNotBe(b, "a different payload hashes differently → a distinct, separately-executable decision");
    }

    [Fact]
    public void A_different_decision_kind_is_a_different_key()
    {
        var plan = SupervisorDecisionLog.DeriveIdempotencyKey("plan", Payload);
        var spawn = SupervisorDecisionLog.DeriveIdempotencyKey("spawn", Payload);

        plan.ShouldNotBe(spawn, "the same payload under a different kind is a different decision");
    }

    [Fact]
    public void The_turn_discriminator_makes_the_same_payload_a_distinct_decision_per_turn()
    {
        var turn1 = SupervisorDecisionLog.DeriveIdempotencyKey("retry", Payload, turnDiscriminator: "turn-1");
        var turn2 = SupervisorDecisionLog.DeriveIdempotencyKey("retry", Payload, turnDiscriminator: "turn-2");
        var noTurn = SupervisorDecisionLog.DeriveIdempotencyKey("retry", Payload);

        turn1.ShouldNotBe(turn2, "the SAME decision payload in a later turn is a distinct, re-executable decision");
        turn1.ShouldNotBe(noTurn, "binding a turn discriminator changes the key vs. the discriminator-free derivation");
    }

    [Fact]
    public void HashPayload_matches_the_key_suffix()
    {
        var key = SupervisorDecisionLog.DeriveIdempotencyKey("plan", Payload);
        var hash = SupervisorDecisionLog.HashPayload(Payload);

        key.ShouldBe($"plan:{hash}", "the audit input_hash column is exactly the hash the (no-turn) key binds");
    }
}
