using CodeSpace.Core.Services.Agents;
using System.Runtime.InteropServices;
using System.Text;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public class SecretRedactorTests
{
    [Fact]
    public void Masks_every_occurrence_of_a_secret()
    {
        var r = new SecretRedactor(new[] { "sk-secret" });

        r.Redact("key=sk-secret and again sk-secret").ShouldBe("key=*** and again ***");
    }

    [Fact]
    public void Fingerprint_is_null_when_empty_stable_per_key_and_never_the_key()
    {
        // The re-attach safety check (a fresh observer re-tails only if its rebuilt redactor's fingerprint
        // matches the one stamped at launch) relies on: empty → null; same key → same fingerprint (stable
        // across processes/restarts); different key → different; and it's a hash, never the key itself.
        SecretRedactor.None.Fingerprint.ShouldBeNull();
        new SecretRedactor(new[] { "   " }).Fingerprint.ShouldBeNull("blank secrets are dropped → empty → no fingerprint");

        var a = new SecretRedactor(new[] { "sk-aaa-key" }).Fingerprint;
        var aAgain = new SecretRedactor(new[] { "sk-aaa-key" }).Fingerprint;
        var b = new SecretRedactor(new[] { "sk-bbb-key" }).Fingerprint;

        a.ShouldNotBeNull();
        a.ShouldBe(aAgain, "same secret → same fingerprint, so a re-attach on a fresh backend can match it");
        a.ShouldNotBe(b, "a rotated/different key → a different fingerprint → re-attach refuses to re-tail");
        a!.ShouldNotContain("sk-aaa-key", customMessage: "the fingerprint is a one-way hash, never the key itself");
    }

    [Fact]
    public void Fingerprint_is_a_function_of_the_secret_set_not_of_construction_order()
    {
        // Two EQUAL-LENGTH secrets are the case that separates a total order from a stable one: the constructor's
        // longest-first sort leaves them in whatever order the caller passed, so a fingerprint taken over that order
        // would differ between these two — and a re-attach whose needles came back in the other order would refuse to
        // re-tail a run nothing had happened to. The order belongs to the hash, not to the caller.
        var forward = new SecretRedactor(new[] { "aaa-11112222", "bbb-33334444" }).Fingerprint;
        var reversed = new SecretRedactor(new[] { "bbb-33334444", "aaa-11112222" }).Fingerprint;

        forward.ShouldNotBeNull();
        forward.ShouldBe(reversed);
    }

    [Fact]
    public void With_widens_a_redactor_without_changing_the_one_it_was_built_from()
    {
        // The seam the per-run MCP capability token arrives through — minted after the credential resolve, so it
        // cannot be a needle at construction. The receiver must stay put: the MCP endpoint is handed the narrower
        // redactor and the launch keeps using the wider one.
        var credentialOnly = new SecretRedactor(new[] { "sk-aaa-key1" });

        var widened = credentialOnly.With(new[] { "run-token-9911" });

        widened.Redact("init sk-aaa-key1 via run-token-9911").ShouldBe("init *** via ***");
        credentialOnly.Redact("init sk-aaa-key1 via run-token-9911").ShouldBe("init *** via run-token-9911");
    }

    [Fact]
    public void Masks_a_secret_embedded_in_a_larger_blob()
    {
        var r = new SecretRedactor(new[] { "sk-secret" });

        r.Redact("{\"line\":\"init key=sk-secret done\"}").ShouldBe("{\"line\":\"init key=*** done\"}");
    }

    [Fact]
    public void Masks_the_longest_secret_first_so_a_contained_secret_does_not_leave_a_tail()
    {
        // "sk-abc" is a substring of "sk-abc-def"; masking the longer first leaves no "***-def" remnant.
        var r = new SecretRedactor(new[] { "sk-abc", "sk-abc-def" });

        r.Redact("token sk-abc-def here").ShouldBe("token *** here");
    }

    [Fact]
    public void None_is_the_identity()
    {
        SecretRedactor.None.IsEmpty.ShouldBeTrue();
        SecretRedactor.None.Redact("anything sk-secret").ShouldBe("anything sk-secret");
    }

    [Fact]
    public void Blank_and_whitespace_only_secrets_are_dropped_so_they_cannot_garble_output()
    {
        var r = new SecretRedactor(new[] { "", "   " });

        r.IsEmpty.ShouldBeTrue("a blank / whitespace-only value is never a real key — dropped so it can't mask spaces");
        r.Redact("untouched   spacing").ShouldBe("untouched   spacing");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Null_or_empty_text_is_returned_as_is(string? text) =>
        new SecretRedactor(new[] { "sk-secret" }).Redact(text!).ShouldBe(text);

    [Fact]
    public void Utf8_stream_masks_a_secret_split_across_arbitrary_byte_chunks()
    {
        var stream = new SecretRedactor(new[] { "sk-secret" }).CreateUtf8Stream();
        var first = stream.Transform(Encoding.UTF8.GetBytes("prefix sk-se"), final: false);
        var second = stream.Transform(Encoding.UTF8.GetBytes("cret suffix"), final: true);

        first.SourceBytesConsumed.ShouldBeGreaterThanOrEqualTo(0);
        first.SourceBytesConsumed.ShouldBeLessThan("prefix sk-se"u8.Length, "the possible secret prefix stays uncommitted until the next chunk");
        (first.SourceBytesConsumed + second.SourceBytesConsumed).ShouldBe("prefix sk-secret suffix"u8.Length);
        (Encoding.UTF8.GetString(first.Bytes.Span) + Encoding.UTF8.GetString(second.Bytes.Span)).ShouldBe("prefix *** suffix");
    }

    [Fact]
    public void Utf8_stream_preserves_non_utf8_bytes_and_never_uses_character_boundaries_as_source_offsets()
    {
        var stream = new SecretRedactor(new[] { "token" }).CreateUtf8Stream();
        var source = new byte[] { 0xff, 0xfe, (byte)'t', (byte)'o', (byte)'k', (byte)'e', (byte)'n', 0x80 };

        var transformed = stream.Transform(source, final: true);

        transformed.SourceBytesConsumed.ShouldBe(source.Length);
        transformed.Bytes.ToArray().ShouldBe(new byte[] { 0xff, 0xfe, (byte)'*', (byte)'*', (byte)'*', 0x80 });
    }

    [Fact]
    public void A_chunk_with_no_patterns_is_returned_without_copying()
    {
        // A run with no brokered/injected secret is the common case, and its log volume is the hot path the 4 GiB
        // capture proof stresses — this pins that path to zero allocation, not just correct content.
        var stream = SecretRedactor.None.CreateUtf8Stream();
        var source = Encoding.UTF8.GetBytes("nothing in this chunk needs to be redacted");

        var transformed = stream.Transform(source, final: false);

        transformed.SourceBytesConsumed.ShouldBe(source.Length);
        MemoryMarshal.TryGetArray(transformed.Bytes, out var segment).ShouldBeTrue();
        ReferenceEquals(segment.Array, source).ShouldBeTrue("a zero-pattern redactor must hand back the caller's own buffer, never a copy of it");
    }

    private const string FrameKey = "sk-frame-BR6FZM29FGMQJMBZ262GMMFL6KSJH3ZJ";

    /// <summary>
    /// A line longer than one read pass arrives as a frame the reader cut (incomplete) and its continuation, and each
    /// is persisted on its own — a native record, a transcript line. Exact matching per frame cannot see a key the cut
    /// split, so the end of a cut frame that could start one is held back and redacted with its continuation. Every
    /// split point of the line is tried, including several cuts in a row.
    /// </summary>
    [Fact]
    public void Frame_stream_masks_a_key_split_across_cut_frames_wherever_the_cuts_land()
    {
        var redactor = new SecretRedactor(new[] { FrameKey });
        var line = "xxxx " + FrameKey + " yyyy";

        for (var first = 0; first <= line.Length; first++)
        {
            for (var second = first; second <= line.Length; second += 7)
            {
                var stream = redactor.CreateFrameStream();
                var persisted = new[] { stream.Redact(line[..first], isComplete: false), stream.Redact(line[first..second], isComplete: false), stream.Redact(line[second..], isComplete: true) };

                string.Concat(persisted).ShouldBe("xxxx *** yyyy", $"the frames concatenate to the masked line (cuts at {first} and {second})");
                foreach (var frame in persisted)
                    for (var start = 0; start + 6 <= FrameKey.Length; start++)
                        frame.ShouldNotContain(FrameKey.Substring(start, 6), Case.Sensitive, $"a frame keeps '{FrameKey.Substring(start, 6)}' of the key (cuts at {first} and {second})");
            }
        }
    }

    [Fact]
    public void Frame_stream_holds_back_only_what_a_cut_frame_could_still_turn_into_a_key()
    {
        var stream = new SecretRedactor(new[] { FrameKey }).CreateFrameStream();

        stream.Redact("progress sk-fr", isComplete: false).ShouldBe("progress ", "the start of the key is held for the continuation");
        stream.Redact("ame-nope", isComplete: false).ShouldBe("sk-frame-nope", "a continuation that does not finish the key releases the held text verbatim");
        stream.Redact("done sk-", isComplete: true).ShouldBe("done sk-", "a complete frame ends its line: nothing can continue it, so nothing is held");
    }

    [Fact]
    public void Frame_stream_with_no_secrets_passes_every_frame_through()
    {
        var stream = SecretRedactor.None.CreateFrameStream();
        const string frame = "an incomplete frame";

        ReferenceEquals(stream.Redact(frame, isComplete: false), frame).ShouldBeTrue("a run with nothing to mask must not copy its frames");
    }
}
