using System.Diagnostics;
using System.Text;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.E2ETests.Agents;

/// <summary>
/// 🔵 Human-gated real-CLI E2E (Rule 12, fidelity = highest available). Drives the ACTUAL <c>codex</c> binary through
/// the PRODUCTION <see cref="CodexHarness"/> to prove the Codex half of the P3 continue chain works against the real
/// CLI's on-disk contract — the counterpart to <see cref="RealClaudeResumeE2ETests"/>, which so far only had a
/// fake-CLI argv proof for Codex:
/// <list type="number">
/// <item>3.1a — the production harness captures a real <c>thread_id</c> from the binary's real <c>exec --json</c> stream
/// (<c>thread.started</c>), emitted at session start BEFORE any model call,</item>
/// <item>3.2 — the production resume argv <c>exec resume &lt;id&gt; --json -c sandbox_mode=&lt;mode&gt;</c> is HONORED by the
/// real binary (no clap rejection — the <c>--sandbox</c>-is-exec-only fix from #852, now a committed real-binary guard),</item>
/// <item>restore mechanism — laying ONLY the prior rollout <c>.jsonl</c> back into a fresh <c>CODEX_HOME</c> at its
/// <c>sessions/…</c> relative path lets <c>codex exec resume</c> FIND the thread, while the same resume against a fresh
/// EMPTY <c>CODEX_HOME</c> is REJECTED (<c>no rollout found for thread</c>). This is exactly what a production Codex
/// continue producer will restore.</item>
/// </list>
/// The discriminator is model-outcome-independent: both the capture (<c>thread.started</c>) and the resume resolution
/// (<c>no rollout found</c> vs a matching <c>thread.started</c>) happen BEFORE the model call, so no valid model auth is
/// needed and the process is killed the instant the signal lands — hermetic and fast, no network round-trip.
///
/// <para><b>Not proven here</b>: the production CONTINUE PRODUCER for Codex — capturing the rollout (its path is
/// date-nested <c>sessions/YYYY/MM/DD/rollout-&lt;ts&gt;-&lt;id&gt;.jsonl</c>, so capture must glob CODEX_HOME by thread id,
/// not compute a cwd-encoded path as Claude does) and restoring it into the fresh per-run CODEX_HOME on a re-stage — is
/// a follow-on. This test de-risks it by proving the binary's resume contract + that a rollout-file restore is
/// sufficient (no hidden index).</para>
///
/// <para><b>Gated</b>: runs only when <see cref="GateEnvVar"/> is set AND the codex binary resolves, so a default
/// <c>dotnet test</c> and CI skip it cleanly. POSIX-only (real binary).</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "RealCli")]
public sealed class RealCodexResumeE2ETests
{
    /// <summary>Opt-in switch — set to any non-empty value to run the real-codex resume E2E (it spawns the real binary).</summary>
    public const string GateEnvVar = "CODESPACE_CODEX_RESUME_E2E";

    private static readonly CodexHarness Harness = new();

    private readonly List<string> _tempDirs = new();

