using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <see cref="WorkspaceProviderRegistry"/> resolution semantics — mirrors the sandbox-runner registry:
/// resolve by kind, reject an unknown kind, reject duplicate kinds at construction.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkspaceProviderRegistryTests
{
    [Fact]
    public void Resolves_a_provider_by_its_kind()
    {
        var local = new FakeProvider("local");
        var registry = new WorkspaceProviderRegistry(new IWorkspaceProvider[] { local, new FakeProvider("k8s") });

        registry.Resolve("local").ShouldBeSameAs(local);
        registry.All.Count.ShouldBe(2);
    }

    [Fact]
    public void Resolving_an_unknown_kind_throws() =>
        Should.Throw<InvalidOperationException>(() => new WorkspaceProviderRegistry(new IWorkspaceProvider[] { new FakeProvider("local") }).Resolve("k8s"));

    [Fact]
    public void Duplicate_kinds_are_rejected_at_construction() =>
        Should.Throw<InvalidOperationException>(() => new WorkspaceProviderRegistry(new IWorkspaceProvider[] { new FakeProvider("local"), new FakeProvider("local") }));

    private sealed class FakeProvider : IWorkspaceProvider
    {
        public FakeProvider(string kind) => Kind = kind;

        public string Kind { get; }

        public Task<IWorkspaceHandle> PrepareAsync(WorkspaceProvisionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not exercised in registry tests");
    }
}
