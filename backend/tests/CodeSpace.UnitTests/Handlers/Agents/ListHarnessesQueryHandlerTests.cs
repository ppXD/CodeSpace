using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Agents;

[Trait("Category", "Unit")]
public class ListHarnessesQueryHandlerTests
{
    private sealed class FakeHarness : IAgentHarness
    {
        public FakeHarness(string kind, string version, params string[] models)
        {
            Kind = kind;
            Version = version;
            Models = models;
        }

        public string Kind { get; }
        public string Version { get; }
        public IReadOnlyList<string> Models { get; }

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "x" };
        public AgentEvent? ParseEvent(string rawLine) => null;
        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) =>
            new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed" };
    }

    private static ListHarnessesQueryHandler HandlerFor(params IAgentHarness[] harnesses) =>
        new(new AgentHarnessRegistry(harnesses));

    [Fact]
    public async Task Projects_every_registered_harness_with_its_kind_version_and_models()
    {
        var handler = HandlerFor(
            new FakeHarness("codex-cli", "1.2", "gpt-5-codex", "gpt-5"),
            new FakeHarness("claude-code", "0.9", "claude-opus-4-8"));

        var result = await handler.Handle(new ListHarnessesQuery(), CancellationToken.None);

        result.Count.ShouldBe(2);

        var codex = result.Single(h => h.Kind == "codex-cli");
        codex.Version.ShouldBe("1.2");
        codex.Models.ShouldBe(new[] { "gpt-5-codex", "gpt-5" });

        result.Single(h => h.Kind == "claude-code").Models.ShouldBe(new[] { "claude-opus-4-8" });
    }

    [Fact]
    public async Task Returns_empty_when_no_harness_is_registered()
    {
        var handler = HandlerFor();

        (await handler.Handle(new ListHarnessesQuery(), CancellationToken.None)).ShouldBeEmpty();
    }
}