    [Fact]
    public async Task Restoring_the_prior_rollout_lets_the_real_codex_resume_the_thread()
    {
        if (OperatingSystem.IsWindows()) return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(GateEnvVar))) return;   // opt-in only
        if (!CodexResolves()) return;

        try
        {
            var cwd = NewWorkspace();

            // ── 1) FRESH run via the production harness → capture the real thread id + the rollout the binary wrote.
            //    awaitRollout: don't kill the instant thread.started prints — wait for the rollout to be flushed to disk
            //    first, else the resume in step 3 restores a truncated/empty file and can't re-open the thread. ──
            var freshHome = NewDir();
            var fresh = await RunCodexUntilAsync(Harness.BuildInvocation(FreshTask(cwd)), freshHome, line => line.Contains("thread.started"), awaitRolloutInHome: freshHome);

            var threadId = Harness.BuildResult(ParseAll(fresh.Stdout), exitCode: 0).SessionId;   // only SessionId is read; the kill makes the real exit code irrelevant
            threadId.ShouldNotBeNullOrEmpty("the production harness captured a thread_id from the REAL codex exec --json stream");

            // The binary wrote a rollout under sessions/ — the on-disk artefact a restore must reproduce, keyed by thread id.
            var rollout = Directory.GetFiles(Path.Combine(freshHome, "sessions"), "*.jsonl", SearchOption.AllDirectories).Single();
            new FileInfo(rollout).Length.ShouldBeGreaterThan(0, "the fresh run flushed a NON-EMPTY rollout before the kill — a truncated file would make the restore's resume fail confusingly with 'no rollout found'");
            var rolloutRel = Path.GetRelativePath(freshHome, rollout);
            rolloutRel.ShouldContain(threadId!, Case.Sensitive, "the rollout filename carries the thread id — the on-disk contract a restore reproduces");

            // ── 2) CONTROL: resume against a FRESH EMPTY CODEX_HOME → the real binary REJECTS ("no rollout found"). ──
            var control = await RunCodexUntilAsync(Harness.BuildInvocation(ContinueTask(cwd, threadId!)), NewDir(), line => line.Contains("no rollout"));

            (control.Stdout + control.Stderr).ShouldContain("no rollout found for thread", Case.Insensitive,
                "without the restored rollout the real binary cannot resolve the thread — the negative control the restore must flip");

            // ── 3) RESTORE + CONTINUE: copy ONLY the rollout into a fresh CODEX_HOME at its sessions/ relative path →
            //    the resume is ACCEPTED (thread.started with the SAME id; never "no rollout found"). ──
            var restoredHome = NewDir();
            var dest = Path.Combine(restoredHome, rolloutRel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(rollout, dest);

            var resumed = await RunCodexUntilAsync(Harness.BuildInvocation(ContinueTask(cwd, threadId!)), restoredHome, line => line.Contains("thread.started"));

            (resumed.Stdout + resumed.Stderr).ShouldNotContain("no rollout found", Case.Insensitive,
                "with the rollout restored under sessions/, the binary FOUND the thread — the restore flipped the control");
            resumed.Stdout.ShouldContain("thread.started", Case.Insensitive, "the accepted resume re-opened the thread");
            resumed.Stdout.ShouldContain(threadId!, Case.Sensitive, "the resumed thread is the SAME thread the fresh run created — not a new one");
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
        Harness = CodexHarness.HarnessKind,
        Model = null,
        WorkspaceDirectory = cwd,
        Permissions = new AgentPermissions { WriteScope = AgentWriteScope.ReadOnly },   // read-only → --sandbox read-only
        TimeoutSeconds = 120,
    };

    private static AgentTask ContinueTask(string cwd, string threadId) => FreshTask(cwd) with
    {
        Goal = "Reply with the single word: again",
        ResumeFromSessionId = threadId,   // → exec resume <id> --json -c sandbox_mode=read-only …
    };

    // ─── Real-process driver ──────────────────────────────────────────────────────

    /// <summary>
    /// Spawn the real codex with an isolated CODEX_HOME + the production spec argv, reading stdout line by line until
    /// <paramref name="stop"/> matches (then kill — the signal lands before the model call, so we never wait on a
    /// network round-trip) or the process exits on its own (the fast "no rollout found" control path). A backstop
    /// timeout kills a hung run. Returns the captured stdout + stderr.
    /// </summary>
    private static async Task<(string Stdout, string Stderr)> RunCodexUntilAsync(SandboxSpec spec, string codexHome, Func<string, bool> stop, string? awaitRolloutInHome = null)
    {
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
        psi.Environment[CodexHarness.ConfigHomeEnvVar] = codexHome;                 // per-run isolated ~/.codex
        psi.Environment[CodexHarness.ApiKeyEnvVar] = "dummy-key-codex-resume-e2e";  // reach session-start; we kill before the model matters
        foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        using var proc = Process.Start(psi)!;
        proc.StandardInput.Close();   // `codex exec` reads stdin ("Reading additional input from stdin…") — close it; the prompt is a positional arg

        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdout = new StringBuilder();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));   // backstop against a hung run
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false)) != null)
            {
                stdout.AppendLine(line);
                if (stop(line)) break;
            }
        }
        catch (OperationCanceledException) { /* backstop hit — fall through to kill + drain */ }

        // The fresh-capture caller passes its CODEX_HOME here: codex writes the rollout ASYNCHRONOUSLY, so killing the
        // instant thread.started prints can leave a truncated/empty rollout the later resume can't re-open. Poll until a
        // non-empty rollout lands before killing, so the on-disk artefact we restore is complete. This uses its OWN
        // dedicated budget (NOT the run-wide backstop) so a slow thread.started can't starve the flush wait.
        if (awaitRolloutInHome is not null) await WaitForNonEmptyRolloutAsync(awaitRolloutInHome).ConfigureAwait(false);

        if (!proc.HasExited) try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }

        try { stdout.Append(await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false)); } catch { /* stream closed by the kill */ }
        return (stdout.ToString(), await stderrTask.ConfigureAwait(false));
    }

    /// <summary>Poll until codex has flushed a non-empty rollout under <c>&lt;home&gt;/sessions</c>, so a kill can't leave a truncated file. Uses its OWN dedicated timeout (independent of the run-wide read backstop) so it always gets its full budget.</summary>
    private static async Task WaitForNonEmptyRolloutAsync(string codexHome)
    {
        var sessions = Path.Combine(codexHome, "sessions");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (Directory.Exists(sessions) && Directory.EnumerateFiles(sessions, "*.jsonl", SearchOption.AllDirectories).Any(f => new FileInfo(f).Length > 0))
                    return;

                await Task.Delay(100, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* flush budget hit — proceed to kill; the caller's Length>0 assertion then fails loudly */ }
    }

    private static IReadOnlyList<AgentEvent> ParseAll(string streamJson) =>
        streamJson.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(Harness.ParseEvents)
            .ToList();

    // ─── Workspace + binary helpers ───────────────────────────────────────────────

    private static bool CodexResolves()
    {
        var cmd = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar);
        if (!string.IsNullOrEmpty(cmd)) return File.Exists(cmd);

        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')
            .Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, "codex")));
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs-codex-e2e-" + Guid.NewGuid().ToString("N"));
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

    private static async Task<int> RunAsync(string file, string[] args, string cwd)
    {
        var psi = new ProcessStartInfo { FileName = file, WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return proc.ExitCode;
    }
}
