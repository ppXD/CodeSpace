using System.Collections.Concurrent;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// Test-only node that PROVES intra-run parallelism is real (not just allowed). Each instance, on
/// entry, joins a shared per-gate barrier (a <see cref="CountdownEvent"/> sized to the expected party
/// count) and blocks until EVERY expected party has arrived. If the engine ran the ready frontier
/// sequentially, the first party would block forever waiting for siblings that can't start — so a
/// barrier that releases WITHOUT timing out is hard proof the nodes executed concurrently. It also
/// records the PEAK number simultaneously in-flight + which parties arrived, for explicit assertions.
///
/// <para>Each test arms a unique gate via <see cref="Arm"/> (party count = the number of parallel
/// nodes) BEFORE running the engine. Registered as an <c>INodeRuntime</c> in
/// <c>PostgresFixture</c> (engine + validator accept "test.concurrency_probe"); it is NOT in any
/// plugin module, so it never reaches the editor palette. Assumes the engine's max-parallelism
/// (default 8) is ≥ the party count — true for every gate these tests arm.</para>
/// </summary>
public sealed class ConcurrencyProbeNode : INodeRuntime
{
    public const string Key = "test.concurrency_probe";

    private sealed class Gate
    {
        public required CountdownEvent Countdown { get; init; }
        public int Current;
        public int Peak;
        public volatile bool TimedOut;
        public ConcurrentBag<string> Arrived { get; } = new();
    }

    private static readonly ConcurrentDictionary<string, Gate> Gates = new();

    /// <summary>Arm a gate before a run: <paramref name="parties"/> = how many nodes are expected to run concurrently through it.</summary>
    public static void Arm(string gate, int parties) => Gates[gate] = new Gate { Countdown = new CountdownEvent(parties) };

    /// <summary>The peak number of parties simultaneously in-flight (== parties when they all overlapped).</summary>
    public static int Peak(string gate) => Gates.TryGetValue(gate, out var g) ? g.Peak : 0;

    /// <summary>True iff every party reached the barrier together (no party timed out waiting) — i.e. they ran concurrently.</summary>
    public static bool AllArrived(string gate) => Gates.TryGetValue(gate, out var g) && !g.TimedOut;

    /// <summary>The party labels that entered the node, in arrival order (a bag — order is best-effort).</summary>
    public static IReadOnlyCollection<string> ArrivedParties(string gate) => Gates.TryGetValue(gate, out var g) ? g.Arrived.ToArray() : Array.Empty<string>();

    public string TypeKey => Key;

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Concurrency probe (test)",
        Category = "Test",
        Kind = NodeKind.Regular,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.EmptyObject(),
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var gateKey = ReadString(context.Inputs, "gate");
        var party = ReadString(context.Inputs, "party");
        var gate = Gates[gateKey];   // armed by the test before the run

        gate.Arrived.Add(party);
        UpdatePeak(gate, Interlocked.Increment(ref gate.Current));
        try
        {
            if (ReadBool(context.Inputs, "peak"))
            {
                // Peak-gauge mode (no barrier): a brief hold gives concurrent siblings a window to
                // overlap, so Peak reflects the real simultaneity the semaphore allowed. Deterministic
                // for a cap of 1 (the semaphore physically forbids a second concurrent RunAsync), and
                // fast (no timeout) — used to PROVE a per-loop maxParallelism throttle.
                await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Barrier mode: block until ALL expected parties are simultaneously in-flight. A
                // sequential walk could never get the others here, so Wait times out — flipping
                // TimedOut so AllArrived() reports false.
                gate.Countdown.Signal();
                if (!gate.Countdown.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                    gate.TimedOut = true;
            }
        }
        finally
        {
            Interlocked.Decrement(ref gate.Current);
        }

        var outputs = new Dictionary<string, JsonElement> { ["party"] = JsonSerializer.SerializeToElement(party) };
        return NodeResult.Ok(outputs);
    }

    /// <summary>Lock-free <c>Peak = max(Peak, observed)</c>.</summary>
    private static void UpdatePeak(Gate gate, int observed)
    {
        int peak;
        while (observed > (peak = gate.Peak) && Interlocked.CompareExchange(ref gate.Peak, observed, peak) != peak) { }
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string name) =>
        bag.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool ReadBool(IReadOnlyDictionary<string, JsonElement> bag, string name) =>
        bag.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.True;
}
