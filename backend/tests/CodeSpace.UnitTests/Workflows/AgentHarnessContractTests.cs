using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The universal invariants every <see cref="IAgentHarness"/> must satisfy, regardless of which CLI it
/// adapts: it advertises a kind / version / models, builds a runnable invocation, NEVER throws on junk
/// input (returns null for non-event lines), and folds exit codes to the right terminal status.
/// Harness-specific parsing (the native-type → <see cref="AgentEventKind"/> table) stays in that
/// harness's own tests; this base is the shared floor every harness inherits by subclassing.
/// </summary>
public abstract class AgentHarnessContractTests
{
    protected abstract IAgentHarness Harness { get; }

    private AgentTask MinimalTask() => new()
    {
        Goal = "do something",
        Harness = Harness.Kind,
        Model = Harness.Models.Count > 0 ? Harness.Models[0] : "model",
        TimeoutSeconds = 600,
    };

    [Fact]
    public void Kind_is_non_empty() => Harness.Kind.ShouldNotBeNullOrWhiteSpace();

    [Fact]
    public void Version_is_non_empty() => Harness.Version.ShouldNotBeNullOrWhiteSpace();

    [Fact]
    public void Advertises_at_least_one_model() => Harness.Models.ShouldNotBeEmpty();

    [Fact]
    public void BuildInvocation_produces_a_runnable_spec()
    {
        var spec = Harness.BuildInvocation(MinimalTask());

        spec.Command.ShouldNotBeNullOrWhiteSpace();
        spec.TimeoutSeconds.ShouldBe(600);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json {")]
    [InlineData("a plain log line, not a structured event")]
    public void ParseEvent_returns_null_for_non_event_lines_and_never_throws(string line) =>
        Harness.ParseEvent(line).ShouldBeNull();

    [Fact]
    public void BuildResult_maps_exit_zero_to_succeeded() =>
        Harness.BuildResult(Array.Empty<AgentEvent>(), 0).Status.ShouldBe(AgentRunStatus.Succeeded);

    [Fact]
    public void BuildResult_maps_nonzero_exit_to_failed() =>
        Harness.BuildResult(Array.Empty<AgentEvent>(), 1).Status.ShouldBe(AgentRunStatus.Failed);
}
