using CodeSpace.Core.Services.Agents.ModelCredentials;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins <see cref="IModelPoolSelector.ResolvePoolDefaultAsync"/>'s DEFAULT interface implementation — the fallback a
/// fake that doesn't model IsDefault/tier still gets "for free" — composed ENTIRELY from the interface's own existing
/// members (<see cref="IModelPoolSelector.ListPoolAsync"/> + <see cref="IModelPoolSelector.ResolveDispatchAsync"/>),
/// no new ranking invented at this layer (the real <c>ModelPoolSelector</c> overrides it with the precedence-ranked DB
/// query — that ranking is <c>AgentPlaneModelRankingTests</c>'s job, and the actual bound-dispatch wiring in
/// <c>RealSupervisorActionExecutor.Spawn.cs</c> is covered by
/// <c>SupervisorRichSpawnFlowTests.A_spawn_with_no_effective_model_still_dispatches_from_a_one_model_pool</c>, an
/// integration test).
/// </summary>
[Trait("Category", "Unit")]
public sealed class ModelPoolSelectorDefaultsTests
{
    [Fact]
    public async Task Default_resolves_the_pool_catalogs_first_entry_through_ResolveDispatchAsync()
    {
        var teamId = Guid.NewGuid();
        var rowIds = new[] { Guid.NewGuid() };
        var fake = new FakeSelector(pool: [new PoolModelInfo("pool-only-model", "Anthropic")], dispatch: new ModelDispatchRef { ModelId = "pool-only-model", ModelCredentialId = Guid.NewGuid(), Provider = "Anthropic" });
        IModelPoolSelector selector = fake;   // the default body is only reachable through the INTERFACE reference (C# DIM rule)

        var dispatch = await selector.ResolvePoolDefaultAsync(teamId, rowIds, CancellationToken.None);

        dispatch.ShouldNotBeNull();
        dispatch!.ModelId.ShouldBe("pool-only-model");
        fake.ListPoolCalls.Count.ShouldBe(1, "the SAME bounded pool the caller passed in — no widening");
        fake.ListPoolCalls[0].ShouldBe((teamId, (IReadOnlyList<Guid>?)rowIds));
        fake.ResolveDispatchCalls.Count.ShouldBe(1, "the catalog's own model name feeds ResolveDispatchAsync, bounded to the SAME pool again");
        fake.ResolveDispatchCalls[0].ShouldBe((teamId, "pool-only-model", (IReadOnlyList<Guid>?)rowIds));
    }

    [Fact]
    public async Task Default_returns_null_and_never_calls_ResolveDispatchAsync_when_the_pool_catalog_is_empty()
    {
        var fake = new FakeSelector(pool: [], dispatch: null);
        IModelPoolSelector selector = fake;

        var dispatch = await selector.ResolvePoolDefaultAsync(Guid.NewGuid(), new[] { Guid.NewGuid() }, CancellationToken.None);

        dispatch.ShouldBeNull();
        fake.ResolveDispatchCalls.ShouldBeEmpty("nothing in the catalog ⇒ nothing to resolve — fail closed, not a guess");
    }

    /// <summary>Implements ONLY what the default body calls (<c>ListPoolAsync</c> / <c>ResolveDispatchAsync</c>) plus the
    /// interface's other non-default members as inert stubs — <c>ResolvePoolDefaultAsync</c> itself is deliberately left
    /// UNIMPLEMENTED so the interface's own default runs, which is exactly what this test pins.</summary>
    private sealed class FakeSelector : IModelPoolSelector
    {
        private readonly IReadOnlyList<PoolModelInfo> _pool;
        private readonly ModelDispatchRef? _dispatch;

        public FakeSelector(IReadOnlyList<PoolModelInfo> pool, ModelDispatchRef? dispatch)
        {
            _pool = pool;
            _dispatch = dispatch;
        }

        public List<(Guid TeamId, IReadOnlyList<Guid>? AllowedRowIds)> ListPoolCalls { get; } = [];
        public List<(Guid TeamId, string ModelName, IReadOnlyList<Guid>? AllowedRowIds)> ResolveDispatchCalls { get; } = [];

        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken)
        {
            ListPoolCalls.Add((teamId, allowedRowIds));
            return Task.FromResult(_pool);
        }

        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken)
        {
            ResolveDispatchCalls.Add((teamId, modelName, allowedRowIds));
            return Task.FromResult(_dispatch);
        }

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(null);
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(null);
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
