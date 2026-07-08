using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using System.Text.Json;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Drives the REAL <see cref="IAgentRunExecutor"/> + the REAL <see cref="CodexHarness"/> /
/// <see cref="ClaudeCodeHarness"/> (resolved from the real DI registry) + the REAL
/// <c>LocalProcessRunner</c> against real Postgres — the production execution pipeline end-to-end:
/// spawn → stream stdout line-by-line → the harness's OWN <c>ParseEvent</c> on real process bytes →
/// persist each event → fold <c>BuildResult</c> → complete. The only substitution is the unavailable
/// codex/claude binary: PR-#295's <c>CommandEnvVar</c> redirects the harness's executable to a committed
/// fake-CLI script that emits a captured-shape fixture and exits with a chosen code. No binary, no auth,
/// no network — just <c>/bin/sh</c> + <c>cat</c> + real exit codes.
///
/// <para><b>Fidelity (Rule 12) — high tier:</b> real production harness + real OS process; the fixtures
/// are representative of the documented codex JSONL / claude stream-json shapes (NOT byte-captured from a
/// live CLI — that calibration refresh is a flagged follow-up). The pipeline, streaming, tolerant parsing,
/// timeout/exit mapping, and persistence are all real.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RealHarnessExecutionTests
{
    private readonly PostgresFixture _fixture;

    public RealHarnessExecutionTests(PostgresFixture fixture) { _fixture = fixture; }

    // Representative codex `exec --json` JSONL — one line per native event type the production
    // CodexHarness.ParseEvent classifies (reason→Reasoning, exec/command→CommandExecuted,
    // patch/apply→FileChanged, message/assistant→AssistantMessage, complete→Completed).
    private const string CodexFixture =
        """
        {"type":"agent_reasoning","message":"Analyzing the failing billing tests"}
        {"type":"exec_command","command":"npm test"}
        {"type":"patch_apply","path":"src/billing/Invoice.cs"}
        {"type":"agent_message","message":"Fixed the billing calculation."}
        {"type":"task_complete","message":"completed"}
        """;

    // Representative claude `--print --output-format stream-json` — system/init (surfaces as a minimal
    // Started event carrying the session id, P2.1), an assistant text turn, two tool_use blocks
    // (Bash→CommandExecuted, Edit→FileChanged), and the final result.
    private const string ClaudeFixture =
        """
        {"type":"system","subtype":"init","cwd":"/tmp/ws"}
        {"type":"assistant","message":{"content":[{"type":"text","text":"Looking into the billing tests."}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"npm test"}}]}}
        {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"src/billing/Invoice.cs"}}]}}
        {"type":"result","subtype":"success","result":"Fixed the billing tests.","is_error":false}
        """;

    // The codex happy stream PLUS the cumulative usage event real codex emits (`token_count.info.total_token_usage`) —
    // the run total the production CodexHarness.BuildResult folds into result_jsonb.
    private const string CodexUsageFixture =
        """
        {"type":"agent_reasoning","message":"Analyzing the failing billing tests"}
        {"type":"agent_message","message":"Fixed the billing calculation."}
        {"type":"token_count","info":{"total_token_usage":{"input_tokens":1450,"output_tokens":260}}}
        {"type":"task_complete","message":"completed"}
        """;

    // The claude happy stream whose final `result` line carries the run `usage` (the shape `--output-format stream-json` emits).
    private const string ClaudeUsageFixture =
        """
        {"type":"system","subtype":"init","cwd":"/tmp/ws"}
        {"type":"assistant","message":{"content":[{"type":"text","text":"Looking into the billing tests."}]}}
        {"type":"result","subtype":"success","result":"Fixed the billing tests.","is_error":false,"usage":{"input_tokens":1450,"output_tokens":260}}
        """;

    // The codex happy stream led by the `thread.started` event real codex emits first (carrying the thread_id the
    // production CodexHarness.BuildResult folds into AgentRunResult.SessionId — the handle a `codex exec resume` uses).
    private const string CodexSessionFixture =
        """
        {"type":"thread.started","thread_id":"thr-codex-realexec"}
        {"type":"agent_message","message":"Fixed the billing calculation."}
        {"type":"task_complete","message":"completed"}
        """;

    // The claude happy stream whose final `result` line carries the run `session_id` (the handle a `claude --resume` uses).
    private const string ClaudeSessionFixture =
        """
        {"type":"system","subtype":"init","cwd":"/tmp/ws"}
        {"type":"assistant","message":{"content":[{"type":"text","text":"Looking into the billing tests."}]}}
        {"type":"result","subtype":"success","result":"Fixed the billing tests.","is_error":false,"session_id":"sess-claude-realexec"}
        """;

    [Theory]
    [InlineData("codex-cli")]
    [InlineData("claude-code")]
    public async Task Real_harness_extracts_token_usage_and_projects_it_onto_the_metric(string harnessKind)
    {
        if (OperatingSystem.IsWindows()) return;

        // The high-fidelity value-correctness proof for BOTH harnesses: the REAL CodexHarness / ClaudeCodeHarness parse
        // a real-shaped USAGE line off the pipe (codex `token_count.info.total_token_usage`; claude `result.usage`), the
        // real executor persists it, and AgentMetricsReader — the SAME reader the run-detail outline/terminal consume —
        // projects that figure onto the per-agent metric. Pins the Claude/Codex token PARITY end-to-end (the unit tests
        // pin it only at the parser), deterministically, no real model needed.
        var (commandEnvVar, fixture) = UsageCase(harnessKind);
        using var cli = new FakeCli(commandEnvVar, fixture);

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, harnessKind, cli.Env());

        await ExecuteRealAsync(runId);

        using var scope = _fixture.BeginScope();
        var metrics = await scope.Resolve<AgentMetricsReader>().ReadAsync(teamId, new[] { runId }, DateTimeOffset.UtcNow, CancellationToken.None);

        var m = metrics[runId];
        m.Status.ShouldBe(AgentRunStatus.Succeeded);
        m.InputTokens.ShouldBe(1450, $"{harnessKind}: the real harness parsed the usage line off the pipe and it reached the projected metric");
        m.OutputTokens.ShouldBe(260, $"{harnessKind}: output tokens project identically across harnesses");
        m.DurationMs.ShouldNotBeNull("a completed real-harness run carries a live duration off its persisted timestamps");
    }

    private (string CommandEnvVar, string Fixture) UsageCase(string harnessKind) =>
        harnessKind switch
        {
            "codex-cli" => (CodexHarness.CommandEnvVar, CodexUsageFixture),
            "claude-code" => (ClaudeCodeHarness.CommandEnvVar, ClaudeUsageFixture),
            _ => throw new ArgumentOutOfRangeException(nameof(harnessKind), harnessKind, null),
        };

    [Theory]
    [InlineData("codex-cli", "thr-codex-realexec")]
    [InlineData("claude-code", "sess-claude-realexec")]
    public async Task Real_harness_captures_the_session_id_off_the_pipe_and_persists_it_on_the_agent_run(string harnessKind, string expectedSessionId)
    {
        if (OperatingSystem.IsWindows()) return;

        // P3.1a end-to-end at the high-fidelity tier: the REAL harness parses its native session-bearing line
        // (codex thread.started / claude result.session_id) off a real OS pipe, the REAL executor folds it onto
        // AgentRunResult.SessionId, and persists it BOTH on the promoted AgentRun.session_id column (the handle the
        // CONTINUE lookup reads) and inside result_jsonb. No binary, no model — just the production pipeline.
        var (commandEnvVar, fixture) = SessionCase(harnessKind);
        using var cli = new FakeCli(commandEnvVar, fixture);

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, harnessKind, cli.Env());

        await ExecuteRealAsync(runId);

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Succeeded, $"{harnessKind}: the real harness streamed + parsed the fixture and the run folded to Succeeded");
        run.SessionId.ShouldBe(expectedSessionId, $"{harnessKind}: the real harness captured its CLI session/thread id off the pipe and the executor persisted it on the promoted column");

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.SessionId.ShouldBe(expectedSessionId, $"{harnessKind}: the captured id also round-trips through result_jsonb");
    }

    private (string CommandEnvVar, string Fixture) SessionCase(string harnessKind) =>
        harnessKind switch
        {
            "codex-cli" => (CodexHarness.CommandEnvVar, CodexSessionFixture),
            "claude-code" => (ClaudeCodeHarness.CommandEnvVar, ClaudeSessionFixture),
            _ => throw new ArgumentOutOfRangeException(nameof(harnessKind), harnessKind, null),
        };

    [Theory]
    [InlineData("codex-cli", "resume")]
    [InlineData("claude-code", "--resume")]
    public async Task Real_executor_carries_the_resume_flag_and_prior_session_to_the_spawned_process(string harnessKind, string expectedFlag)
    {
        if (OperatingSystem.IsWindows()) return;

        // P3.2 end-to-end: a task carrying ResumeFromSessionId flows through the REAL harness BuildInvocation → the
        // REAL runner → a REAL OS process whose argv the fake CLI dumps. Proves the built resume flag (and the prior
        // id) actually reach the spawned process — not just the in-memory SandboxSpec. No producer sets the field in
        // production yet (a later slice does); this pins the plumbing the CONTINUE decision will rely on.
        var (commandEnvVar, fixture) = SessionCase(harnessKind);
        using var cli = new FakeCli(commandEnvVar, fixture);

        var argvDump = Path.Combine(Path.GetTempPath(), "cs-argv-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var teamId = await SeedTeamAsync();
            var env = new Dictionary<string, string>(cli.Env()) { ["FAKE_ARGV_OUT"] = argvDump };
            var runId = await CreateRunAsync(teamId, harnessKind, env, resumeFromSessionId: "prior-session-7c3");

            await ExecuteRealAsync(runId);

            File.Exists(argvDump).ShouldBeTrue($"{harnessKind}: the fake CLI was spawned (argv dump written)");
            var argv = File.ReadAllText(argvDump);
            argv.ShouldContain(expectedFlag, customMessage: $"{harnessKind}: the real executor → runner carried the resume flag to the spawned process argv");
            argv.ShouldContain("prior-session-7c3", customMessage: $"{harnessKind}: the prior session id reached the process so the CLI resumes the right conversation");
        }
        finally
        {
            if (File.Exists(argvDump)) File.Delete(argvDump);
        }
    }

    [Fact]
    public async Task Real_executor_skips_capturing_a_session_transcript_over_the_size_cap()
    {
        if (OperatingSystem.IsWindows()) return;

        // A0: a pathologically-large session .jsonl must NOT be read whole into memory (OOM risk) — over the cap the
        // capture SKIPS and a continue cold-starts (cold-start >> OOM). Proven end-to-end: a real fake CLI writes a real
        // session file, the cap env is lowered below it, and the REAL executor persists an EMPTY transcript + still
        // Succeeds. Env mutation is safe here — PostgresCollection serializes its classes, and only the capture path (this
        // collection) reads this var; restored in finally.
        const string sid = "sess-claude-realexec";
        const string sessionContent = "{\"type\":\"assistant\",\"text\":\"this transcript is over the (test-lowered) cap\"}\n";

        var workspaceDir = Path.Combine(Path.GetTempPath(), "cs-cap-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);
        var relPath = ClaudeTranscriptPath.For(workspaceDir, sid);

        var priorCap = Environment.GetEnvironmentVariable(AgentRunExecutor.MaxSessionTranscriptBytesEnvVar);
        using var cli = new FakeCli(ClaudeCodeHarness.CommandEnvVar, ClaudeSessionFixture);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.MaxSessionTranscriptBytesEnvVar, "10");   // 10 bytes < the session file

            var teamId = await SeedTeamAsync();
            var env = new Dictionary<string, string>(cli.Env()) { ["FAKE_SESSION_REL"] = relPath, ["FAKE_SESSION_CONTENT"] = sessionContent };
            var runId = await CreateRunAsync(teamId, "claude-code", env, workspaceDirectory: workspaceDir);

            await ExecuteRealAsync(runId);

            using var scope = _fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded, "an over-cap transcript never fails the run — it just isn't captured");

            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            result.SessionTranscript.ShouldBe("", "the over-cap session file was skipped (not read into memory) — a continue cold-starts");
            result.SessionTranscriptArtifactId.ShouldBeNull("nothing captured → nothing offloaded");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.MaxSessionTranscriptBytesEnvVar, priorCap);
            if (Directory.Exists(workspaceDir)) Directory.Delete(workspaceDir, recursive: true);
        }
    }

    [Fact]
    public async Task Real_executor_captures_the_claude_session_transcript_from_the_config_home()
    {
        if (OperatingSystem.IsWindows()) return;

        // P3 (capture) end-to-end at the high-fidelity tier: the REAL ClaudeCodeHarness (it IS the IAgentSessionTranscript)
        // runs a fake claude that WRITES a session jsonl into its per-run CLAUDE_CONFIG_DIR — exactly where --resume reads
        // it — and the REAL executor reads that file from the config home BEFORE the spool is reaped and persists it on the
        // result. That is the durable transcript a later CONTINUE restores. The file's path is the PRODUCTION
        // ClaudeTranscriptPath, computed once in C# and handed to the fake CLI, so the WRITE and the CAPTURE key on the
        // identical encoding — no $PWD-resolution flakiness. A ScriptedHarness could not exercise this (only the real
        // Claude harness declares a session-transcript location), so this is the one test that proves the whole capture edge.
        const string sid = "sess-claude-realexec";
        const string sessionContent = "{\"type\":\"user\"}\n{\"type\":\"assistant\",\"text\":\"the prior turn the continue restores\"}\n";

        var workspaceDir = Path.Combine(Path.GetTempPath(), "cs-cap-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);
        var relPath = ClaudeTranscriptPath.For(workspaceDir, sid);

        using var cli = new FakeCli(ClaudeCodeHarness.CommandEnvVar, ClaudeSessionFixture);
        try
        {
            var teamId = await SeedTeamAsync();
            var env = new Dictionary<string, string>(cli.Env()) { ["FAKE_SESSION_REL"] = relPath, ["FAKE_SESSION_CONTENT"] = sessionContent };
            var runId = await CreateRunAsync(teamId, "claude-code", env, workspaceDirectory: workspaceDir);

            await ExecuteRealAsync(runId);

            using var scope = _fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded, "the real harness streamed the fixture and the run folded to Succeeded");

            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            result.SessionTranscript.ShouldBe(sessionContent, "the executor read the session jsonl from the per-run config home (where the fake CLI wrote it) and persisted it for a later continue");
        }
        finally
        {
            if (Directory.Exists(workspaceDir)) Directory.Delete(workspaceDir, recursive: true);
        }
    }

    [Fact]
    public async Task Real_executor_captures_the_codex_session_rollout_by_globbing_the_config_home()
    {
        if (OperatingSystem.IsWindows()) return;

        // P3 (Codex continue producer) end-to-end: the REAL CodexHarness (it IS the IAgentSessionTranscript) runs a fake
        // codex that WRITES a date-nested rollout into its per-run CODEX_HOME/sessions — with a timestamp not known ahead
        // of time, exactly like the real CLI — and the REAL executor CAPTURES it by GLOBBING sessions/ for the id-bearing
        // rollout (there is no cwd-computable path as Claude has). This proves the search-based capture shape works
        // through the production executor, not just a unit glob. The fixture's thread.started carries the id the glob keys on.
        const string sid = "thr-codex-realexec";   // matches CodexSessionFixture's thread.started
        const string rolloutContent = "{\"session_meta\":{\"id\":\"thr-codex-realexec\"}}\n{\"type\":\"turn\",\"text\":\"the prior turn the continue restores\"}\n";

        // A realistic date-nested rollout whose FILENAME carries the id (the timestamp is arbitrary — the executor globs, never computes it).
        var relPath = $"{CodexHarness.SessionsRoot}/2026/07/02/rollout-2026-07-02T00-00-00-{sid}.jsonl";

        using var cli = new FakeCli(CodexHarness.CommandEnvVar, CodexSessionFixture);
        var teamId = await SeedTeamAsync();
        var env = new Dictionary<string, string>(cli.Env()) { ["FAKE_SESSION_REL"] = relPath, ["FAKE_SESSION_CONTENT"] = rolloutContent };
        var runId = await CreateRunAsync(teamId, "codex-cli", env);   // no workspaceDirectory — Codex keys the session on the id, not the cwd

        await ExecuteRealAsync(runId);

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded, "the real codex harness streamed the fixture and the run folded to Succeeded");

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.SessionTranscript.ShouldBe(rolloutContent, "the executor GLOBBED CODEX_HOME/sessions for the id-bearing rollout the fake codex wrote and captured it — the search-based capture shape, no cwd-computed path");
    }

    [Fact]
    public async Task Real_executor_captures_the_session_transcript_even_when_the_run_fails()
    {
        if (OperatingSystem.IsWindows()) return;

        // Failed runs are PRIME continue candidates (the agent hit an error mid-task and a human wants to resume + redirect
        // it). The capture runs after the harness regardless of exit code, so a non-zero exit still persists the session
        // transcript — proving a CONTINUE can resume a conversation that ended in failure, not only a clean one.
        const string sid = "sess-claude-realexec";
        const string sessionContent = "{\"type\":\"assistant\",\"text\":\"got partway then errored\"}\n";

        var workspaceDir = Path.Combine(Path.GetTempPath(), "cs-cap-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);
        var relPath = ClaudeTranscriptPath.For(workspaceDir, sid);

        using var cli = new FakeCli(ClaudeCodeHarness.CommandEnvVar, ClaudeSessionFixture);
        try
        {
            var teamId = await SeedTeamAsync();
            var env = new Dictionary<string, string>(cli.Env(exitCode: 1)) { ["FAKE_SESSION_REL"] = relPath, ["FAKE_SESSION_CONTENT"] = sessionContent };
            var runId = await CreateRunAsync(teamId, "claude-code", env, workspaceDirectory: workspaceDir);

            await ExecuteRealAsync(runId);

            using var scope = _fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Failed, "the non-zero exit folds to Failed");

            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            result.SessionTranscript.ShouldBe(sessionContent, "a FAILED run still captures its session transcript — a continue can resume + redirect it");
        }
        finally
        {
            if (Directory.Exists(workspaceDir)) Directory.Delete(workspaceDir, recursive: true);
        }
    }

    [Fact]
    public async Task Real_executor_leaves_the_session_transcript_empty_when_the_cli_wrote_no_session_file()
    {
        if (OperatingSystem.IsWindows()) return;

        // The cold-start-safe complement: a run whose CLI persists NO session file (the common pre-resume case) captures an
        // EMPTY session transcript — so the 3.2c producer's both-or-neither guard sees nothing to restore and the continue
        // cold-starts. Proves the capture never fabricates a transcript and never fails the run when the file is absent.
        var workspaceDir = Path.Combine(Path.GetTempPath(), "cs-cap-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);

        using var cli = new FakeCli(ClaudeCodeHarness.CommandEnvVar, ClaudeSessionFixture);
        try
        {
            var teamId = await SeedTeamAsync();
            var runId = await CreateRunAsync(teamId, "claude-code", cli.Env(), workspaceDirectory: workspaceDir);

            await ExecuteRealAsync(runId);

            using var scope = _fixture.BeginScope();
            var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(AgentRunStatus.Succeeded);

            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
            result.SessionTranscript.ShouldBe("", "no session file on disk → nothing captured → a continue cold-starts");
            result.SessionTranscriptArtifactId.ShouldBeNull("nothing to offload when there's no captured transcript");
        }
        finally
        {
            if (Directory.Exists(workspaceDir)) Directory.Delete(workspaceDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("codex-cli")]
    [InlineData("claude-code")]
    public async Task Real_harness_streams_parses_persists_and_completes_a_session(string harnessKind)
    {
        if (OperatingSystem.IsWindows()) return;

        var expected = HappyCase(harnessKind);
        using var cli = new FakeCli(expected.CommandEnvVar, expected.Fixture);

        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, harnessKind, cli.Env());

        await ExecuteRealAsync(runId);

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        var run = await svc.GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded, "the real harness streamed + parsed the fixture and the run folded to Succeeded");

        var events = await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None);
        events.Select(e => e.Kind).ShouldBe(expected.Kinds, "the production ParseEvent normalized the real-shaped lines off the pipe into the expected kinds");
        events.Select(e => e.Text).ShouldBe(expected.Texts);

        var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options)!;
        result.Summary.ShouldBe(expected.Summary);
        result.ChangedFiles.ShouldBe(expected.ChangedFiles);
    }

    [Fact]
    public async Task A_nonzero_harness_exit_lands_the_run_failed()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeCli(CodexHarness.CommandEnvVar, CodexFixture);
        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, "codex-cli", cli.Env(exitCode: 7));

        await ExecuteRealAsync(runId);

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Failed, "the real BuildResult folds a non-zero exit to Failed even after emitting events");
        run.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_harness_that_overruns_its_timeout_is_killed_and_lands_timed_out()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeCli(CodexHarness.CommandEnvVar, CodexFixture);
        var teamId = await SeedTeamAsync();
        // Real CTS-driven kill: the fixture script sleeps 10s, the run's wall-clock cap is 1s.
        var runId = await CreateRunAsync(teamId, "codex-cli", cli.Env(sleepSeconds: 10), timeoutSeconds: 1);

        await ExecuteRealAsync(runId);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status
            .ShouldBe(AgentRunStatus.TimedOut, "the linked CTS fired, the process tree was killed, and the run mapped to TimedOut — not Failed");
    }

    [Fact]
    public async Task Malformed_lines_are_tolerated_and_the_run_still_completes()
    {
        if (OperatingSystem.IsWindows()) return;

        const string junkInterleaved =
            """
            not json at all
            {"type":"agent_reasoning","message":"thinking"}
            {"no_type":"here"}
            {"type":"task_complete","message":"done"}
            """;
        using var cli = new FakeCli(CodexHarness.CommandEnvVar, junkInterleaved);
        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, "codex-cli", cli.Env());

        await ExecuteRealAsync(runId);

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentRunService>();

        (await svc.GetAsync(runId, CancellationToken.None)).Status
            .ShouldBe(AgentRunStatus.Succeeded, "tolerant ParseEvent never throws on real junk bytes — non-event lines are dropped and the run still completes");
        (await svc.GetEventsAsync(runId, teamId, 0, CancellationToken.None)).Select(e => e.Kind)
            .ShouldBe(new[] { AgentEventKind.Reasoning, AgentEventKind.Completed }, "only the two parseable lines persisted; the junk + typeless lines were dropped");
    }

    [Fact]
    public async Task Cancellation_propagates_and_does_not_complete_the_run()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeCli(CodexHarness.CommandEnvVar, CodexFixture);
        var teamId = await SeedTeamAsync();
        var runId = await CreateRunAsync(teamId, "codex-cli", cli.Env());

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => ExecuteRealAsync(runId, cancelled.Token));

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        new[] { AgentRunStatus.Succeeded, AgentRunStatus.Failed, AgentRunStatus.TimedOut }
            .ShouldNotContain(run.Status, "cancellation must not COMPLETE the run — it stays non-terminal (re-dispatchable), never a false terminal state");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private async Task ExecuteRealAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        using var scope = _fixture.BeginScope();
        // The fully DI-wired production executor — real harness registry (codex-cli + claude-code), real runner, real notifier.
        await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, cancellationToken);
    }

    private async Task<Guid> CreateRunAsync(Guid teamId, string harnessKind, IReadOnlyDictionary<string, string> env, int timeoutSeconds = 1800, string? resumeFromSessionId = null, string? workspaceDirectory = null)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "fix the billing tests", Harness = harnessKind, Model = null, Environment = env, TimeoutSeconds = timeoutSeconds, ResumeFromSessionId = resumeFromSessionId, WorkspaceDirectory = workspaceDirectory },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    private (string CommandEnvVar, string Fixture, AgentEventKind[] Kinds, string[] Texts, string Summary, string[] ChangedFiles) HappyCase(string harnessKind) =>
        harnessKind switch
        {
            "codex-cli" => (CodexHarness.CommandEnvVar, CodexFixture,
                new[] { AgentEventKind.Reasoning, AgentEventKind.CommandExecuted, AgentEventKind.FileChanged, AgentEventKind.AssistantMessage, AgentEventKind.Completed },
                new[] { "Analyzing the failing billing tests", "npm test", "src/billing/Invoice.cs", "Fixed the billing calculation.", "completed" },
                "Fixed the billing calculation.", new[] { "src/billing/Invoice.cs" }),
            "claude-code" => (ClaudeCodeHarness.CommandEnvVar, ClaudeFixture,
                new[] { AgentEventKind.Started, AgentEventKind.AssistantMessage, AgentEventKind.CommandExecuted, AgentEventKind.FileChanged, AgentEventKind.Completed },
                new[] { "Session started", "Looking into the billing tests.", "npm test", "src/billing/Invoice.cs", "Fixed the billing tests." },
                "Fixed the billing tests.", new[] { "src/billing/Invoice.cs" }),
            _ => throw new ArgumentOutOfRangeException(nameof(harnessKind)),
        };

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"realexec-{userId:N}@test.local", Name = $"realexec-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"realexec-{teamId:N}", Name = "Real Exec Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    /// <summary>
    /// A committed-at-runtime fake CLI: writes a POSIX emitter script + the fixture to a GUID temp dir and
    /// points the harness's <c>CommandEnvVar</c> at the script, so the REAL harness BuildInvocation spawns it
    /// instead of codex/claude. The script honours FAKE_SLEEP / FAKE_EXIT from the run's environment, so one
    /// script covers happy / non-zero-exit / timeout. Restores the env var + deletes the dir on dispose.
    /// </summary>
    private sealed class FakeCli : IDisposable
    {
        private readonly string _envVar;
        private readonly string? _original;
        private readonly string _dir;

        public string FixturePath { get; }

        public FakeCli(string commandEnvVar, string fixtureContent)
        {
            _envVar = commandEnvVar;
            _dir = Path.Combine(Path.GetTempPath(), "cs-fakecli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            FixturePath = Path.Combine(_dir, "fixture.jsonl");
            File.WriteAllText(FixturePath, fixtureContent);

            var script = Path.Combine(_dir, "fake-cli.sh");
            // When FAKE_ARGV_OUT is set, dump the spawned process's args ($@) there first — lets a test prove the REAL
            // executor→runner carried a built flag (e.g. --resume) all the way to the process argv. Inert otherwise.
            // When FAKE_SESSION_REL is set, write FAKE_SESSION_CONTENT into the per-run config home at that
            // config-home-relative path — simulates the CLI persisting its resumable session file, so the REAL executor's
            // P3 capture has a real on-disk file to read. The config home is whichever env var the harness isolates
            // (CLAUDE_CONFIG_DIR for claude, CODEX_HOME for codex — exactly one is set per run), so one script serves both.
            // Inert otherwise.
            File.WriteAllText(script, "#!/bin/sh\n[ -n \"$FAKE_ARGV_OUT\" ] && printf '%s\\n' \"$@\" > \"$FAKE_ARGV_OUT\"\nCFG=\"${CLAUDE_CONFIG_DIR:-$CODEX_HOME}\"\n[ -n \"$FAKE_SESSION_REL\" ] && { mkdir -p \"$CFG/$(dirname \"$FAKE_SESSION_REL\")\"; printf '%s' \"$FAKE_SESSION_CONTENT\" > \"$CFG/$FAKE_SESSION_REL\"; }\n[ -n \"$FAKE_SLEEP\" ] && sleep \"$FAKE_SLEEP\"\ncat \"$FAKE_FIXTURE\"\nexit \"${FAKE_EXIT:-0}\"\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            _original = Environment.GetEnvironmentVariable(commandEnvVar);
            Environment.SetEnvironmentVariable(commandEnvVar, script);
        }

        /// <summary>The run environment the spawned script reads — the fixture to emit, plus optional exit code / pre-emit sleep.</summary>
        public IReadOnlyDictionary<string, string> Env(int exitCode = 0, int sleepSeconds = 0)
        {
            var env = new Dictionary<string, string> { ["FAKE_FIXTURE"] = FixturePath };
            if (exitCode != 0) env["FAKE_EXIT"] = exitCode.ToString();
            if (sleepSeconds != 0) env["FAKE_SLEEP"] = sleepSeconds.ToString();
            return env;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_envVar, _original);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
