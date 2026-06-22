using System.Collections.Concurrent;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Test-only node that fails a deterministic number of times, then succeeds — the controllable
/// failure source the retry-flow tests need (no real builtin can be made to fail-then-succeed
/// across in-process retries). Registered into the integration container via
/// <c>PostgresFixture.RegisterTestAssemblyTypes</c>; it lives in <c>INodeRegistry</c> so the
/// engine + validator accept it, but it's NOT part of any <c>IPluginModule</c>, so it never
/// shows up in the editor palette / node-manifest list.
///
/// <para>Config:
///   <list type="bullet">
///     <item><c>key</c> — unique per test; isolates the attempt counter so concurrent / repeated
///           runs don't collide on the shared static dictionary.</item>
///     <item><c>failTimes</c> — how many leading attempts return <c>Failure</c>. The first attempt
///           with <c>attempt &gt; failTimes</c> returns <c>Success</c>.</item>
///   </list></para>
///
/// <para>The counter is static because the node is a DI singleton and the engine re-invokes the
/// SAME instance on each retry within one run; keying by <c>key</c> keeps tests independent.</para>
/// </summary>
public sealed class FlakyTestNode : INodeRuntime
{
    public const string Key = "test.flaky";

    private static readonly ConcurrentDictionary<string, int> AttemptsByKey = new();

    /// <summary>Total RunAsync invocations seen for a key — lets a test assert the engine actually retried.</summary>
    public static int AttemptsFor(string key) => AttemptsByKey.GetValueOrDefault(key);

    public string TypeKey => Key;

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Flaky (test)",
        Category = "Test",
        Kind = NodeKind.Regular,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.EmptyObject(),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var key = ReadString(context.Config, "key");
        var failTimes = ReadInt(context.Config, "failTimes");
        var throwCategory = ReadString(context.Config, "throwCategory");   // ""=Fail-result; "auth-failed"/"transient"=THROW a typed LlmApiException

        var attempt = AttemptsByKey.AddOrUpdate(key, 1, (_, n) => n + 1);

        if (attempt <= failTimes)
        {
            // A typed THROW lets a test prove the engine's retry CLASSIFICATION: a non-retryable category fails fast
            // (one attempt), a retryable one retries. An empty throwCategory keeps the original Fail-result behaviour.
            if (throwCategory.Length > 0) throw TypedFault(throwCategory, attempt);

            return Task.FromResult(NodeResult.Fail($"flaky failure on attempt {attempt}"));
        }

        var outputs = new Dictionary<string, JsonElement> { ["attempts"] = JsonSerializer.SerializeToElement(attempt) };
        return Task.FromResult(NodeResult.Ok(outputs));
    }

    private static CodeSpace.Core.Services.Workflows.Llm.LlmApiException TypedFault(string category, int attempt) => category switch
    {
        "auth-failed" => new("Anthropic", 401, CodeSpace.Core.Services.Workflows.Llm.LlmErrorCategory.AuthFailed, $"auth failed on attempt {attempt}"),
        "transient" => new("Anthropic", 503, CodeSpace.Core.Services.Workflows.Llm.LlmErrorCategory.Transient, $"transient on attempt {attempt}"),
        _ => new("Anthropic", 400, CodeSpace.Core.Services.Workflows.Llm.LlmErrorCategory.BadRequest, $"bad request on attempt {attempt}"),
    };

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string name) =>
        bag.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> bag, string name) =>
        bag.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
