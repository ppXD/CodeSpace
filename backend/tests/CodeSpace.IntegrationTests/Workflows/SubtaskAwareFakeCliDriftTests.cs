using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Rule-12.5 DRIFT DETECTOR for the inline codex-event mirror the <see cref="SubtaskAwareFakeCli"/> emits in
/// <c>HeadlineFlowE2ETests</c> and <c>PlannerCodingFlowE2ETests</c>. The fake CLI hand-prints JSONL shaped like <c>codex exec --json</c>;
/// this pins that mirror against the PRODUCTION harness so a divergence fails LOUDLY here instead of silently
/// passing a stale shape in the E2E.
///
/// <para>The pin runs the fake's three documented event lines (the SAME types the canonical
/// <c>RealHarnessExecutionTests.CodexFixture</c> mirror uses) through the REAL
/// <see cref="CodexHarness.ParseEvents"/> + <see cref="CodexHarness.BuildResult"/> and asserts they normalize to
/// the kinds + summary the E2E relies on. It catches drift in ONE direction: a production harness change that stops
/// accepting these shapes. It cannot catch the fake moving — the event lines are duplicated here as literals rather
/// than read out of <c>ScriptBody</c>, so a fake that changes its lines leaves this test green. It is a pure check (no DB), but it's tagged Integration so it runs in the SAME CI gate
/// that builds the integration project it lives in — the Unit gate scans only CodeSpace.UnitTests, so a
/// Category=Unit trait here would run in NEITHER gate.</para>
/// </summary>
[Trait("Category", "Integration")]
public class SubtaskAwareFakeCliDriftTests
{
    [Fact]
    public void The_fake_cli_event_lines_still_parse_through_the_real_codex_harness_into_the_expected_shape()
    {
        var harness = new CodexHarness();

        // The exact JSONL the fake CLI prints for a branch whose goal is "Work on alpha" (the runner spawns the
        // script, which derives this from the goal arg). Kept in lock-step with SubtaskAwareFakeCli.ScriptBody.
        const string goal = "Work on alpha";
        var lines = new[]
        {
            """{"type":"agent_reasoning","message":"Planning work for: Work on alpha"}""",
            $$"""{"type":"agent_message","message":"{{SubtaskAwareFakeCli.SummaryPrefix}}Work on alpha"}""",
            """{"type":"task_complete","message":"completed"}""",
        };

        var parsed = lines.SelectMany(harness.ParseEvents).ToList();

        parsed.Count.ShouldBe(lines.Length, "every fake-CLI line must still parse to exactly one event through the real CodexHarness.ParseEvents — a drop means the mirror's event shape drifted from what production accepts");

        parsed.Select(e => e.Kind).ShouldBe(
            new[] { AgentEventKind.Reasoning, AgentEventKind.AssistantMessage, AgentEventKind.Completed },
            customMessage: "the fake CLI's event types must keep normalizing to these kinds — if codex's type→kind table changed, update BOTH the fake and this pin");

        // The summary BuildResult folds (what the executor records + the synthesizer composes) must match the
        // deterministic transform the E2E asserts. A drift in how codex's final message maps to Summary breaks this.
        var result = harness.BuildResult(parsed, exitCode: 0, "");
        result.Status.ShouldBe(AgentRunStatus.Succeeded);
        result.Summary.ShouldBe(SubtaskAwareFakeCli.ExpectedSummaryFor(goal),
            customMessage: "BuildResult must fold the fake CLI's final agent_message into exactly the summary HeadlineFlowE2ETests composes — the mirror + the E2E's expectation are one contract");
    }

    [Fact]
    public void The_fake_cli_emitted_event_types_match_its_declared_contract()
    {
        // The fake declares its emitted types (EmittedEventTypes) for documentation + this self-pin. If the
        // ScriptBody adds/removes/reorders a line, this catches the drift between the script and its declaration.
        SubtaskAwareFakeCli.EmittedEventTypes.ShouldBe(new[] { "agent_reasoning", "agent_message", "task_complete" });
    }

