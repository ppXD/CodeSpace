using System.Collections.Concurrent;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Test-only SUSPENDING body node for the flow.map durable-resume (PR2) tests — the hermetic stand-in for
/// an <c>agent.run</c> that parks to an AgentRun. It mirrors the loop parallel-suspend fixture
/// (<c>flow.wait_approval</c> in LoopFlowTests) but parks an <b>Action</b> wait, which needs NO external
/// staging (no child run / agent run), so the test stays self-contained while still committing a REAL
/// <c>WorkflowRunWait</c> row and resuming through the REAL <see cref="WorkflowResumeService"/> path:
///
///   1. FIRST pass: records (thread-safely, keyed) that THIS element-branch's node ran exactly once, then
///      returns <c>Suspend</c> with an Action token whose <c>CorrelationToken</c> embeds the element value,
///      so the test can locate this branch's wait by token. The engine commits the wait under the branch's
///      iteration key <c>"&lt;mapId&gt;#&lt;i&gt;"</c> — K parking branches ⇒ K independent wait rows.
///   2. RESUMED pass (a resolved wait injected its payload): emits the per-element result — the element it
///      saw plus the resume payload's <c>summary</c> (the simulated agent output) — which the branch
///      terminal surfaces as <c>results[i]</c>.
///
/// The per-key ran-count is the exactly-once-per-branch probe: a completed branch must NOT re-run its
/// FIRST pass when a SIBLING branch's wait resolves and triggers a re-walk (the #306 property for map).
/// Registered into the integration container via <c>PostgresFixture.RegisterTestAssemblyTypes</c> as an
/// <c>INodeRuntime</c>; NOT in any IPluginModule, so it never reaches the editor palette.
/// </summary>
public sealed class SuspendProbeNode : INodeRuntime
{
    public const string Key = "test.suspend_probe";

    // key → how many times the node's FIRST (suspending) pass ran. A re-walk that re-runs a completed
    // branch would bump this past 1 for that branch — exactly the corruption the exactly-once test guards.
    private static readonly ConcurrentDictionary<string, int> FirstPassCountByElement = new();

    /// <summary>How many times the suspending first pass ran for a given (test-key, element) — must be 1 per completed branch.</summary>
    public static int FirstPassCount(string key, string element) => FirstPassCountByElement.GetValueOrDefault($"{key}::{element}", 0);

    /// <summary>Clear all recorded counts for a test key before a run so a re-run doesn't accumulate.</summary>
    public static void Reset(string key)
    {
        foreach (var k in FirstPassCountByElement.Keys.Where(k => k.StartsWith($"{key}::", StringComparison.Ordinal)).ToList())
            FirstPassCountByElement.TryRemove(k, out _);
    }

    public string TypeKey => Key;

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Suspend probe (test)",
        Category = "Test",
        Kind = NodeKind.Regular,
        CanSuspend = true,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""{ "type": "object", "additionalProperties": true }"""),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var key = ReadString(context.Inputs, "key");
        var element = ReadString(context.Inputs, "item");
        var boom = ReadString(context.Inputs, "boom");

        // Resumed pass: a wait resolved + injected its payload. Echo the element + the resolved summary.
        if (context.ResumePayload.HasValue)
        {
            var outputs = new Dictionary<string, JsonElement>
            {
                ["item"] = JsonSerializer.SerializeToElement(element),
                ["summary"] = ReadOr(context.ResumePayload.Value, "summary", JsonSerializer.SerializeToElement("")),
            };
            return Task.FromResult(NodeResult.Ok(outputs));
        }

        // Boom element: count the pass (so a test can assert exactly-once on the FAILING branch too) + FAIL —
        // the abandon point for a continue-mode map. A sibling-triggered re-walk that re-ran this would bump
        // the count past 1, re-firing the (here, just-counted) side effect.
        if (boom.Length > 0 && element == boom)
        {
            FirstPassCountByElement.AddOrUpdate($"{key}::{element}", 1, (_, n) => n + 1);
            return Task.FromResult(NodeResult.Fail($"boom on element '{element}'"));
        }

        // First pass: count it (exactly-once probe) + park an Action wait. The correlation token embeds the
        // element so a test can find THIS branch's wait by token; the engine scopes the wait row to the
        // branch's iteration key, so K branches park K independent waits.
        FirstPassCountByElement.AddOrUpdate($"{key}::{element}", 1, (_, n) => n + 1);

        var token = new SuspensionToken
        {
            Kind = WorkflowWaitKinds.Action,
            Payload = JsonSerializer.SerializeToElement(new { element }),
            CorrelationToken = $"{key}::{element}",
        };
        return Task.FromResult(NodeResult.Suspend(token));
    }

    private static JsonElement ReadOr(JsonElement obj, string name, JsonElement fallback) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v.Clone() : fallback;

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string name) =>
        bag.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
