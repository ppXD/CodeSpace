using System.Diagnostics;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.E2ETests.Agents;

/// <summary>
/// THE live proof that the P3 continue chain SEMANTICALLY works: a REAL <c>claude</c> agent, authenticated by the
/// seeded gateway credential, is told a unique codeword in a FRESH run, then a SECOND run RESTORES that conversation
/// (the captured session id + transcript, placed by the production harness at the resume cwd's encoded path) and asks
/// the model to recall it — and the live model answers with the codeword. That can only happen if the whole chain held
/// end to end against the real model: capture the session id (3.1a) → encode cwd + restore transcript (3.3a) →
/// <c>--resume</c> loads it (3.2) → the model CONTINUES the prior conversation. The always-on
/// <c>RealClaudeResumeE2ETests</c> proves the MECHANICAL find against the real binary (no model); THIS is the only tier
/// that proves the live model actually USES the restored context.
///
/// <para>INFORMATIONAL (report-only) on capability: the recall verdict is REPORTED
/// (codeword present → <see cref="RealModelOutcome.Drove"/>; a clean run without it → <see cref="RealModelOutcome.CapabilityMiss"/>)
/// and gates ONLY a <see cref="RealModelOutcome.CodeFault"/>; an incomplete run is <see cref="AgentExecutionInfraException"/>
/// (gateway/exec infra, a non-gating LOUD skip). A live model recalling a codeword from a restored transcript is a basic
/// capability, so this can be promoted to the strict gating tier once a few green runs confirm it. Self-skips (skip ≠
/// pass, surfaced loudly) when <c>CODESPACE_LLM_*</c> are absent or the <c>claude</c> CLI is not installed; FAILS on a
/// partial secret config. POSIX-only. <c>[Category=RealModel]</c>, class token <c>RealModelSession</c> → runs only on
/// the real-model lane.</para>
///
/// <para>A gateway FORMAT fault (the owner's Anthropic-compat layer mangling thinking-block continuation) buys ONE
/// repair before that skip: a COLD RE-STAGE of the whole fixture, bounded by <c>RealModelFormatFaultRepair</c>.
/// Deliberately NOT production's <c>ApplyFormatFaultMitigation</c> — it drops the restored conversation this arm
/// exists to measure, so a mitigated retry would report a different experiment as this arm's verdict.</para>
/// </summary>
[Trait("Category", "RealModel")]
[Trait("Surface", "RealCli")]
public sealed class RealModelSessionResumeE2ETests
{
    private const string Provider = "Anthropic";
    private static readonly ClaudeCodeHarness Harness = new();

    private readonly List<string> _tempDirs = new();

    [SkippableFact]
    public async Task A_real_claude_agent_recalls_a_codeword_from_the_restored_conversation()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        // A set-but-blank secret (an undefined ${{ secrets.X }} expands to "") counts as ABSENT — skip ≠ pass.
        var present = new[] { baseUrl, apiKey, model }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none.");

        if (OperatingSystem.IsWindows()) return;
        if (!await ClaudeReadyAsync()) throw RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — the resume gate needs the harness binary (skip ≠ pass)");

