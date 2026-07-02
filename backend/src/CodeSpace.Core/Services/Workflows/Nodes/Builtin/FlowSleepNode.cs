using System.Text.Json;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Pauses the workflow for a fixed delay, then resumes. The first execution returns
/// <c>Suspend</c> with a Timer token (wake_at = now + seconds); the engine parks the run as
/// Suspended and schedules a resume at wake_at. When the timer fires the engine re-runs this
/// node — now with a <c>ResumePayload</c> — and it returns Success so the walk continues.
///
/// The simplest possible suspend/resume node, and the proof that the mechanism works
/// end-to-end. Approval / callback wait nodes (Phase 1.2) follow the same shape with a
/// different wait kind.
/// </summary>
public sealed class FlowSleepNode : INodeRuntime
{
    public string TypeKey => "flow.sleep";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Sleep / delay",
        Category = "Logic",
        Kind = NodeKind.Regular,
        CanSuspend = true,
        // D3: rerunnable as a from-node ROOT — the last CanSuspend node whose re-execution is a clean re-stage.
        // The "external run" a re-stage mints is just the engine's OWN Timer wait, keyed to the fork's run id +
        // a fresh wait id and self-woken by the scheduled ResumeWaitAsync (no external party re-issues anything,
        // unlike Approval/Callback/Action/Decision, which strand). Re-executing on the fork parks a fresh timer;
        // the original run's wait is untouched. A sleep has no side effects, so it is not side-effecting here.
        IsRerunnableWhenSuspendable = true,
        IconKey = "clock",
        Description = "Pauses the workflow for a fixed delay, then resumes. The run shows as Suspended while waiting.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "seconds": { "type": "integer", "minimum": 1, "description": "How long to pause, in seconds." }
              },
              "required": ["seconds"]
            }
            """),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.EmptyObject(),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Resumed pass: the timer fired and the engine re-ran us with the resolved payload.
        if (context.ResumePayload.HasValue)
        {
            context.Logger.LogInformation("flow.sleep resumed");
            return Task.FromResult(NodeResult.Ok());
        }

        // First pass: validate the delay + park the run with a Timer token.
        var seconds = ReadInt(context.Config, "seconds");
        if (seconds <= 0)
            return Task.FromResult(NodeResult.Fail("Config 'seconds' must be a positive integer."));

        var wakeAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        var payload = JsonSerializer.SerializeToElement(new { wake_at = wakeAt.ToString("o"), seconds });

        context.Logger.LogInformation("flow.sleep suspending for {Seconds}s (wake_at={WakeAt:o})", seconds, wakeAt);
        return Task.FromResult(NodeResult.Suspend(new SuspensionToken { Kind = WorkflowWaitKinds.Timer, Payload = payload }));
    }

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> bag, string key)
    {
        if (!bag.TryGetValue(key, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)) return parsed;
        return 0;
    }
}
