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
/// the kinds + summary the E2E relies on. If the production codex contract changes such that these shapes no
/// longer parse the way the fake assumes, this test breaks — exactly the "mirror diverged from prod" signal
/// Rule 12.5 mandates. It is a pure check (no DB), but it's tagged Integration so it runs in the SAME CI gate
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
        var result = harness.BuildResult(parsed, exitCode: 0);
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

        harness.BuildResult(parsed, exitCode: 0).Status.ShouldBe(AgentRunStatus.Succeeded);
    }
}
