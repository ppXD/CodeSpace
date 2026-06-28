namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Isolates <see cref="AgentCostPricingTests"/> — the ONE test class that mutates the process-global
/// <c>CODESPACE_AGENT_MODEL_PRICES</c> env override (e.g. temporarily setting <c>claude-opus-4-8=6/30</c>) to exercise
/// the price-table override path. <c>AgentCostPricing.ResolveTable()</c> reads that env var LIVE on every
/// <c>CostUsd</c> call, and many OTHER tests price a model live (e.g. <c>LlmCompleteNodeTests</c> prices its stub's
/// <c>claude-opus-4-8</c> completion against the seeded default). Without isolation, xUnit runs the mutator in parallel
/// with those readers and the override leaks into a concurrent read — flaking the reader's expected default-priced cost.
/// <c>DisableParallelization</c> runs this collection in its own sequential phase, so the mutator never overlaps any
/// parallel price-reader (it's the sole mutator, so isolating it alone closes the whole race class — no need to
/// enumerate every reader). Mirrors <c>LocalProcessIdleWatchdogCollection</c>.
/// </summary>
[CollectionDefinition("ModelPriceEnvMutation", DisableParallelization = true)]
public sealed class ModelPriceEnvMutationCollection;
