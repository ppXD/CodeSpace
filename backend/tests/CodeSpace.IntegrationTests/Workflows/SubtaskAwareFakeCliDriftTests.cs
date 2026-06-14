using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Rule-12.5 DRIFT DETECTOR for the inline codex-event mirror the <see cref="SubtaskAwareFakeCli"/> emits in
/// <see cref="HeadlineFlowE2ETests"/>. The fake CLI hand-prints JSONL shaped like <c>codex exec --json</c>;
/// this pins that mirror against the PRODUCTION harness so a divergence fails LOUDLY here instead of silently
/// passing a stale shape in the E2E.
///
/// <para>The pin runs the fake's three documented event lines (the SAME types the canonical
/// <c>RealHarnessExecutionTests.CodexFixture</c> mirror uses) through the REAL
/// <see cref="CodexHarness.ParseEvent"/> + <see cref="CodexHarness.BuildResult"/> and asserts they normalize to
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

        var parsed = lines.Select(harness.ParseEvent).ToList();

        parsed.ShouldAllBe(e => e != null, "every fake-CLI line must still parse through the real CodexHarness.ParseEvent — a null means the mirror's event shape drifted from what production accepts");

        parsed.Select(e => e!.Kind).ShouldBe(
            new[] { AgentEventKind.Reasoning, AgentEventKind.AssistantMessage, AgentEventKind.Completed },
            customMessage: "the fake CLI's event types must keep normalizing to these kinds — if codex's type→kind table changed, update BOTH the fake and this pin");

        // The summary BuildResult folds (what the executor records + the synthesizer composes) must match the
        // deterministic transform the E2E asserts. A drift in how codex's final message maps to Summary breaks this.
        var result = harness.BuildResult(parsed!, exitCode: 0);
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
}
