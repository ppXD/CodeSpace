using System.Diagnostics;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
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
/// </summary>
[Trait("Category", "RealModel")]
[Trait("Surface", "RealCli")]
public sealed class RealModelSessionResumeE2ETests
{
    private const string Provider = "Anthropic";
    private static readonly ClaudeCodeHarness Harness = new();

    private readonly List<string> _tempDirs = new();

    [Fact]
    public async Task A_real_claude_agent_recalls_a_codeword_from_the_restored_conversation()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        // A set-but-blank secret (an undefined ${{ secrets.X }} expands to "") counts as ABSENT — skip ≠ pass.
        var present = new[] { baseUrl, apiKey, model }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)"); return; }
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three (base url / api key / model id) or none.");

        if (OperatingSystem.IsWindows()) return;
        if (!await ClaudeReadyAsync()) { RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — the resume gate needs the harness binary (skip ≠ pass)"); return; }

        try
        {
            // INFORMATIONAL: gates ONLY a CodeFault; a CapabilityMiss (model ran but didn't recall) is reported, an
            // incomplete run is non-gating infra. A fresh codeword + config per attempt — a stale transcript can't satisfy a retry.
            await RealModelGate.AssessLiveAsync(Provider, async () =>
            {
                var codeword = "CODESPACE-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
                var env = Harness.ProjectToEnv(new ResolvedModelCredential { Provider = Provider, ApiKey = apiKey, BaseUrl = baseUrl });
                var cwd = await ResolveRealPathAsync(NewWorkspace());

                // ── FRESH run: tell the model a unique codeword + capture the real session id + the session transcript. ──
                var freshConfig = NewDir();
                var fresh = await RunClaudeAsync(Harness.BuildInvocation(Task(cwd, model!, env, $"Remember this codeword, I will ask you to recall it: {codeword}. Reply with only: ok.")), freshConfig);
                var freshResult = Harness.BuildResult(ParseAll(fresh.Stdout), fresh.ExitCode);

                if (freshResult.Status != AgentRunStatus.Succeeded || string.IsNullOrEmpty(freshResult.SessionId))
                    throw new AgentExecutionInfraException($"the fresh claude run did not complete (status={freshResult.Status}, session={freshResult.SessionId ?? "null"}) — gateway/exec infra, not a recall verdict");

                var sessionId = freshResult.SessionId!;
                var binaryDir = Directory.GetDirectories(Path.Combine(freshConfig, "projects")).Select(Path.GetFileName).Single();
                var transcript = await File.ReadAllTextAsync(Path.Combine(freshConfig, "projects", binaryDir!, $"{sessionId}.jsonl"));

                // ── CONTINUE run: restore that transcript via the production harness + --resume, then ask for the codeword. ──
                var continueTask = Task(cwd, model!, env, "What was the codeword I told you to remember? Reply with ONLY the codeword, nothing else.")
                    with { ResumeFromSessionId = sessionId, RestoredTranscript = transcript };
                var resumed = await RunClaudeAsync(Harness.BuildInvocation(continueTask), NewDir());
                var resumedResult = Harness.BuildResult(ParseAll(resumed.Stdout), resumed.ExitCode);

                if (resumedResult.Status != AgentRunStatus.Succeeded)
                    throw new AgentExecutionInfraException($"the resumed claude run did not complete (status={resumedResult.Status}) — gateway/exec infra, not a recall verdict");

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
            });
        }
        finally
        {
            foreach (var dir in _tempDirs)
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
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
