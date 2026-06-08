using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class SandboxRunnerRegistryTests
{
    private sealed class FakeRunner : ISandboxRunner
    {
        public FakeRunner(string kind) => Kind = kind;

        public string Kind { get; }

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" });
    }

    [Fact]
    public void Resolves_a_registered_runner_by_kind()
    {
        var local = new FakeRunner("local");
        var registry = new SandboxRunnerRegistry(new[] { local });

        registry.Resolve("local").ShouldBeSameAs(local);
    }

    [Fact]
    public void Resolve_throws_for_an_unregistered_kind()
    {
        var registry = new SandboxRunnerRegistry(new[] { new FakeRunner("local") });

        Should.Throw<InvalidOperationException>(() => registry.Resolve("docker"));
    }

    [Fact]
    public void Construction_rejects_duplicate_kinds()
    {
        Should.Throw<InvalidOperationException>(() => new SandboxRunnerRegistry(new[] { new FakeRunner("local"), new FakeRunner("local") }));
    }

    [Fact]
    public void All_lists_every_registered_runner()
    {
        var registry = new SandboxRunnerRegistry(new[] { new FakeRunner("local"), new FakeRunner("docker") });

        registry.All.Count.ShouldBe(2);
    }
}
