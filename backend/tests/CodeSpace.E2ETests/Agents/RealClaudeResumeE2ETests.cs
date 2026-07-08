using System.Diagnostics;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.E2ETests.Agents;

/// <summary>
/// 🔵 Human-gated real-CLI E2E (Rule 12, fidelity = highest available). Drives the ACTUAL <c>claude</c> binary through
/// the PRODUCTION <see cref="ClaudeCodeHarness"/> + <see cref="LocalProcessRunner.WriteConfigHomeFiles"/> to prove the
/// whole P3 continue chain works against the real CLI's on-disk contract — not a fake:
/// <list type="number">
/// <item>3.1a — the harness captures a real <c>session_id</c> from the binary's real stream-json,</item>
/// <item>3.3a — <see cref="ClaudeTranscriptPath.EncodeCwd"/> matches the exact projects-dir the binary created, and the
/// harness restores the transcript there,</item>
/// <item>3.2 — <c>--resume &lt;id&gt;</c> over the RESTORED transcript is ACCEPTED (the binary loads the prior session and
/// runs a turn), while the same resume WITHOUT the restore is REJECTED (<c>No conversation found</c>).</item>
/// </list>
/// The one proposition NOT proven here is the model SEMANTICALLY continuing the conversation — that needs valid model
/// auth; this run reaches the model call and a 401 (or any model-stage outcome) is fine, because the discriminator is
/// "did the binary FIND the session", observable before/independent of the model result.
///
/// <para><b>Gated</b>: runs only when <see cref="GateEnvVar"/> is set AND the claude binary resolves, so a default
/// <c>dotnet test</c> and CI (no binary, no opt-in) skip it cleanly. POSIX-only (real binary + <c>pwd -P</c> cwd
/// resolution; the encoded transcript dir keys on the RESOLVED path the process actually runs in).</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "RealCli")]
public sealed class RealClaudeResumeE2ETests
{
    /// <summary>Opt-in switch — set to any non-empty value to run the real-claude resume E2E (it spawns the real binary + reaches the network).</summary>
    public const string GateEnvVar = "CODESPACE_CLAUDE_RESUME_E2E";

    private static readonly ClaudeCodeHarness Harness = new();

    private readonly List<string> _tempDirs = new();

