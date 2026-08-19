using System.Text;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
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
    public void An_intermediate_directory_symlink_that_escapes_the_config_home_is_rejected()
    {
        if (OperatingSystem.IsWindows()) return;   // symlink creation needs privileges on Windows; the guard is Linux/macOS-relevant

        // The escape a SEARCH-based locate (Codex's sessions/ glob) surfaces that a LEAF-only resolve MISSES: the agent
        // plants an intermediate DIRECTORY symlink ("ln -s <secretDir> sessions/leak") and a REAL rollout under the linked
        // target. The leaf (rollout-<id>.jsonl) is a genuine file, not a symlink, so a leaf-only check passes — the clamp
        // must walk EVERY component and fail-close on the symlinked directory. This is the HIGH-severity capture-exfil vector.
        var home = Path.Combine(Path.GetTempPath(), "cs-cfg-home-" + Guid.NewGuid().ToString("N"));
        var secretDir = Path.Combine(Path.GetTempPath(), "cs-secret-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "sessions"));
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "rollout-abc.jsonl"), "HOST OPERATOR SECRET");

            Directory.CreateSymbolicLink(Path.Combine(home, "sessions", "leak"), secretDir);   // intermediate DIR symlink pointing OUT

            AgentRunExecutor.ResolveSessionTranscriptPath(home, "sessions/leak/rollout-abc.jsonl")
                .ShouldBeNull("an intermediate directory symlink escaping the config home must be refused — a leaf-only resolve would miss it");
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

    // ─── A0: the capture cap (bound the whole-file read into memory) ────────────────────────────────

    [Fact]
    public void The_capture_cap_env_var_name_is_pinned()
    {
        // Rule 8: an air-gapped/large-context operator pins this env name to raise the cap; a silent rename would break
        // their config. Hard-pin the literal so a rename is a compile-visible decision, not an invisible refactor.
        AgentRunExecutor.MaxSessionTranscriptBytesEnvVar.ShouldBe("CODESPACE_AGENT_MAX_SESSION_TRANSCRIPT_BYTES");
    }

    [Fact]
    public void The_default_capture_cap_is_32_mib()
    {
        AgentRunExecutor.DefaultMaxSessionTranscriptBytes.ShouldBe(32L * 1024 * 1024);
    }

    [Theory]
    [InlineData("1048576", 1_048_576)]     // a valid positive override wins
    [InlineData("999", 999)]
    [InlineData(null, 42)]                  // absent → fallback
    [InlineData("", 42)]                    // blank → fallback
    [InlineData("not-a-number", 42)]        // garbage → fallback
    [InlineData("0", 42)]                   // non-positive → fallback (a 0 cap would skip every capture)
    [InlineData("-5", 42)]                  // negative → fallback
    public void The_capture_cap_override_parses_a_positive_long_else_falls_back(string? raw, long expected)
    {
        AgentRunExecutor.ParseMaxSessionTranscriptBytes(raw, fallback: 42).ShouldBe(expected);
    }

    // ─── A0b: what the cap DOES — pinned against the real capture, not asserted in a doc-comment ────────
    //
    // The cap's whole observable behaviour is a pair: at-or-under it a session IS captured, over it the capture is
    // SKIPPED and the result comes back untouched. Only the pair is meaningful — "over-cap captures nothing" alone
    // would still pass if capture were broken outright, and "under-cap captures" alone would still pass if the skip
    // were deleted. Both drive the REAL CaptureSessionTranscriptAsync against a REAL ClaudeCodeHarness and a real
    // on-disk config home, so the file layout and the security clamp are the production ones.
    //
    // The skip is a DECIDED limit (see MaxSessionTranscriptBytesEnvVar's doc for the measured cost of removing it),
    // so deleting it must turn a test red rather than pass silently.

    [Fact]
    public async Task An_over_cap_session_is_skipped_and_leaves_the_result_untouched()
    {
        using var spool = new SessionSpool(AgentRunExecutor.DefaultMaxSessionTranscriptBytes + 1);
        var outcome = Result() with { Summary = "shipped it", ProducedBranch = "agent/run-1" };

        var captured = await CaptureAsync(spool, outcome);

        captured.ShouldBe(outcome, "over the cap the capture reads NOTHING — same status, same exit reason, same produced work");
        captured.SessionTranscript.ShouldBeEmpty("a session past the cap is not read whole into memory; a continue cold-starts instead");
    }

    [Fact]
    public async Task An_under_cap_session_is_captured_so_the_skip_is_not_a_dead_path()
    {
        using var spool = new SessionSpool(64 * 1024);

        var captured = await CaptureAsync(spool, Result());

        Encoding.UTF8.GetBytes(captured.SessionTranscript).ShouldBe(spool.SessionBytes, "under the cap the session is captured byte-for-byte; without this the skip test would pass on a capture that never works");
    }

    /// <summary>Run the production capture over a staged spool and return the result it produced.</summary>
    private static async Task<AgentRunResult> CaptureAsync(SessionSpool spool, AgentRunResult result)
    {
        var executor = new AgentRunExecutor(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, NullLogger<AgentRunExecutor>.Instance);
        var task = new AgentTask { Goal = "Fix the failing billing tests", Harness = ClaudeCodeHarness.HarnessKind, WorkspaceDirectory = SessionSpool.WorkspaceDirectory };

        return await executor.CaptureSessionTranscriptAsync(new AgentRunExecutor.SessionCapture(Guid.NewGuid(), task, new ClaudeCodeHarness(), spool.Handle), result, CancellationToken.None);
    }

    private static AgentRunResult Result() => new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed", SessionId = "sess-cap-boundary" };

    /// <summary>A real on-disk per-run spool: the config home the runner would have created, holding a session file at the path the REAL harness locates it by — so the test never restates the layout the production code owns.</summary>
    private sealed class SessionSpool : IDisposable
    {
        public const string WorkspaceDirectory = "/tmp/ws-cap-boundary";

        private readonly string _root = Path.Combine(Path.GetTempPath(), $"cs-spool-{Guid.NewGuid():N}");

        public SessionSpool(long atLeastBytes)
        {
            var configHome = LocalProcessRunner.ConfigHomePath(_root);
            var relative = ((IAgentSessionTranscript)new ClaudeCodeHarness()).SessionTranscriptRelativePath(configHome, WorkspaceDirectory, "sess-cap-boundary")
                ?? throw new InvalidOperationException("The harness could not address a session transcript for the staged run.");
            var path = Path.Combine(configHome, relative.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SessionBytes = BuildSession(atLeastBytes);
            File.WriteAllBytes(path, SessionBytes);

            Handle = new SandboxHandle { Kind = "local", ProcessId = 4242, SpoolDirectory = _root, Deadline = DateTimeOffset.UtcNow.AddHours(1) };
        }

        public byte[] SessionBytes { get; }

        public SandboxHandle Handle { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        /// <summary>A session of at least <paramref name="atLeastBytes"/>, repeating a realistic mixed-script line block — multi-byte CJK and an astral-plane emoji, so a byte comparison is not satisfied by pure ASCII.</summary>
        private static byte[] BuildSession(long atLeastBytes)
        {
            var block = Encoding.UTF8.GetBytes("{\"role\":\"user\",\"text\":\"重構結帳流程，先寫測試 🚀\"}\n");

            using var session = new MemoryStream();

            while (session.Length < atLeastBytes) session.Write(block);

            return session.ToArray();
        }
    }
}
