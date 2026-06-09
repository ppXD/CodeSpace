using System.Text;
using CodeSpace.Core.Services.Providers.Source;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Source;

/// <summary>
/// Pure decode logic behind the Code browser's file viewer — the binary/truncated/text decision that
/// every provider funnels its bytes through. No SDK, no IO, so every branch is exercised directly.
/// </summary>
[Trait("Category", "Unit")]
public class FileContentDecoderTests
{
    [Fact]
    public void Plain_text_decodes_to_text_with_metadata()
    {
        var bytes = Encoding.UTF8.GetBytes("hello\nworld\n");

        var result = FileContentDecoder.Build("src/a.txt", "a.txt", bytes, "sha-1");

        result.Text.ShouldBe("hello\nworld\n");
        result.IsBinary.ShouldBeFalse();
        result.IsTruncated.ShouldBeFalse();
        result.Size.ShouldBe(bytes.LongLength);
        result.Sha.ShouldBe("sha-1");
        result.Path.ShouldBe("src/a.txt");
        result.Name.ShouldBe("a.txt");
    }

    [Fact]
    public void Multibyte_utf8_round_trips()
    {
        var bytes = Encoding.UTF8.GetBytes("héllo — 世界 🌍");

        FileContentDecoder.Build("p", "n", bytes, null).Text.ShouldBe("héllo — 世界 🌍");
    }

    [Fact]
    public void Leading_utf8_bom_is_stripped()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("x = 1")).ToArray();

        var result = FileContentDecoder.Build("p", "n", bytes, null);

        result.Text.ShouldBe("x = 1");
        result.Text!.ShouldNotStartWith("﻿");
    }

    [Fact]
    public void Empty_file_is_empty_text_not_binary()
    {
        var result = FileContentDecoder.Build("p", "n", Array.Empty<byte>(), null);

        result.Text.ShouldBe("");
        result.IsBinary.ShouldBeFalse();
        result.IsTruncated.ShouldBeFalse();
        result.Size.ShouldBe(0);
    }

    [Fact]
    public void Nul_byte_marks_binary()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x00, 0x4E, 0x47 };   // NUL mid-stream — the git binary heuristic

        var result = FileContentDecoder.Build("img.png", "img.png", bytes, null);

        result.IsBinary.ShouldBeTrue();
        result.Text.ShouldBeNull();
        result.IsTruncated.ShouldBeFalse();
        result.Size.ShouldBe(5);
    }

    [Fact]
    public void Null_content_marks_truncated_and_keeps_reported_size()
    {
        var result = FileContentDecoder.Build("big.bin", "big.bin", content: null, sha: "s", reportedSize: 5_000_000);

        result.IsTruncated.ShouldBeTrue();
        result.Text.ShouldBeNull();
        result.IsBinary.ShouldBeFalse();
        result.Size.ShouldBe(5_000_000);
    }

    [Fact]
    public void Content_over_cap_marks_truncated()
    {
        var result = FileContentDecoder.Build("p", "n", new byte[101], null, reportedSize: null, maxInlineBytes: 100);

        result.IsTruncated.ShouldBeTrue();
        result.Text.ShouldBeNull();
    }

    [Fact]
    public void Content_exactly_at_cap_is_inlined()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('a', 100));

        var result = FileContentDecoder.Build("p", "n", bytes, null, reportedSize: null, maxInlineBytes: 100);

        result.IsTruncated.ShouldBeFalse();
        result.Text!.Length.ShouldBe(100);
    }

    [Fact]
    public void Reported_size_over_cap_truncates_even_when_byte_blob_is_small()
    {
        // GitHub can report a large Size while returning a small/empty content blob — trust the larger signal.
        var result = FileContentDecoder.Build("p", "n", Encoding.UTF8.GetBytes("partial"), null, reportedSize: 10_000_000);

        result.IsTruncated.ShouldBeTrue();
        result.Text.ShouldBeNull();
        result.Size.ShouldBe(10_000_000);
    }
}