    [Fact]
    public async Task Restoring_the_prior_transcript_lets_the_real_claude_resume_the_session()
    {
        if (OperatingSystem.IsWindows()) return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(GateEnvVar))) return;   // opt-in only
        if (!ClaudeResolves()) return;

        try
        {
            var workspace = NewWorkspace();
            var cwd = await ResolveRealPathAsync(workspace);   // the path the process sees (macOS /var → /private/var)

            // ── 1) FRESH run via the production harness → capture the real session id from the binary's stream-json. ──
            var freshConfig = NewDir();
            var fresh = await RunClaudeAsync(Harness.BuildInvocation(FreshTask(cwd)), freshConfig);

            var sessionId = Harness.BuildResult(ParseAll(fresh.Stdout), fresh.ExitCode).SessionId;
            sessionId.ShouldNotBeNullOrEmpty("the production harness captured a session_id from the REAL claude stream-json");

            // ── The encoder matches the binary's ACTUAL on-disk projects dir (ground truth, not a fixture). ──
            var binaryDir = Directory.GetDirectories(Path.Combine(freshConfig, "projects")).Select(Path.GetFileName).Single();
            ClaudeTranscriptPath.EncodeCwd(cwd).ShouldBe(binaryDir, "EncodeCwd reproduces the exact dir the real claude binary created for this cwd");

            var transcript = await File.ReadAllTextAsync(Path.Combine(freshConfig, "projects", binaryDir!, $"{sessionId}.jsonl"));
            transcript.ShouldNotBeNullOrEmpty("the binary wrote the session transcript we will restore");

            // ── 2) CONTROL: --resume with NO restored transcript → the real binary REJECTS the resume ("No conversation
            //    found" — surfaced as an error_during_execution result line in stream-json, num_turns:0). ──
            var control = await RunClaudeAsync(Harness.BuildInvocation(ContinueTask(cwd, sessionId, restoredTranscript: null)), NewDir());

            (control.Stdout + control.Stderr).ShouldContain("No conversation found", Case.Insensitive,
                "without the restored transcript the real binary cannot find the session — this is the negative control the restore must flip");

            // ── 3) CONTINUE: restore the transcript via the production harness ConfigHomeFiles → --resume is ACCEPTED:
            //    the binary loads the session and runs a turn (reaching the model — a 401 here is fine; the FIND is what
            //    P3 proves), so it NEVER reports "No conversation found". ──
            var resumed = await RunClaudeAsync(Harness.BuildInvocation(ContinueTask(cwd, sessionId, restoredTranscript: transcript)), NewDir());

            (resumed.Stdout + resumed.Stderr).ShouldNotContain("No conversation found", Case.Insensitive,
                "with the transcript restored at projects/<encoded-cwd>/<id>.jsonl, the binary FOUND the session and ran a turn");
            ParseAll(resumed.Stdout).ShouldNotBeEmpty("the accepted resume produced a real stream-json result the production harness parsed");
        }
        finally
        {
            foreach (var dir in _tempDirs)
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of the gated run's temp dirs */ }
        }
    }

    [Fact]
    public async Task A_session_id_is_recoverable_from_the_real_binarys_early_init_line_even_when_killed_before_completion()
    {
        // P2.1: AgentRunExecutor's TimedOut/Stalled paths force-kill the process before it ever reaches its clean
        // "result" line — proving the production harness can still recover a resumable session id from whatever
        // the REAL binary emitted before the kill (its "system"/"init" line) is what makes a forced-terminal
        // agent's later retry WARM instead of always cold. Ground truth, not a synthetic JSON fixture.
        if (OperatingSystem.IsWindows()) return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(GateEnvVar))) return;   // opt-in only
        if (!ClaudeResolves()) return;

        try
        {
            var workspace = NewWorkspace();
            var cwd = await ResolveRealPathAsync(workspace);
            var configDir = NewDir();

            var earlyLines = await RunClaudeUntilInitThenKillAsync(Harness.BuildInvocation(FreshTask(cwd)), configDir);

            earlyLines.ShouldNotBeEmpty("the real binary emitted at least its system/init line before being killed");

            var events = earlyLines.SelectMany(Harness.ParseEvents).ToList();
            events.ShouldContain(e => e.Kind == AgentEventKind.Started, "the init line surfaces as a minimal lifecycle event instead of being silently dropped");

            AgentSessionIdReader.TryRead(events).ShouldNotBeNullOrEmpty(
                "a run killed before completion still yields a resumable session id from its early init line");
        }
        finally
        {
            foreach (var dir in _tempDirs)
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of the gated run's temp dirs */ }
        }
    }

    // ─── Tasks (production harness inputs) ────────────────────────────────────────

    private static AgentTask FreshTask(string cwd) => new()
    {
        Goal = "Reply with the single word: hello",
        Harness = ClaudeCodeHarness.HarnessKind,
        Model = null,
        WorkspaceDirectory = cwd,
        Permissions = new AgentPermissions { WriteScope = AgentWriteScope.ReadOnly },   // plan mode — no writes, no side effects
        TimeoutSeconds = 120,
    };

    private static AgentTask ContinueTask(string cwd, string sessionId, string? restoredTranscript) => FreshTask(cwd) with
    {
        Goal = "Reply with the single word: again",
        ResumeFromSessionId = sessionId,
        RestoredTranscript = restoredTranscript,
    };

    // ─── Real-process driver ──────────────────────────────────────────────────────

    /// <summary>Materialize the spec's config-home files (the restored transcript) via the PRODUCTION runner method, then spawn the real claude with an isolated CLAUDE_CONFIG_DIR + the workspace cwd, and capture stdout/stderr/exit.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunClaudeAsync(SandboxSpec spec, string configDir)
    {
        LocalProcessRunner.WriteConfigHomeFiles(spec.ConfigHomeFiles, configDir);

        var psi = new ProcessStartInfo
        {
            FileName = spec.Command,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        foreach (var arg in spec.Args) psi.ArgumentList.Add(arg);
        psi.Environment[ClaudeCodeHarness.ConfigDirEnvVar] = configDir;
        foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        using var proc = Process.Start(psi)!;
        proc.StandardInput.Close();   // claude --print waits up to 3s for stdin otherwise; close it (the prompt is a positional arg)

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ } throw new TimeoutException("the real claude run exceeded 90s"); }

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// Spawn the real claude, read stdout LINE BY LINE, and KILL the process the instant its "system"/"init" line
    /// arrives — simulating exactly what AgentRunExecutor's TimedOut/Stalled paths do: force-terminate before the
    /// run ever reaches its clean "result" line. Returns every line read (including the init line). A 60s safety
    /// deadline guards against a binary that never emits init at all (falls through to the kill regardless).
    /// </summary>
    private static async Task<IReadOnlyList<string>> RunClaudeUntilInitThenKillAsync(SandboxSpec spec, string configDir)
    {
        LocalProcessRunner.WriteConfigHomeFiles(spec.ConfigHomeFiles, configDir);

        var psi = new ProcessStartInfo
        {
            FileName = spec.Command,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        foreach (var arg in spec.Args) psi.ArgumentList.Add(arg);
        psi.Environment[ClaudeCodeHarness.ConfigDirEnvVar] = configDir;
        foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        using var proc = Process.Start(psi)!;
        proc.StandardInput.Close();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var lines = new List<string>();

        try
        {
            while (true)
            {
                var line = await proc.StandardOutput.ReadLineAsync(cts.Token);
                if (line is null) break;   // the process closed stdout (exited) before init ever arrived

                lines.Add(line);

                if (line.Contains("\"subtype\":\"init\"", StringComparison.Ordinal)) break;
            }
        }
        catch (OperationCanceledException) { /* the 60s safety deadline fired — fall through to the kill below regardless */ }

        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }

        return lines;
    }

    private static IReadOnlyList<AgentEvent> ParseAll(string streamJson) =>
        streamJson.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(Harness.ParseEvents)
            .ToList();

    // ─── Workspace + binary helpers ───────────────────────────────────────────────

    private static bool ClaudeResolves()
    {
        var cmd = Environment.GetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar);
        if (!string.IsNullOrEmpty(cmd)) return File.Exists(cmd);

        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')
            .Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, "claude")));
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs-claude-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);   // tracked so the finally cleans every dir this gated run created
        return dir;
    }

    private string NewWorkspace()
    {
        var ws = Path.Combine(NewDir(), "ws");
        Directory.CreateDirectory(ws);
        RunAsync("git", new[] { "init", "-q" }, ws).GetAwaiter().GetResult();
        return ws;
    }

    /// <summary>The canonical real path the process sees (resolves macOS /var → /private/var) — the cwd claude keys its transcript dir on.</summary>
    private static async Task<string> ResolveRealPathAsync(string dir)
    {
        var (_, stdout, _) = await RunAsync("/bin/sh", new[] { "-c", "cd \"$0\" && pwd -P", dir }, dir);
        return stdout.Trim();
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string file, string[] args, string cwd)
    {
        var psi = new ProcessStartInfo { FileName = file, WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var o = proc.StandardOutput.ReadToEndAsync();
        var e = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, await o, await e);
    }
}
