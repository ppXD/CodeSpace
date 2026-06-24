using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Stagers;

/// <summary>
/// The SEED-corpus fixture stager (Rule 18.3 — one impl beside its variant folder): materializes a named offline
/// shell fixture via <see cref="SeedBenchmarkFixtures.Stage"/>. Self-registers via <see cref="ISingletonDependency"/>;
/// a future remote-corpus stager is a sibling impl, never an edit to the corpus runner.
/// </summary>
public sealed class SeedFixtureStager : IBenchmarkFixtureStager, ISingletonDependency
{
    public void Stage(string fixtureRef, string directory) => SeedBenchmarkFixtures.Stage(fixtureRef, directory);
}