    [Fact]
    public void The_file_writing_fake_cli_lines_parse_through_the_real_harness_and_its_file_slug_is_deterministic()
    {
        // Rule-12.5 drift pin for FileWritingFakeCli (the driver of the whole-loop supervisor E2E). It emits the
        // SAME three codex-shaped lines as SubtaskAwareFakeCli PLUS writes a file; pin both the event contract and
        // the goal→filename slug the E2E's "changed files start with agent_" assertion depends on.
        var harness = new CodexHarness();
        const string goal = "do alpha";

        var lines = new[]
        {
            """{"type":"agent_reasoning","message":"Editing for: do alpha"}""",
            $$"""{"type":"agent_message","message":"{{FileWritingFakeCli.SummaryPrefix}}do alpha"}""",
            """{"type":"task_complete","message":"completed"}""",
        };

        var parsed = lines.SelectMany(harness.ParseEvents).ToList();
        parsed.Select(e => e.Kind).ShouldBe(new[] { AgentEventKind.Reasoning, AgentEventKind.AssistantMessage, AgentEventKind.Completed });

        var result = harness.BuildResult(parsed, exitCode: 0, "");
        result.Status.ShouldBe(AgentRunStatus.Succeeded);
        result.Summary.ShouldBe(FileWritingFakeCli.ExpectedSummaryFor(goal));

        FileWritingFakeCli.EmittedEventTypes.ShouldBe(new[] { "agent_reasoning", "agent_message", "task_complete" });
        // The C# FileFor mirror must equal the script's `tr -c 'A-Za-z0-9' '_'` slug — distinct goals → distinct files
        // (clean K-way merge); the E2E asserts the captured diff names a file with this prefix.
        FileWritingFakeCli.FileFor("do alpha").ShouldBe("agent_do_alpha.txt");
        FileWritingFakeCli.FileFor("do beta").ShouldBe("agent_do_beta.txt");
    }

    [Fact]
    public void The_file_writing_fake_claude_dialect_folds_the_same_summary_as_its_codex_dialect()
    {
        // The #1297 trap, pinned parse-level: Claude folds FinalSummary ?? Completed ?? AssistantMessage while
        // Codex SKIPS Completed — so the claude result line's `result` property must echo the codex agent_message
        // ("DONE: <goal>"), never a literal "completed", or the two dialects silently fold different summaries and
        // every whole-loop assertion on ExpectedSummaryFor becomes harness-lottery-dependent.
        const string goal = "do alpha";

        var claudeLines = new[]
        {
            $$$"""{"type":"assistant","message":{"content":[{"type":"text","text":"{{{FileWritingFakeCli.SummaryPrefix}}}do alpha"}]}}""",
            $$$"""{"type":"result","subtype":"success","result":"{{{FileWritingFakeCli.SummaryPrefix}}}do alpha","is_error":false}""",
        };

        var claude = new ClaudeCodeHarness();
        var result = claude.BuildResult(claudeLines.SelectMany(claude.ParseEvents).ToList(), exitCode: 0, "");

        result.Status.ShouldBe(AgentRunStatus.Succeeded);
        result.Summary.ShouldBe(FileWritingFakeCli.ExpectedSummaryFor(goal), "the two dialects must fold the SAME summary — the claude fold prefers the result line's text");
    }