        try
        {
            // INFORMATIONAL: gates ONLY a CodeFault; a CapabilityMiss (model ran but didn't recall) is reported, an
            // incomplete run is non-gating infra. A fresh codeword + config per attempt — a stale transcript can't satisfy a retry.
            //
            // A gateway FORMAT fault buys ONE COLD RE-STAGE (RealModelFormatFaultRepair owns the bound). NOT the
            // production mitigation: this arm's SUBJECT is the warm resume, and ApplyFormatFaultMitigation drops the
            // very transcript under test (ResumeFromSessionId + RestoredTranscript = null), so a mitigated retry would
            // measure something else and report it as this arm's verdict. The whole drive below already stages itself
            // COLD per call — fresh codeword, fresh workspace, fresh config dirs, fresh session — so re-driving it is
            // a faithful second measurement of the SAME configuration, and it never re-sends the mangled block
            // (the new conversation has its own transcript). This arm's gate does not retry infra on its own, so the
            // re-stage is the only attempt the format fault gets.
            await RealModelGate.AssessLiveAsync(Provider, () => RealModelFormatFaultRepair.WithColdRestageAsync(() => DriveColdStagedResumeAsync(baseUrl!, apiKey!, model!)));
        }
        finally
        {
            foreach (var dir in _tempDirs)
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// One COLD-staged measurement of the continue chain: a fresh codeword told to a fresh conversation, then that
    /// conversation's captured session id + transcript restored into a second run that is asked to recall it. Every
    /// resource is minted per call (codeword, workspace, both config dirs), which is what makes this safe to re-drive
    /// as the format-fault repair — a re-stage measures the same configuration, never a stale transcript.
    ///
    /// <para>Both infra exits carry the harness's own folded error text. That text is the ONLY carrier of the
    /// gateway's <c>Content block is not a thinking block</c> message, and <c>RealModelFormatFaultRepair</c> reads
    /// production's marker vocabulary off the thrown exception to decide whether a repair is owed — a status-only
    /// message classified as unrepairable generic infra and cost the whole measurement.</para>
    /// </summary>
    private async Task<(RealModelOutcome Outcome, string Note)> DriveColdStagedResumeAsync(string baseUrl, string apiKey, string model)
    {
        var codeword = "CODESPACE-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var env = Harness.ProjectToEnv(new ResolvedModelCredential { Provider = Provider, ApiKey = apiKey, BaseUrl = baseUrl });
        var cwd = await ResolveRealPathAsync(NewWorkspace());

        // ── FRESH run: tell the model a unique codeword + capture the real session id + the session transcript. ──
        var freshConfig = NewDir();
        var fresh = await RunClaudeAsync(Harness.BuildInvocation(Task(cwd, model, env, $"Remember this codeword, I will ask you to recall it: {codeword}. Reply with only: ok.")), freshConfig);
        var freshResult = Harness.BuildResult(ParseAll(fresh.Stdout), fresh.ExitCode, "");

        if (freshResult.Status != AgentRunStatus.Succeeded || string.IsNullOrEmpty(freshResult.SessionId))
            throw new AgentExecutionInfraException($"the fresh claude run did not complete (status={freshResult.Status}, session={freshResult.SessionId ?? "null"}, error={freshResult.Error ?? "none"}) — gateway/exec infra, not a recall verdict");

        var sessionId = freshResult.SessionId!;
        var binaryDir = Directory.GetDirectories(Path.Combine(freshConfig, "projects")).Select(Path.GetFileName).Single();
        var transcript = await File.ReadAllTextAsync(Path.Combine(freshConfig, "projects", binaryDir!, $"{sessionId}.jsonl"));

        // ── CONTINUE run: restore that transcript via the production harness + --resume, then ask for the codeword. ──
        var continueTask = Task(cwd, model, env, "What was the codeword I told you to remember? Reply with ONLY the codeword, nothing else.")
            with { ResumeFromSessionId = sessionId, RestoredTranscript = transcript };
        var resumed = await RunClaudeAsync(Harness.BuildInvocation(continueTask), NewDir());
        var resumedResult = Harness.BuildResult(ParseAll(resumed.Stdout), resumed.ExitCode, "");

        if (resumedResult.Status != AgentRunStatus.Succeeded)
            throw new AgentExecutionInfraException($"the resumed claude run did not complete (status={resumedResult.Status}, error={resumedResult.Error ?? "none"}) — gateway/exec infra, not a recall verdict");

        // The model recalled the codeword ⇒ it genuinely CONTINUED the restored conversation (the chain held live).
        // Check ONLY the model's OWN reply events (assistant/completed), never the raw stream — verified against the
        // real binary that `--resume` does NOT echo the loaded history to stdout, so a match can't be a false positive;
        // restricting to reply events keeps that guarantee even if a future CLI version changed the stream shape.
        var modelReply = string.Join("\n", ParseAll(resumed.Stdout)
            .Where(e => e.Kind is AgentEventKind.AssistantMessage or AgentEventKind.Completed or AgentEventKind.FinalSummary)
            .Select(e => e.Text));
        var recalled = modelReply.Contains(codeword, StringComparison.OrdinalIgnoreCase);

        return (recalled ? RealModelOutcome.Drove : RealModelOutcome.CapabilityMiss,
            $"{Provider} '{model}': the resumed agent {(recalled ? "RECALLED" : "did NOT recall")} the codeword {codeword} from the restored conversation — the P3 continue chain {(recalled ? "held end-to-end against the live model" : "did not surface the prior context")}");
    }

    /// <summary>
    /// 3c — the CROSS-HOST arm: the conversation is taken from a MID-RUN checkpoint, the way a run whose host dies
    /// leaves one behind, and a second agent continues from exactly those bytes.
    ///
    /// <para>What makes it a different experiment from the arm above, and why both are needed: that one reads the
    /// session file AFTER the process exits, from the host that ran it — which is precisely what a dead host cannot
    /// offer. This one takes the transcript while the agent is STILL RUNNING, through the same two production calls
    /// the executor's observer tick makes (<c>IAgentSessionTranscript.SessionTranscriptRelativePath</c> to locate it
    /// and <c>AgentRunExecutor.ResolveSessionTranscriptPath</c> to clamp it inside the config home), then KILLS the
    /// agent before it can finish — the host loss — and asks a fresh agent, in a fresh config home, to recall the
    /// codeword from those mid-run bytes alone.</para>
    ///
    /// <para>A mid-run snapshot that does not yet contain the codeword turn is INFRA, not a capability miss: the
    /// experiment could not be staged, so there is nothing to measure. Same reporting contract as the arm above —
    /// informational, gating only a <see cref="RealModelOutcome.CodeFault"/>.</para>
    ///
    /// <para><b>EXACTLY what this arm proves, and what it does not.</b> It proves ONE thing no other tier can: that a
    /// transcript located mid-run through the production locate + clamp, cut at a line boundary, is bytes a real
    /// <c>claude</c> actually resumes from — that the live model USES the pre-loss context. That is a statement about
    /// the CLI and the model, not about this codebase's plumbing.</para>
    ///
    /// <para>It therefore CANNOT go red for anything else 3c wrote, and must not be read as coverage for it. It drives
    /// <c>Process.Start</c> directly, so it never touches the executor's tick, the checkpointer, the artifact store,
    /// the fenced row stamp, the reconciler's abandon or the agent node's respawn — all of which are pinned, with
    /// their own mutations, in <c>AgentSessionTranscriptCheckpointerTests</c>,
    /// <c>AgentRunSessionCheckpointFlowTests</c>, <c>AgentCodeNodeTests</c> and <c>AgentNodeFlowTests</c>. It also
    /// cannot FAIL the lane on a recall miss (the verdict is informational by the same ruling as its sibling). What
    /// it assumes: that the harness's locate and the executor's clamp are the ones this file calls — which they are,
    /// at the call sites in <see cref="TryReadLiveTranscript"/>, and which is the only reason a layout change in
    /// either would surface here at all.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_real_claude_agent_recalls_a_codeword_from_a_mid_run_checkpoint()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none.");

        if (OperatingSystem.IsWindows()) return;
        if (!await ClaudeReadyAsync()) throw RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — the checkpoint-resume gate needs the harness binary (skip ≠ pass)");

        try
        {
            await RealModelGate.AssessLiveAsync(Provider, () => RealModelFormatFaultRepair.WithColdRestageAsync(() => DriveCheckpointSourcedResumeAsync(baseUrl!, apiKey!, model!)));
        }
        finally
        {
            foreach (var dir in _tempDirs)
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// One COLD-staged measurement of the cross-host chain: checkpoint a LIVE agent's transcript, kill it, continue
    /// from the checkpoint. Every resource is minted per call, so a re-stage measures the same configuration.
    /// </summary>
    private async Task<(RealModelOutcome Outcome, string Note)> DriveCheckpointSourcedResumeAsync(string baseUrl, string apiKey, string model)
    {
        var codeword = "CODESPACE-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var env = Harness.ProjectToEnv(new ResolvedModelCredential { Provider = Provider, ApiKey = apiKey, BaseUrl = baseUrl });
        var cwd = await ResolveRealPathAsync(NewWorkspace());
        var liveConfig = NewDir();

        // A LONG second instruction so the agent is still working when the checkpoint is taken and the kill lands —
        // the host loss must interrupt a live conversation, not race a process that already exited.
        var goal = $"Remember this codeword, I will ask you to recall it: {codeword}. Reply 'ok', then count slowly from 1 to 40, one number per message.";
        var live = await RunClaudeUntilCheckpointedAsync(Harness.BuildInvocation(Task(cwd, model, env, goal)), liveConfig, cwd, codeword);

        if (live.Checkpoint is not { Length: > 0 } checkpoint)
            throw new AgentExecutionInfraException($"no mid-run checkpoint containing the codeword could be taken before the live claude run ended (sessionId={live.SessionId ?? "null"}, snapshots={live.Snapshots}, error={live.Error ?? "none"}) — gateway/exec infra, not a recall verdict");

        // ── CONTINUE on a FRESH config home: the dead host's spool is gone, so only the checkpoint's bytes travel. ──
        var continueTask = Task(cwd, model, env, "What was the codeword I told you to remember? Reply with ONLY the codeword, nothing else.")
            with { ResumeFromSessionId = live.SessionId, RestoredTranscript = checkpoint };
        var resumed = await RunClaudeAsync(Harness.BuildInvocation(continueTask), NewDir());
        var resumedResult = Harness.BuildResult(ParseAll(resumed.Stdout), resumed.ExitCode, "");

        if (resumedResult.Status != AgentRunStatus.Succeeded)
            throw new AgentExecutionInfraException($"the checkpoint-resumed claude run did not complete (status={resumedResult.Status}, error={resumedResult.Error ?? "none"}) — gateway/exec infra, not a recall verdict");

        var modelReply = string.Join("\n", ParseAll(resumed.Stdout)
            .Where(e => e.Kind is AgentEventKind.AssistantMessage or AgentEventKind.Completed or AgentEventKind.FinalSummary)
            .Select(e => e.Text));
        var recalled = modelReply.Contains(codeword, StringComparison.OrdinalIgnoreCase);

        return (recalled ? RealModelOutcome.Drove : RealModelOutcome.CapabilityMiss,
            $"{Provider} '{model}': after a {checkpoint.Length}-byte MID-RUN checkpoint and a kill, the continued agent {(recalled ? "RECALLED" : "did NOT recall")} the codeword {codeword} — cross-host recovery {(recalled ? "held end-to-end against the live model" : "did not surface the pre-loss context")}");
    }

    /// <summary>
    /// Run the live agent, checkpoint its session transcript WHILE it runs, and kill it — the host loss, staged.
    ///
    /// <para>The locate is the production pair and nothing else: the harness's own
    /// <c>SessionTranscriptRelativePath</c> (the CLI's cwd-encoded layout, which this test must never restate) and
    /// <c>AgentRunExecutor.ResolveSessionTranscriptPath</c> (the symlink/traversal clamp, which applies to a LIVE read
    /// exactly as it does to a post-exit one). The session id comes off the live stream through the harness's own
    /// parse, as the executor's fold gets it.</para>
    ///
    /// <para>Only a snapshot ending in a newline is kept: the CLI appends whole JSON lines, so a partial tail means
    /// the file was caught mid-write and restoring it would hand the next CLI a corrupt session.</para>
    /// </summary>
    private static async Task<(string? SessionId, string? Checkpoint, int Snapshots, string? Error)> RunClaudeUntilCheckpointedAsync(SandboxSpec spec, string configDir, string cwd, string codeword)
    {
        LocalProcessRunner.WriteConfigHomeFiles(spec.ConfigHomeFiles, configDir);

        var psi = new ProcessStartInfo { FileName = spec.Command, WorkingDirectory = spec.WorkingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var arg in spec.Args) psi.ArgumentList.Add(arg);
        psi.Environment[ClaudeCodeHarness.ConfigDirEnvVar] = configDir;
        foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        using var proc = Process.Start(psi)!;
        proc.StandardInput.Close();

        string? sessionId = null;
        string? checkpoint = null;
        var snapshots = 0;
        var deadline = DateTime.UtcNow.AddSeconds(180);

        while (!proc.HasExited && DateTime.UtcNow < deadline)
        {
            if (await proc.StandardOutput.ReadLineAsync() is not { } line) break;

            sessionId ??= AgentSessionIdReader.TryRead(Harness.ParseEvents(line).ToList());

            if (sessionId is null) continue;

            if (TryReadLiveTranscript(configDir, cwd, sessionId) is not { } snapshot) continue;

            snapshots++;

            // The experiment needs the codeword turn to be INSIDE the checkpoint — that is the fact the continued
            // agent is asked to recall. Anything earlier is a snapshot of a conversation that never heard it.
            if (!snapshot.Contains(codeword, StringComparison.Ordinal)) continue;

            checkpoint = snapshot;
            break;
        }

        // The host loss: the process is killed where it stands, so nothing it would have written after the checkpoint
        // — including its post-exit session file — can reach the continuation.
        try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token); } catch { /* best-effort */ }

        return (sessionId, checkpoint, snapshots, checkpoint is null ? await proc.StandardError.ReadToEndAsync() : null);
    }

