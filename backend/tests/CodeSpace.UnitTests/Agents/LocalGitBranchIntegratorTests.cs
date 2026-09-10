using CodeSpace.Core.Services.Agents.Workspace.Integrators;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: <see cref="LocalGitBranchIntegrator.RedactedConflictDetail"/> — the pure string transform that turns
/// git's raw <c>apply --index --3way</c> stderr into something safe to persist/display: the clone directory
/// rewritten repo-relative, the credential token redacted (both its raw and URL-escaped forms), every embedded
/// newline collapsed to a single space BEFORE the length cap (a multi-line git failure must never reach a one-line
/// prompt/recipe/timeline surface as raw newlines), and the cap itself Rune-safe (never splitting a surrogate
/// pair). Internal (InternalsVisibleTo) so this is pinned directly, without staging a real conflicting git apply.
/// </summary>
[Trait("Category", "Unit")]
public class LocalGitBranchIntegratorTests
{
    [Fact]
    public void Blank_input_yields_empty_output_so_the_caller_falls_back_to_the_generic_message()
    {
        LocalGitBranchIntegrator.RedactedConflictDetail("", "/work", "tok").ShouldBe("");
        LocalGitBranchIntegrator.RedactedConflictDetail("   ", "/work", "tok").ShouldBe("");
        LocalGitBranchIntegrator.RedactedConflictDetail("\n\n", "/work", "tok").ShouldBe("");
    }

    [Fact]
    public void The_clone_directory_is_rewritten_repo_relative()
    {
        const string directory = "/tmp/codespace-agent-workspaces/integrate-abc123";
        var stderr = $"error: {directory}/src/App.cs: does not exist in index";

        LocalGitBranchIntegrator.RedactedConflictDetail(stderr, directory, null).ShouldBe("error: ./src/App.cs: does not exist in index");
    }

    [Fact]
    public void Embedded_newlines_are_collapsed_to_a_single_space_never_reaching_a_one_line_surface()
    {
        // The reported gap: a real `git apply --index --3way` failure is routinely multi-line, and the raw text used
        // to land verbatim in the decider prompt's indented block / the recipe bullet / the timeline one-liner —
        // a continuation line lost its indent and read as a top-level instruction. A RUN of whitespace (a CRLF pair,
        // a doubled blank line) collapses to ONE space, not one space per character, so no extra padding survives.
        const string stderr = "error: patch failed: f.txt:1\r\n\r\nerror: f.txt: patch does not apply\nU f.txt\n";

        var detail = LocalGitBranchIntegrator.RedactedConflictDetail(stderr, "/work", null);

        detail.ShouldNotContain("\n");
        detail.ShouldNotContain("\r");
        detail.ShouldBe("error: patch failed: f.txt:1 error: f.txt: patch does not apply U f.txt");
    }

    [Fact]
    public void Both_the_raw_token_and_its_url_escaped_form_are_redacted()
    {
        // BuildAuthenticatedUrl embeds Uri.EscapeDataString(token) in the clone/push argv, so a token with
        // URL-special characters appears ENCODED in a failing git command — redacting only the raw literal would
        // leak the reversible encoded form.
        const string token = "tok@en/special+chars";
        var escaped = Uri.EscapeDataString(token);

        var rawStderr = $"fatal: could not read Username for 'https://x-access-token:{token}@github.com/o/r.git'";
        var escapedStderr = $"fatal: could not read Username for 'https://x-access-token:{escaped}@github.com/o/r.git'";

        var redactedRaw = LocalGitBranchIntegrator.RedactedConflictDetail(rawStderr, "/work", token);
        var redactedEscaped = LocalGitBranchIntegrator.RedactedConflictDetail(escapedStderr, "/work", token);

        redactedRaw.ShouldNotContain(token);
        redactedRaw.ShouldContain("***");
        redactedEscaped.ShouldNotContain(escaped);
        redactedEscaped.ShouldContain("***");
    }

    [Fact]
    public void Long_output_is_capped_and_marked_with_an_ellipsis()
    {
        var stderr = new string('x', 600);

        var detail = LocalGitBranchIntegrator.RedactedConflictDetail(stderr, "/work", null);

        detail.Length.ShouldBe(513, "512 chars (mirrors LocalGitBranchIntegrator's private ConflictDetailCapChars) + the ellipsis marker");
        detail.ShouldStartWith(new string('x', 512));
        detail.ShouldEndWith("…");
    }

    [Fact]
    public void The_cap_never_splits_a_surrogate_pair()
    {
        // A Rune straddling the 512-char cap boundary: 511 filler chars put the emoji's HIGH surrogate exactly at
        // index 511 (text[maxChars-1]) — slicing there naively would emit the lone high half with no low partner.
        var filler = new string('a', 511);
        const string emoji = "\U0001F600";   // one Rune, two UTF-16 code units (a surrogate pair)
        var stderr = filler + emoji + new string('b', 100);

        var detail = LocalGitBranchIntegrator.RedactedConflictDetail(stderr, "/nonexistent", null);

        detail.ShouldBe(filler + "…", "the cap drops the WHOLE surrogate pair rather than emit its unpaired high half");
    }
}
