using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins what each adapter's stable native protocol can actually say about physical model calls. This is coverage
/// metadata, not a promise that capture succeeded and not transcript/body completeness.
/// </summary>
[Trait("Category", "Unit")]
public sealed class HarnessModelCallObservationCoverageTests
{
    [Fact]
    public void Claude_declares_per_response_metadata_and_codex_only_a_cumulative_aggregate()
    {
        new ClaudeCodeHarness().ShouldBeAssignableTo<IAgentHarnessModelCallObservation>()!
            .ModelCallObservationCoverage.ShouldBe(HarnessModelCallObservationCoverage.PerResponseMetadata);
        new ClaudeCodeHarness().ShouldBeAssignableTo<IAgentModelCallFrameReader>(
            "per-response metadata is useful to the model-call plane only because this adapter can read the response frame that states it");
        new CodexHarness().ShouldBeAssignableTo<IAgentHarnessModelCallObservation>()!
            .ModelCallObservationCoverage.ShouldBe(HarnessModelCallObservationCoverage.CumulativeAggregate);
        new CodexHarness().ShouldNotBeAssignableTo<IAgentModelCallFrameReader>(
            "a cumulative turn total cannot be projected as a physical call the stream never enumerated");
    }

    [Fact]
    public void Every_shipped_harness_explicitly_declares_its_observation_coverage()
    {
        var shipped = typeof(IAgentHarness).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IAgentHarness).IsAssignableFrom(type))
            .ToList();

        shipped.ShouldNotBeEmpty();
        shipped.ShouldAllBe(type => typeof(IAgentHarnessModelCallObservation).IsAssignableFrom(type),
            customMessage: "a new production adapter fails this build-time contract until it declares coverage; absence cannot be inferred from its name or transcript");
    }

    [Fact]
    public void An_undeclared_legacy_adapter_is_unknown_rather_than_none()
    {
        AgentNativeRecordPump.ModelCallObservationCoverageOf(new LegacyHarness()).ShouldBe(
            nameof(HarnessModelCallObservationCoverage.LegacyUnknown),
            customMessage: "absence of a declaration is not evidence that the harness observed no telemetry");

        AgentNativeRecordPump.ModelCallObservationCoverageOf(new UndefinedHarness()).ShouldBe(
            nameof(HarnessModelCallObservationCoverage.LegacyUnknown),
            customMessage: "an undefined future enum value must not materialize as a known coverage claim");
    }

    private class LegacyHarness : IAgentHarness
    {
        public string Kind => "legacy";
        public string Version => "1";
        public IReadOnlyList<string> Models => Array.Empty<string>();
        public SandboxSpec BuildInvocation(AgentTask task) => throw new NotSupportedException();
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => Array.Empty<AgentEvent>();
        public IAgentEventFolder CreateFolder() => throw new NotSupportedException();
    }

    private sealed class UndefinedHarness : LegacyHarness, IAgentHarnessModelCallObservation
    {
        public HarnessModelCallObservationCoverage ModelCallObservationCoverage => (HarnessModelCallObservationCoverage)500;
    }
}