    [Fact]
    public void The_file_writing_fake_script_serves_the_dialect_of_whichever_harness_invokes_it()
    {
        // The end-to-end half the parse pins can't see: the SCRIPT's own `$1` discriminator (codex argv always
        // starts with `exec`, claude with `--print`) and its last-positional goal extraction — both harnesses put
        // the prompt last. Runs the materialized script through /bin/sh exactly as the runner would, once per
        // dialect, and asserts each stdout parses through ITS harness to the SAME summary + the goal-derived file
        // lands in the cwd (the workspace-clone edit the whole-loop arm integrates).
        if (OperatingSystem.IsWindows()) return;

        const string goal = "do alpha";
        var dir = Path.Combine(Path.GetTempPath(), "cs-filewriting-dialect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var script = Path.Combine(dir, "fake-agent.sh");
            File.WriteAllText(script, FileWritingFakeCli.ScriptBody);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var codexStdout = RunScript(dir, script, "exec", "--json", goal);
            var claudeStdout = RunScript(dir, script, "--print", "--output-format", "stream-json", goal);

            var codex = new CodexHarness();
            var codexResult = codex.BuildResult(codexStdout.SelectMany(codex.ParseEvents).ToList(), exitCode: 0, "");
            var claude = new ClaudeCodeHarness();
            var claudeResult = claude.BuildResult(claudeStdout.SelectMany(claude.ParseEvents).ToList(), exitCode: 0, "");

            codexResult.Summary.ShouldBe(FileWritingFakeCli.ExpectedSummaryFor(goal));
            claudeResult.Summary.ShouldBe(codexResult.Summary, "one script, two dialects, one summary — the $1 branch must serve whichever harness the model picked");
            File.Exists(Path.Combine(dir, FileWritingFakeCli.FileFor(goal))).ShouldBeTrue("the WORK half is dialect-free — the goal-derived file is written regardless of who invoked it");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void A_model_length_goal_still_writes_its_file_and_never_exits_zero_on_a_failed_write()
    {
        // The run-31200742534 disease, pinned at the process level: a MODEL-authored goal is a multi-sentence
        // instruction whose full slug exceeded the 255-byte filename limit — the write failed, the script still
        // exited 0, and 37 Succeeded agents captured NOTHING (the whole-loop arcs' "did not converge" reds were
        // this test-infra lie, not model capability). The slug is now truncated to 100 chars AND the write is
        // fail-loud (exit 90), so this asserts both: the long-goal file lands, and a mutation that removes the
        // truncation trips the loud exit instead of a silent Succeeded.
        if (OperatingSystem.IsWindows()) return;

        var goal = string.Concat(Enumerable.Repeat("Implement the primary feature endpoint with validation and error handling following existing conventions. ", 4));
        var dir = Path.Combine(Path.GetTempPath(), "cs-filewriting-longgoal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var script = Path.Combine(dir, "fake-agent.sh");
            File.WriteAllText(script, FileWritingFakeCli.ScriptBody);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            RunScript(dir, script, "exec", "--json", goal);   // asserts exit 0 internally — a failed write now exits 90 and fails HERE

            var written = Directory.GetFiles(dir, FileWritingFakeCli.FilePrefix + "*.txt");
            written.ShouldHaveSingleItem("the truncated slug keeps a model-length goal writable — the whole point of the fix");
            Path.GetFileName(written[0]).ShouldBe(FileWritingFakeCli.FileFor(goal), "the C# mirror and the script must derive the SAME truncated name");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static string[] RunScript(string cwd, string script, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(script);
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000).ShouldBeTrue("the fake script must exit promptly");
        process.ExitCode.ShouldBe(0);

        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void The_high_volume_fake_cli_lines_still_parse_into_the_expected_per_line_shape()
    {
        // Rule-12.5 drift pin for HighVolumeSubtaskFakeCli (the driver of the two D1 map-fan-out E2E tests). It
        // hand-prints N "agent_message" lines tagged "<goal>#NNN" + a "task_complete" terminal; if codex's
        // type→kind mapping or the message→Text fold ever drifts, those E2E tests would silently parse a different
        // shape and their "every line present, in order" assertions would become meaningless. Pin it here loudly.
        var harness = new CodexHarness();
        const string goal = "Work on alpha";

        var lines = new[]
        {
            $$"""{"type":"agent_message","message":"{{goal}}#001"}""",
            $$"""{"type":"agent_message","message":"{{goal}}#060"}""",
            """{"type":"task_complete","message":"completed"}""",
        };

        var parsed = lines.SelectMany(harness.ParseEvents).ToList();

        parsed.Count.ShouldBe(lines.Length, "every high-volume fake line must still parse to one event through the real CodexHarness.ParseEvents");
        parsed.Select(e => e.Kind).ShouldBe(new[] { AgentEventKind.AssistantMessage, AgentEventKind.AssistantMessage, AgentEventKind.Completed },
            customMessage: "the high-volume fake's agent_message/task_complete must keep normalizing to AssistantMessage/Completed — the kinds the two map E2E tests filter + assert on");

        // The parsed AssistantMessage Text must equal what ExpectedLinesFor predicts — the exact contract the E2E
        // tests assert each branch's log against (a per-line tag drift would desync the fake from its expectation).
        var expected = HighVolumeSubtaskFakeCli.ExpectedLinesFor(goal);
        parsed[0].Text.ShouldBe(expected[0]);     // "<goal>#001"
        parsed[1].Text.ShouldBe(expected[^1]);    // "<goal>#060"
        expected.Count.ShouldBe(HighVolumeSubtaskFakeCli.LineCount);

        harness.BuildResult(parsed, exitCode: 0, "").Status.ShouldBe(AgentRunStatus.Succeeded);
    }
}
