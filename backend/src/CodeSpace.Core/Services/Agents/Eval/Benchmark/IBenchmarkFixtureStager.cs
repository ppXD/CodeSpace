namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// Materializes a benchmark task's fixture (named by <c>BenchmarkTask.FixtureRef</c>) into a fresh workspace
/// directory in its FAILING start-state — the "where does a corpus task's repo come from" variant axis (Rule 18.3).
/// The corpus runner depends on this seam, never on a concrete stager, so the offline SEED corpus (a local shell
/// fixture) and a future REMOTE corpus (a git clone-URL+ref behind the same <c>FixtureRef</c> field) plug in with
/// zero runner change. Slice 1 ships only the seed stager.
/// </summary>
public interface IBenchmarkFixtureStager
{
    /// <summary>Stage the fixture named by <paramref name="fixtureRef"/> into <paramref name="directory"/> (already created) in its failing start-state. Throws on an unknown ref / a staging failure — the runner treats that as an infra error for that pair.</summary>
    void Stage(string fixtureRef, string directory);
}