    /// <summary>The live transcript through the PRODUCTION locate + clamp, or null when it is not addressable yet or was caught mid-write. Never a hand-built path: the cwd encoding is the harness's to own.</summary>
    private static string? TryReadLiveTranscript(string configDir, string cwd, string sessionId)
    {
        if (((IAgentSessionTranscript)Harness).SessionTranscriptRelativePath(configDir, cwd, sessionId) is not { } relative) return null;

        if (AgentRunExecutor.ResolveSessionTranscriptPath(configDir, relative) is not { } path || !File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            return text.EndsWith('\n') ? text : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static AgentTask Task(string cwd, string model, IReadOnlyDictionary<string, string> env, string goal) => new()
    {
        Goal = goal,
        Harness = ClaudeCodeHarness.HarnessKind,
        Model = model,
        WorkspaceDirectory = cwd,
        Autonomy = AgentAutonomyLevel.Trusted,
        Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
        Environment = env,
        TimeoutSeconds = 180,
    };

    // ─── Real-process driver (controls the config dir + cwd so the continue can read the session file) ────────────

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
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ } throw new AgentExecutionInfraException("the live claude run exceeded its deadline — gateway/exec infra"); }

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    private static IReadOnlyList<AgentEvent> ParseAll(string streamJson) =>
        streamJson.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(Harness.ParseEvents)
            .ToList();

    private static async Task<bool> ClaudeReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs-realmodel-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string NewWorkspace()
    {
        var ws = Path.Combine(NewDir(), "ws");
        Directory.CreateDirectory(ws);
        RunGitInitAsync(ws).GetAwaiter().GetResult();
        return ws;
    }

    private static async Task<string> ResolveRealPathAsync(string dir)
    {
        var psi = new ProcessStartInfo { FileName = "/bin/sh", WorkingDirectory = dir, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("pwd -P");
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return stdout.Trim();
    }

    private static async Task RunGitInitAsync(string cwd)
    {
        var psi = new ProcessStartInfo { FileName = "git", WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("init");
        psi.ArgumentList.Add("-q");
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
    }
}
