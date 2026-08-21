using System.Text;
using CodeSpace.Core.Services.Sessions.Room;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// The Room preview limit is a wire/DOM UTF-8 BYTE contract, not a UTF-16 character limit. These tests invoke the
/// projection seam directly so no database, artifact carrier or file selection can hide a byte-overrun or broken rune.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RoomFilePreviewCapTests
{
    private const int MaxPreviewBytes = 512 * 1024;

    [Fact]
    public void Empty_and_untruncated_text_are_byte_identical()
    {
        var empty = RoomFilePreviewService.Cap("");
        empty.Text.ShouldBeSameAs("");
        empty.Size.ShouldBe(0);
        empty.Truncated.ShouldBeFalse();

        var body = "A界😀e\u0301";
        var small = RoomFilePreviewService.Cap(body);
        small.Text.ShouldBeSameAs(body, "a body within the UTF-8 byte budget must be returned byte-for-byte, with no normalization or copy");
        small.Size.ShouldBe(Encoding.UTF8.GetByteCount(body));
        small.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void Ascii_at_the_exact_byte_boundary_is_unchanged_and_one_byte_over_is_cut_exactly()
    {
        var exactBody = new string('a', MaxPreviewBytes);
        var exact = RoomFilePreviewService.Cap(exactBody);
        exact.Text.ShouldBeSameAs(exactBody);
        exact.Size.ShouldBe(MaxPreviewBytes);
        exact.Truncated.ShouldBeFalse();

        var over = RoomFilePreviewService.Cap(exactBody + "z");
        over.Text.ShouldBe(exactBody);
        Encoding.UTF8.GetByteCount(over.Text).ShouldBe(MaxPreviewBytes);
        over.Size.ShouldBe(MaxPreviewBytes + 1);
        over.Truncated.ShouldBeTrue();
    }

    [Fact]
    public void Cjk_is_capped_by_utf8_bytes_not_utf16_code_units()
    {
        var body = string.Concat(Enumerable.Repeat("界", MaxPreviewBytes / 3 + 2));

        var capped = RoomFilePreviewService.Cap(body);

        capped.Size.ShouldBe(Encoding.UTF8.GetByteCount(body));
        Encoding.UTF8.GetByteCount(capped.Text).ShouldBeLessThanOrEqualTo(MaxPreviewBytes);
        capped.Text.Length.ShouldBe(MaxPreviewBytes / 3);
        capped.Truncated.ShouldBeTrue();
        AssertWellFormed(capped.Text);
    }

    [Fact]
    public void An_astral_rune_crossing_the_boundary_is_excluded_whole_never_split_into_a_surrogate()
    {
        var prefix = new string('a', MaxPreviewBytes - 1);
        var body = prefix + "😀tail";

        var capped = RoomFilePreviewService.Cap(body);

        capped.Text.ShouldBe(prefix, "the four-byte rune does not fit in the one remaining byte and must be excluded whole");
        Encoding.UTF8.GetByteCount(capped.Text).ShouldBe(MaxPreviewBytes - 1);
        capped.Size.ShouldBe(Encoding.UTF8.GetByteCount(body));
        capped.Truncated.ShouldBeTrue();
        AssertWellFormed(capped.Text);
    }

    [Fact]
    public void A_combining_code_point_may_be_cut_from_its_base_but_never_encoded_as_replacement()
    {
        var prefix = new string('a', MaxPreviewBytes - 1);
        var body = prefix + "\u0301tail";

        var capped = RoomFilePreviewService.Cap(body);

        capped.Text.ShouldBe(prefix, "grapheme preservation is not required, but the two-byte combining code point cannot overrun the byte cap");
        Encoding.UTF8.GetByteCount(capped.Text).ShouldBe(MaxPreviewBytes - 1);
        capped.Truncated.ShouldBeTrue();
        AssertWellFormed(capped.Text);
    }

    private static void AssertWellFormed(string text)
    {
        text.ShouldNotContain('\uFFFD');
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])).ShouldBeTrue("a high surrogate must retain its low-surrogate half");
                i++;
            }
            else
            {
                char.IsLowSurrogate(text[i]).ShouldBeFalse("a low surrogate must retain its high-surrogate half");
            }
        }
    }
}
