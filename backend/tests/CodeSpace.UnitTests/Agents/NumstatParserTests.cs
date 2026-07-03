using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the pure parse of <c>git diff --numstat</c> into per-file <see cref="FileDiffStat"/> rows — the capture
/// that makes "+X −Y" a durable fact. Pins: text counts parse; a BINARY file's "-" counts read null (not 0, so a
/// consumer sums non-nulls); zero counts survive; renames + spaced paths carry verbatim; blank / malformed / empty
/// input degrade to empty rather than throwing (a git quirk never fails a capture).
/// </summary>
[Trait("Category", "Unit")]
public class NumstatParserTests
{
    [Fact]
    public void Parses_added_and_deleted_counts_per_file()
    {
        var stats = NumstatParser.Parse("12\t3\tsrc/auth/session.ts\n0\t7\tsrc/old.ts");

        stats.Count.ShouldBe(2);
        stats[0].ShouldBe(new FileDiffStat("src/auth/session.ts", 12, 3));
        stats[1].ShouldBe(new FileDiffStat("src/old.ts", 0, 7), "a pure-deletion (0 added) survives — 0 is a real count, not absent");
    }

    [Fact]
    public void A_binary_files_counts_read_null_not_zero()
    {
        // numstat reports "-\t-" for a binary file (no line concept). Null — NOT 0 — so a consumer summing "+X −Y"
        // skips it rather than counting a phantom 0-line change.
        var stats = NumstatParser.Parse("-\t-\tassets/logo.png");

        stats.ShouldHaveSingleItem();
        stats[0].ShouldBe(new FileDiffStat("assets/logo.png", null, null));
    }

    [Fact]
    public void Parses_a_mix_of_text_and_binary_files()
    {
        var stats = NumstatParser.Parse("5\t2\treadme.md\n-\t-\timg.png\n1\t0\tsrc/a.ts");

        stats.Select(s => s.Path).ShouldBe(new[] { "readme.md", "img.png", "src/a.ts" });
        stats.Where(s => s.Additions is not null).Sum(s => s.Additions!.Value).ShouldBe(6, "the '+X' total sums the non-binary additions (5 + 1)");
        stats.Where(s => s.Deletions is not null).Sum(s => s.Deletions!.Value).ShouldBe(2, "the '−Y' total sums the non-binary deletions");
    }

    [Theory]
    // git renders a rename in numstat (detection ON — the default, so counts are the true net delta) as a brace form
    // with the common prefix/suffix folded out, or a bare "old => new". Each resolves to the file's NEW name — the same
    // name --name-only lists — so the per-file stat JOINS the changed-file list instead of orphaning on a "old => new" key.
    [InlineData("src/{old.ts => new.ts}", "src/new.ts")]   // real git output: `git mv src/old.ts src/new.ts`
    [InlineData("{a => c}/b.ts", "c/b.ts")]                 // folded common suffix
    [InlineData("old.ts => new.ts", "new.ts")]             // bare (no common prefix/suffix)
    [InlineData("dir/{sub => }/file.ts", "dir/file.ts")]   // moved OUT of a subdir — the empty side folds cleanly
    public void Resolves_a_rename_path_to_its_new_name(string numstatPath, string expected)
    {
        var stats = NumstatParser.Parse($"1\t0\t{numstatPath}");

        stats.ShouldHaveSingleItem();
        stats[0].ShouldBe(new FileDiffStat(expected, 1, 0), "a rename resolves to its new name with git's accurate net counts");
    }

    [Fact]
    public void Carries_a_non_rename_spaced_path_verbatim()
    {
        NumstatParser.Parse("5\t2\tsrc/my file.ts")[0].Path
            .ShouldBe("src/my file.ts", "a path with spaces (no rename arrow) survives verbatim — only tabs delimit columns");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void An_empty_or_blank_numstat_reads_no_stats(string? numstat)
    {
        NumstatParser.Parse(numstat).ShouldBeEmpty();
    }

    [Fact]
    public void Skips_a_malformed_row_rather_than_throwing()
    {
        // A row without the three tab fields (a git quirk / a stray line) is skipped; the well-formed rows still parse.
        var stats = NumstatParser.Parse("garbage line\n7\t1\tsrc/ok.ts\n\tonly-tabs\n42\tnot-a-number\tsrc/weird.ts");

        stats.Select(s => s.Path).ShouldBe(new[] { "src/ok.ts", "src/weird.ts" });
        stats.Single(s => s.Path == "src/ok.ts").ShouldBe(new FileDiffStat("src/ok.ts", 7, 1));
        stats.Single(s => s.Path == "src/weird.ts").ShouldBe(new FileDiffStat("src/weird.ts", 42, null), "an unparseable count degrades to null, keeping the row");
    }

    [Fact]
    public void Tolerates_trailing_newline_and_carriage_returns()
    {
        var stats = NumstatParser.Parse("3\t4\tsrc/a.ts\r\n1\t0\tsrc/b.ts\r\n");

        stats.Count.ShouldBe(2);
        stats[0].ShouldBe(new FileDiffStat("src/a.ts", 3, 4), "a CRLF line ending is trimmed — no stray \\r on the path");
        stats[1].Path.ShouldBe("src/b.ts");
    }
}
