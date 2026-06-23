using CodeSpace.Core.Services.Agents.Context;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the <see cref="ContextSourceRegistry"/> dispatch spine (same contract as TaskLaunchSeedProviderRegistry /
/// AgentToolRegistry): dedups on <see cref="IContextSource.Kind"/> (a duplicate kind throws at build), resolves by
/// kind (unknown throws; TryResolve returns false), and exposes a kind-ordered catalog. Pure logic — no DB.
/// </summary>
[Trait("Category", "Unit")]
public class ContextSourceRegistryTests
{
    private sealed class FakeSource : IContextSource
    {
        public required string Kind { get; init; }
        public string Description => $"the {Kind} source";
        public Task<AgentContextResult> RetrieveAsync(AgentContextQuery query, CancellationToken cancellationToken) => Task.FromResult(AgentContextResult.Empty);
    }

    private static FakeSource Source(string kind) => new() { Kind = kind };

    [Fact]
    public void All_is_ordered_by_kind()
    {
        var registry = new ContextSourceRegistry(new[] { Source("session.turns"), Source("session.summary"), Source("repo.layout") });

        registry.All.Select(s => s.Kind).ShouldBe(new[] { "repo.layout", "session.summary", "session.turns" });
    }

    [Fact]
    public void Resolve_returns_the_source_for_a_known_kind()
    {
        var registry = new ContextSourceRegistry(new[] { Source("session.turns"), Source("session.summary") });

        registry.Resolve("session.summary").Kind.ShouldBe("session.summary");
    }

    [Fact]
    public void Resolve_throws_a_teachable_error_for_an_unknown_kind()
    {
        var registry = new ContextSourceRegistry(new[] { Source("session.turns") });

        var ex = Should.Throw<InvalidOperationException>(() => registry.Resolve("nope"));
        ex.Message.ShouldContain("nope");
        ex.Message.ShouldContain("Context/Sources", customMessage: "the error teaches how a new source self-registers");
    }

    [Theory]
    [InlineData("session.turns", true)]
    [InlineData("missing", false)]
    public void TryResolve_reports_presence(string kind, bool expected)
    {
        var registry = new ContextSourceRegistry(new[] { Source("session.turns") });

        registry.TryResolve(kind, out var source).ShouldBe(expected);
        if (expected) source.Kind.ShouldBe(kind);
    }

    [Fact]
    public void A_duplicate_kind_throws_at_build()
    {
        var ex = Should.Throw<InvalidOperationException>(() => new ContextSourceRegistry(new[] { Source("session.turns"), Source("session.turns") }));

        ex.Message.ShouldContain("Duplicate");
        ex.Message.ShouldContain("session.turns");
    }
}
