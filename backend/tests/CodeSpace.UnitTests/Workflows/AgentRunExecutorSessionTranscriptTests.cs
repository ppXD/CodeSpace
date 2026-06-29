using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class AgentRunExecutorSessionTranscriptTests
{
    // The session id that NAMES the transcript file is captured from the agent's UNTRUSTED stream unescaped, so the
    // capture's path resolution is a security boundary: a benign id resolves inside the config home; a traversal id
    // must resolve to null so the executor reads nothing outside it.
    private static readonly string ConfigHome = Path.Combine(Path.GetTempPath(), "cs-cfg-home");

    [Fact]
    public void A_benign_relative_path_resolves_inside_the_config_home()
    {
        var resolved = AgentRunExecutor.ResolveSessionTranscriptPath(ConfigHome, "projects/-tmp-ws/sess-abc.jsonl");

        resolved.ShouldNotBeNull();
        resolved!.ShouldBe(Path.GetFullPath(Path.Combine(ConfigHome, "projects", "-tmp-ws", "sess-abc.jsonl")));
    }

    [Theory]
    [InlineData("projects/-tmp-ws/../../../../etc/passwd.jsonl")]   // climb out via a hostile session id
    [InlineData("projects/-tmp-ws/../../../../../../../../etc/hostname.jsonl")]
    [InlineData("../outside.jsonl")]
    public void A_traversal_path_resolves_to_null_so_nothing_outside_the_config_home_is_read(string hostileRelativePath)
    {
        AgentRunExecutor.ResolveSessionTranscriptPath(ConfigHome, hostileRelativePath)
            .ShouldBeNull("a session id that traverses out of the config home must not let the capture read an arbitrary file");
    }

    [Fact]
    public void A_path_that_only_prefix_matches_a_sibling_dir_is_rejected()
    {
        // Guard against the classic prefix-match bug: "<home>-evil" starts with "<home>" as a string but is NOT inside
        // it — the separator-terminated comparison must reject it.
        AgentRunExecutor.ResolveSessionTranscriptPath(ConfigHome, "../" + Path.GetFileName(ConfigHome) + "-evil/x.jsonl")
            .ShouldBeNull("a sibling directory sharing the config-home name prefix is still outside it");
    }

    [Fact]
    public void A_symlink_that_spells_in_bounds_but_points_out_is_rejected()
    {
        if (OperatingSystem.IsWindows()) return;   // symlink creation needs privileges on Windows; the guard is Linux/macOS-relevant

        // The agent has WRITE access to its config home, so a lexically-in-bounds path can be a SYMLINK it planted that
        // points OUT ("ln -s <secret> projects/<cwd>/<id>.jsonl"). GetFullPath normalizes .. but does NOT resolve
        // symlinks — the ResolveLinkTarget re-clamp must catch this and return null so the secret is never read.
        var home = Path.Combine(Path.GetTempPath(), "cs-cfg-home-" + Guid.NewGuid().ToString("N"));
        var secretDir = Path.Combine(Path.GetTempPath(), "cs-secret-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "projects", "ws"));
            Directory.CreateDirectory(secretDir);
            var secret = Path.Combine(secretDir, "secret.txt");
            File.WriteAllText(secret, "TOP SECRET");

            var planted = Path.Combine(home, "projects", "ws", "sess.jsonl");
            File.CreateSymbolicLink(planted, secret);   // in-bounds NAME, out-of-bounds TARGET

            AgentRunExecutor.ResolveSessionTranscriptPath(home, "projects/ws/sess.jsonl")
                .ShouldBeNull("a planted symlink whose final target escapes the config home must be refused (fail-closed)");
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
            if (Directory.Exists(secretDir)) Directory.Delete(secretDir, recursive: true);
        }
    }

    [Fact]
    public void A_regular_in_bounds_file_that_exists_is_accepted()
    {
        // The benign happy path with a REAL file present: a regular (non-symlink) in-bounds file resolves to itself —
        // the symlink re-clamp returns null for "not a link" and the lexical path is returned for the read.
        var home = Path.Combine(Path.GetTempPath(), "cs-cfg-home-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "projects", "ws"));
            var real = Path.Combine(home, "projects", "ws", "sess.jsonl");
            File.WriteAllText(real, "{}\n");

            AgentRunExecutor.ResolveSessionTranscriptPath(home, "projects/ws/sess.jsonl")
                .ShouldBe(Path.GetFullPath(real), "a regular in-bounds file is the real path and is accepted");
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }
}
