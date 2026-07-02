using CodeSpace.Core.Services.Sessions.Room;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// The pure per-file extraction from a git unified diff: match a repo-relative path to its block, classify the change
/// off the git header, reconstruct an added file's full content, and offer the raw section for everything else. No IO.
/// </summary>
[Trait("Category", "Unit")]
public class UnifiedPatchReaderTests
{
    private const string AddedMd =
        "diff --git a/docs/plan.md b/docs/plan.md\n" +
        "new file mode 100644\n" +
        "index 0000000..1111111\n" +
        "--- /dev/null\n" +
        "+++ b/docs/plan.md\n" +
        "@@ -0,0 +1,3 @@\n" +
        "+# Plan\n" +
        "+\n" +
        "+Ship it.\n";

    [Fact]
    public void An_added_text_file_reconstructs_its_full_content()
    {
        var view = UnifiedPatchReader.Read(AddedMd, "docs/plan.md");

        view.ShouldNotBeNull();
        view!.Change.ShouldBe(PatchFileChange.Added);
        view.IsBinary.ShouldBeFalse();
        view.PostImage.ShouldBe("# Plan\n\nShip it.");
    }

    [Fact]
    public void A_modified_file_offers_the_diff_but_no_reconstructed_content()
    {
        var patch =
            "diff --git a/src/x.cs b/src/x.cs\n" +
            "index aaa..bbb 100644\n" +
            "--- a/src/x.cs\n" +
            "+++ b/src/x.cs\n" +
            "@@ -1,3 +1,3 @@\n" +
            " unchanged\n" +
            "-var old = 1;\n" +
            "+var next = 2;\n" +
            " tail\n";

        var view = UnifiedPatchReader.Read(patch, "src/x.cs");

        view.ShouldNotBeNull();
        view!.Change.ShouldBe(PatchFileChange.Modified);
        view.PostImage.ShouldBeNull();
        view.DiffText.ShouldContain("+var next = 2;");
        view.DiffText.ShouldContain("-var old = 1;");
    }

    [Fact]
    public void A_deleted_file_is_classified_deleted_and_matched_by_its_a_side_path()
    {
        var patch =
            "diff --git a/old/gone.txt b/old/gone.txt\n" +
            "deleted file mode 100644\n" +
            "index ccc..0000000\n" +
            "--- a/old/gone.txt\n" +
            "+++ /dev/null\n" +
            "@@ -1,2 +0,0 @@\n" +
            "-line one\n" +
            "-line two\n";

        var view = UnifiedPatchReader.Read(patch, "old/gone.txt");

        view.ShouldNotBeNull();
        view!.Change.ShouldBe(PatchFileChange.Deleted);
        view.PostImage.ShouldBeNull();
    }

    [Fact]
    public void A_renamed_file_is_matched_by_its_rename_target()
    {
        var patch =
            "diff --git a/old-name.md b/new-name.md\n" +
            "similarity index 100%\n" +
            "rename from old-name.md\n" +
            "rename to new-name.md\n";

        var view = UnifiedPatchReader.Read(patch, "new-name.md");

        view.ShouldNotBeNull();
        view!.Change.ShouldBe(PatchFileChange.Renamed);
    }

    [Fact]
    public void A_binary_file_is_flagged_binary_with_no_content()
    {
        var patch =
            "diff --git a/assets/logo.png b/assets/logo.png\n" +
            "new file mode 100644\n" +
            "index 0000000..2222222\n" +
            "Binary files /dev/null and b/assets/logo.png differ\n";

        var view = UnifiedPatchReader.Read(patch, "assets/logo.png");

        view.ShouldNotBeNull();
        view!.Change.ShouldBe(PatchFileChange.Binary);
        view.IsBinary.ShouldBeTrue();
        view.PostImage.ShouldBeNull();
    }

    [Fact]
    public void The_matching_block_is_picked_out_of_a_multi_file_diff()
    {
        var patch = AddedMd +
            "diff --git a/src/y.cs b/src/y.cs\n" +
            "index ddd..eee 100644\n" +
            "--- a/src/y.cs\n" +
            "+++ b/src/y.cs\n" +
            "@@ -1 +1 @@\n" +
            "-a\n" +
            "+b\n";

        UnifiedPatchReader.Read(patch, "docs/plan.md")!.Change.ShouldBe(PatchFileChange.Added);

        var y = UnifiedPatchReader.Read(patch, "src/y.cs");
        y.ShouldNotBeNull();
        y!.Change.ShouldBe(PatchFileChange.Modified);
        y.DiffText.ShouldNotContain("plan.md");
    }

    [Fact]
    public void A_c_quoted_path_matches_its_block_in_the_same_quoted_form()
    {
        // git c-quotes a non-ASCII / special-char path IDENTICALLY in --name-only (the ChangedFiles the caller queried
        // with) and the +++ header, so the reader must match the quoted string, not the unquoted bytes.
        var quoted = "\"\\346\\226\\207\\346\\241\\243.md\"";   // "文档.md", c-quoted
        var patch =
            "diff --git \"a/\\346\\226\\207\\346\\241\\243.md\" \"b/\\346\\226\\207\\346\\241\\243.md\"\n" +
            "new file mode 100644\n" +
            "index 0000000..1111111\n" +
            "--- /dev/null\n" +
            "+++ \"b/\\346\\226\\207\\346\\241\\243.md\"\n" +
            "@@ -0,0 +1,1 @@\n" +
            "+hi\n";

        var view = UnifiedPatchReader.Read(patch, quoted);

        view.ShouldNotBeNull();
        view!.Change.ShouldBe(PatchFileChange.Added);
        view.PostImage.ShouldBe("hi");
    }

    [Fact]
    public void An_added_crlf_file_keeps_its_carriage_returns_in_the_reconstructed_content()
    {
        // git separates diff lines with LF; a CRLF file's content line is "+line\r" where the \r is content, not a
        // separator — the reconstruction must not collapse it to LF.
        var patch =
            "diff --git a/win.txt b/win.txt\n" +
            "new file mode 100644\n" +
            "index 0000000..2222222\n" +
            "--- /dev/null\n" +
            "+++ b/win.txt\n" +
            "@@ -0,0 +1,2 @@\n" +
            "+line1\r\n" +
            "+line2\r\n";

        UnifiedPatchReader.Read(patch, "win.txt")!.PostImage.ShouldBe("line1\r\nline2\r");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a diff at all")]
    public void A_patch_with_no_matching_block_returns_null(string patch)
    {
        UnifiedPatchReader.Read(patch, "docs/plan.md").ShouldBeNull();
    }

    [Fact]
    public void A_path_absent_from_the_diff_returns_null()
    {
        UnifiedPatchReader.Read(AddedMd, "docs/other.md").ShouldBeNull();
    }
}
