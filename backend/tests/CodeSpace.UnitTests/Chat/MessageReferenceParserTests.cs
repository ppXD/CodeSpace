using CodeSpace.Core.Services.Chat;
using Shouldly;

namespace CodeSpace.UnitTests.Chat;

/// <summary>
/// The generic <c>&lt;reftype:refid|label&gt;</c> tokenizer — the heart of the zero-hardcode
/// <c>@</c> system. These pin the grammar: what is a reference, what is plain text, how
/// duplicates collapse, and that an open ref-type namespace (no known-types list) holds.
/// </summary>
[Trait("Category", "Unit")]
public class MessageReferenceParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just plain text, no tokens here")]
    [InlineData("an angle < and a colon : but no token")]
    public void Bodies_without_a_token_yield_no_references(string? body)
    {
        MessageReferenceParser.Parse(body).ShouldBeEmpty();
    }

    [Fact]
    public void Parses_a_single_reference_with_a_label()
    {
        var refs = MessageReferenceParser.Parse("hey <user:550e8400-e29b-41d4-a716-446655440000|Alice> ping");

        var one = refs.ShouldHaveSingleItem();
        one.RefType.ShouldBe("user");
        one.RefId.ShouldBe("550e8400-e29b-41d4-a716-446655440000");
        one.Label.ShouldBe("Alice");
    }

    [Fact]
    public void Reference_without_a_label_has_null_label()
    {
        var one = MessageReferenceParser.Parse("see <workflow:wf-123>").ShouldHaveSingleItem();
        one.RefType.ShouldBe("workflow");
        one.RefId.ShouldBe("wf-123");
        one.Label.ShouldBeNull();
    }

    [Fact]
    public void Empty_label_normalizes_to_null()
    {
        // "<user:x|>" — a pipe with nothing after it is no label, not an empty-string label.
        MessageReferenceParser.Parse("<user:x|>").ShouldHaveSingleItem().Label.ShouldBeNull();
    }

    [Fact]
    public void Refid_keeps_internal_colons_for_a_code_location()
    {
        // A code-location refid is "repoId:sha:path:line" — the colons must survive intact;
        // only the FIRST colon (after reftype) is the type/id separator.
        var one = MessageReferenceParser.Parse("look at <code_location:repo7:abc123:src/Foo.cs:42|Foo.cs:42>").ShouldHaveSingleItem();
        one.RefType.ShouldBe("code_location");
        one.RefId.ShouldBe("repo7:abc123:src/Foo.cs:42");
        one.Label.ShouldBe("Foo.cs:42");
    }

    [Fact]
    public void Refid_keeps_a_pull_request_hash()
    {
        var one = MessageReferenceParser.Parse("blocked by <pull_request:repo7#42|PR #42>").ShouldHaveSingleItem();
        one.RefId.ShouldBe("repo7#42");
        one.Label.ShouldBe("PR #42");
    }

    [Fact]
    public void Extracts_multiple_distinct_references_in_first_seen_order()
    {
        var refs = MessageReferenceParser.Parse("<user:u1|A> and <pull_request:r#1|PR1> then <workflow:w9>");

        refs.Count.ShouldBe(3);
        refs[0].RefType.ShouldBe("user");
        refs[1].RefType.ShouldBe("pull_request");
        refs[2].RefType.ShouldBe("workflow");
    }

    [Fact]
    public void Same_type_and_id_collapses_to_one_keeping_the_first_label()
    {
        var refs = MessageReferenceParser.Parse("<user:u1|Alice> ... <user:u1|Alice again> ... <user:u1>");

        var one = refs.ShouldHaveSingleItem();
        one.RefId.ShouldBe("u1");
        one.Label.ShouldBe("Alice", customMessage: "Dedup must keep the FIRST label seen for a (type,id).");
    }

    [Fact]
    public void Same_id_under_different_types_are_distinct_references()
    {
        // The dedup key is (RefType, RefId) — an id reused across namespaces is two references.
        var refs = MessageReferenceParser.Parse("<user:x|U> vs <workflow:x|W>");

        refs.Count.ShouldBe(2);
        refs.ShouldContain(r => r.RefType == "user" && r.RefId == "x");
        refs.ShouldContain(r => r.RefType == "workflow" && r.RefId == "x");
    }

    [Fact]
    public void Open_namespace_accepts_an_unknown_ref_type_without_any_known_list()
    {
        // The whole point of the generic design: a never-before-seen kind just works.
        var one = MessageReferenceParser.Parse("<incident:INC-9001|Sev1>").ShouldHaveSingleItem();
        one.RefType.ShouldBe("incident");
        one.RefId.ShouldBe("INC-9001");
    }

    [Theory]
    [InlineData("<user>")]                 // no colon → not a token
    [InlineData("<:abc>")]                 // missing reftype
    [InlineData("<user:>")]                // empty refid (needs ≥1 char)
    [InlineData("<User:x>")]               // uppercase reftype not in [a-z]…
    [InlineData("<1user:x>")]              // reftype must start with a letter
    [InlineData("<user:x")]                // unclosed token
    [InlineData("user:x>")]                // no opening angle
    public void Malformed_tokens_are_ignored_as_plain_text(string body)
    {
        MessageReferenceParser.Parse(body).ShouldBeEmpty();
    }

    [Fact]
    public void Label_stops_at_the_first_closing_angle()
    {
        // "<user:x|a>b>" — the first '>' closes the token, so the label is "a" and "b>" is text.
        var one = MessageReferenceParser.Parse("<user:x|a>b>").ShouldHaveSingleItem();
        one.RefId.ShouldBe("x");
        one.Label.ShouldBe("a");
    }

    [Fact]
    public void Reference_at_the_very_start_and_end_of_a_body_parse()
    {
        var refs = MessageReferenceParser.Parse("<user:a|A> middle <user:b|B>");
        refs.Count.ShouldBe(2);
        refs[0].RefId.ShouldBe("a");
        refs[1].RefId.ShouldBe("b");
    }
}
