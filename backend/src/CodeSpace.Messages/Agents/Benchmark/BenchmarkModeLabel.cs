namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// The single source of truth for the per-mode ROW LABEL a benchmark result carries onto the scorecard — so the
/// existing pure scorer (<c>EvalScorecard.Compute</c>, which groups by a string label) renders one comparable row
/// per mode WITHOUT any change to it or to PR-A's team-history scorecard. A pure static map (a noun's projection →
/// Messages, Rule 18.1); the benchmark scorecard service hands each result's mode through here to get the label it
/// groups by. Prefixed <c>bench:</c> so a benchmark row is never confusable with a real harness row.
/// </summary>
public static class BenchmarkModeLabel
{
    /// <summary>The label-namespace prefix marking a row as a benchmark mode (not a production harness). Pinned by a test (Rule 8) — it's the wire-visible row key an operator reads + a UI may filter on.</summary>
    public const string Prefix = "bench:";

    public static string For(BenchmarkMode mode) => mode switch
    {
        BenchmarkMode.HarnessCli => Prefix + "cli",
        BenchmarkMode.HarnessCliWithMcp => Prefix + "cli-mcp",
        BenchmarkMode.WorkflowMap => Prefix + "workflow-map",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown benchmark mode — add its scorecard label here."),
    };
}
