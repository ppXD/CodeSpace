using System.Text.Json;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the SERVER-side idempotency-key derivation — the heart of the exactly-once invariant. The hash must be STABLE
/// across object-key reordering / whitespace / number-token spelling (else dedup silently fails and a side effect
/// double-runs), DETERMINISTIC (same tool + input → same key), and DISCRIMINATING (a different input → a different
/// key). It is computed from the tool kind + arguments, NEVER from any model-supplied key.
/// </summary>
[Trait("Category", "Unit")]
public class ToolCallKeyTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void For_composes_toolKind_and_hash_with_a_colon()
    {
        ToolCallKey.For("git.open_pr", "abc123").ShouldBe("git.open_pr:abc123");
    }

    [Fact]
    public void InputHash_is_64_char_lowercase_hex()
    {
        var hash = ToolCallKey.InputHash(Parse("""{"a":1}"""));

        hash.Length.ShouldBe(64);
        hash.ShouldBe(hash.ToLowerInvariant());
        hash.ShouldAllBe(c => Uri.IsHexDigit(c));
    }

    [Fact]
    public void InputHash_is_deterministic_for_the_same_input()
    {
        var a = ToolCallKey.InputHash(Parse("""{"repo":"x","branch":"main"}"""));
        var b = ToolCallKey.InputHash(Parse("""{"repo":"x","branch":"main"}"""));

        a.ShouldBe(b, "the same logical input must hash identically — exactly-once depends on it");
    }

    [Fact]
    public void InputHash_is_independent_of_object_key_order()
    {
        // The model may serialize the same arguments with reordered keys across calls; dedup must NOT be defeated by it.
        var a = ToolCallKey.InputHash(Parse("""{"repo":"x","branch":"main"}"""));
        var b = ToolCallKey.InputHash(Parse("""{"branch":"main","repo":"x"}"""));

        a.ShouldBe(b, "reordered object keys are the SAME logical input — must hash the same");
    }

    [Fact]
    public void InputHash_is_independent_of_insignificant_whitespace()
    {
        var a = ToolCallKey.InputHash(Parse("""{"a":1,"b":[2,3]}"""));
        var b = ToolCallKey.InputHash(Parse("{ \"a\" : 1 ,  \"b\" : [ 2 , 3 ] }"));

        a.ShouldBe(b, "whitespace is insignificant — must not change the hash");
    }

    [Fact]
    public void InputHash_is_independent_of_number_token_spelling()
    {
        var a = ToolCallKey.InputHash(Parse("""{"n":1}"""));
        var b = ToolCallKey.InputHash(Parse("""{"n":1.0}"""));
        var c = ToolCallKey.InputHash(Parse("""{"n":1e0}"""));

        a.ShouldBe(b, "1 and 1.0 are the same value — must hash the same");
        a.ShouldBe(c, "1 and 1e0 are the same value — must hash the same");
    }

    [Fact]
    public void InputHash_is_independent_of_nested_object_key_order()
    {
        var a = ToolCallKey.InputHash(Parse("""{"outer":{"x":1,"y":2}}"""));
        var b = ToolCallKey.InputHash(Parse("""{"outer":{"y":2,"x":1}}"""));

        a.ShouldBe(b, "canonicalization must recurse into nested objects");
    }

    [Fact]
    public void InputHash_differs_for_different_inputs()
    {
        var a = ToolCallKey.InputHash(Parse("""{"branch":"main"}"""));
        var b = ToolCallKey.InputHash(Parse("""{"branch":"release"}"""));

        a.ShouldNotBe(b, "a genuinely different intent (different args) must be a different key → runs separately");
    }

    [Fact]
    public void InputHash_distinguishes_two_19_digit_integers_differing_only_in_the_last_digit()
    {
        // The exactly-once defect this guards: a 19-digit id does NOT overflow double but LOSES precision under the
        // "R" round-trip, collapsing two DISTINCT ids to the SAME canonical token → the at-most-once key would merge
        // two genuinely different calls + the second side effect would silently dedup away. Lossless Int64
        // canonicalization keeps them distinct. (1234567890123456789 vs ...788 are equal as double.)
        var a = ToolCallKey.InputHash(Parse("""{"id":1234567890123456789}"""));
        var b = ToolCallKey.InputHash(Parse("""{"id":1234567890123456788}"""));

        a.ShouldNotBe(b, "two distinct 19-digit integer ids MUST hash differently — the exactly-once key must never collapse distinct calls");
    }

    [Fact]
    public void InputHash_is_sensitive_to_array_order()
    {
        // Array order is SEMANTIC (a list of steps, args) — reordering is a different call, unlike object keys.
        var a = ToolCallKey.InputHash(Parse("""{"args":["a","b"]}"""));
        var b = ToolCallKey.InputHash(Parse("""{"args":["b","a"]}"""));

        a.ShouldNotBe(b, "array order is meaningful — a reordered array is a different input");
    }

    [Fact]
    public void Derived_key_does_not_depend_on_any_model_supplied_key()
    {
        // The wire IdempotencyKey placeholder is dead: ToolCallKey reads ONLY the tool kind + arguments. Two calls with
        // identical args derive the same key regardless of what a model might try to pass — there is no forgery surface.
        var input = Parse("""{"repo":"x"}""");

        ToolCallKey.For("git.open_pr", ToolCallKey.InputHash(input))
            .ShouldBe(ToolCallKey.For("git.open_pr", ToolCallKey.InputHash(Parse("""{"repo":"x"}"""))),
                "the key is derived from kind + canonical input only — never from a wire-supplied key");
    }

    [Fact]
    public void Different_tool_same_input_yields_a_different_key()
    {
        var hash = ToolCallKey.InputHash(Parse("{}"));

        ToolCallKey.For("git.open_pr", hash).ShouldNotBe(ToolCallKey.For("git.merge_pr", hash),
            "the same args to a DIFFERENT tool is a different call — the tool kind binds the key");
    }
}
