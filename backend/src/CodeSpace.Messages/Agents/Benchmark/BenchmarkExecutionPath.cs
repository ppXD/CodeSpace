using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>The production boundary a benchmark cell actually exercised.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BenchmarkExecutionPath>))]
public enum BenchmarkExecutionPath
{
    /// <summary>The benchmark runner created an AgentRun directly. Useful development evidence; it does not cover TaskLaunch routing, projection, or workflow completion.</summary>
    DirectAgentHarness,

    /// <summary>The benchmark entered through ITaskLaunchService and observed the resulting workflow to a truthful terminal outcome.</summary>
    TaskLaunch,
}
