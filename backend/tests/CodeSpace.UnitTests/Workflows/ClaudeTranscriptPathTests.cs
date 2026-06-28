using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: <see cref="ClaudeTranscriptPath"/> reproduces Claude Code's transcript-file location so a CONTINUE can
/// RESTORE a prior session's JSONL where the CLI looks for it on <c>--resume</c>. The known-pairs are the LOAD-BEARING
/// pin: they are REAL <c>~/.claude/projects</c> directory names observed on a machine running claude 2.1.193, so a
/// drift in the cwd→sanitized-dir encoding fails HERE at test time rather than as a silent failed real-CLI resume
/// (the sharpest P3 hazard — a mismatch lands the transcript under the wrong dir and <c>--resume</c> cold-starts with
/// no error).
/// </summary>
[Trait("Category", "Unit")]
public class ClaudeTranscriptPathTests
{
    [Theory]
    // Ground truth — a byte-exact port of the real claude 2.1.193 `ab()`: replace every char outside [A-Za-z0-9] with
    // '-' (so '/', '.', AND '_' all become '-'); alphanumerics + existing '-' survive (they map to themselves).
    [InlineData("/Users/mars/Projects/CodeSpace", "-Users-mars-Projects-CodeSpace")]                              // real ~/.claude/projects dir
    [InlineData("/Users/mars/Projects/CodeSpace/backend/src/CodeSpace.Core", "-Users-mars-Projects-CodeSpace-backend-src-CodeSpace-Core")]   // real dir; the '.' in CodeSpace.Core → '-'
    [InlineData("/private/var/folders/z7/qrtkqj255vs6dg3wjfkgcn380000gn/T/codespace-agent-workspaces/05e4e233e0c5482985cbddd01d1a72a4",
                "-private-var-folders-z7-qrtkqj255vs6dg3wjfkgcn380000gn-T-codespace-agent-workspaces-05e4e233e0c5482985cbddd01d1a72a4")]   // resolved agent-workspace cwd (/private, not /var)
    [InlineData("/Users/john_doe/my_project", "-Users-john-doe-my-project")]   // UNDERSCORE → '-' (the real binary does NOT preserve '_') — ground truth via the extracted ab()
    [InlineData("/Users/a_b/x.y/z", "-Users-a-b-x-y-z")]                       // mixed '_' + '.' → '-'
    public void EncodeCwd_matches_the_real_claude_encoder(string cwd, string expected) =>
        ClaudeTranscriptPath.EncodeCwd(cwd).ShouldBe(expected);

    [Fact]
    public void EncodeCwd_truncates_at_200_and_appends_the_real_base36_hash_for_a_deep_cwd()
    {
        // Ground truth from the real `ab()` (run via node on the extracted functions): a sanitized cwd over 200 chars
        // is truncated to 200 + "-" + base36(abs(djb2(ORIGINAL cwd))). The hash suffix is the NON-CIRCULAR pin — it
        // comes from the real algorithm over the original path, so a wrong djb2/base36 port fails here.
        var cwd = "/private/var/folders/z7/abc/T/codespace-agent-workspaces/" + new string('a', 180) + "/repo/src/deep";

        var encoded = ClaudeTranscriptPath.EncodeCwd(cwd);

        encoded.Length.ShouldBe(207, "200 truncated chars + '-' + the 6-char hash");
        encoded.ShouldStartWith("-private-var-folders-z7-abc-T-codespace-agent-workspaces-");
        encoded.ShouldEndWith("-e2qqel", customMessage: "base36(abs(djb2(original cwd))) — the real binary's hash suffix");
    }

    [Fact]
    public void For_builds_the_projects_relative_transcript_path()
    {
        ClaudeTranscriptPath.For("/Users/mars/Projects/CodeSpace", "sess-abc-123")
            .ShouldBe("projects/-Users-mars-Projects-CodeSpace/sess-abc-123.jsonl");
    }

    [Fact]
    public void Encoded_cwd_is_always_a_single_path_segment_so_it_cannot_escape_the_config_home()
    {
        // The sanitizer turns every separator into '-', so the result has no '/' or '\\' — the transcript can only land
        // UNDER projects/, never traverse out of the per-run config home (the runner's path-escape guard stays satisfied).
        var encoded = ClaudeTranscriptPath.EncodeCwd("/a/../../etc/passwd");

        encoded.ShouldNotContain("/");
        encoded.ShouldNotContain("\\");
        encoded.ShouldBe("-a-------etc-passwd", "even '..' segments are flattened to '-' — no traversal survives the sanitizer");
    }
}
