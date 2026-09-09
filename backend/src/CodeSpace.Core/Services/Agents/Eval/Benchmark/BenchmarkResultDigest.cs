using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>Version-one content identity for the exact terminal result sealed by a cell admission and later projected into an observation.</summary>
public static class BenchmarkResultDigest
{
    public static string Compute(BenchmarkResult result) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result, Agents.AgentJson.Options))));
}
