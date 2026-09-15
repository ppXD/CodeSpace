using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Workflows.Budget;

/// <summary>
/// The reader every ADMISSION decision takes a workflow run's own cost ceiling from: the launch-stamped route
/// provenance (<see cref="WorkflowRun.RoutePlanJson"/> → <c>Caps.MaxCostUsd</c>, written by
/// <c>TaskRunSnapshotFactory</c>). The engine's per-node model-call scope and the agent executor's output-review
/// critic both admit through it, so a run the Room DISPLAYS as capped is a run whose model calls were actually
/// admitted against that same cap.
///
/// <para>It is NOT yet the only reader of the column. <c>RoomProjector</c> deserializes the same
/// <c>RoutePlan</c> itself at three sites (the runs-list route rows, the run's budget block, the cap probe) — it
/// needs the whole route, not just the cap — and <c>TaskLaunchBenchmarkCellRunner.Drive</c> reads it with
/// <c>WorkflowJson.Options</c> rather than the Web options this column was written with. Folding those onto this
/// reader is a separate change; what is guaranteed here is only that no CAP is enforced from a second parse.</para>
///
/// <para>A run with no route (a manual / trigger / subworkflow run) has no cap at this layer and returns null —
/// its model calls record Unbudgeted rather than being metered against a ceiling nobody declared. A malformed or
/// legacy column degrades to null the same way, never to a fabricated cap: guessing one would REFUSE calls an
/// operator never bounded.</para>
/// </summary>
public static class RunCostCap
{
    // The SAME web options TaskRunSnapshotFactory wrote the column with (and RoomProjector reads it back with).
    private static readonly JsonSerializerOptions RouteJson = new(JsonSerializerDefaults.Web);

    /// <summary>The run's declared cost ceiling, or null when it declares none.</summary>
    public static decimal? Of(WorkflowRun run) => Of(run.RoutePlanJson);

    /// <summary>The cost ceiling stamped in a run's route provenance JSON, or null when absent / unreadable / non-positive.</summary>
    public static decimal? Of(string? routePlanJson)
    {
        var cap = Read(routePlanJson)?.Caps.MaxCostUsd;

        return cap is > 0 ? cap : null;
    }

    /// <summary>
    /// Whether this route's projection makes ONE agent run the SOLE claimant of the run's ceiling — the single-agent
    /// (quick) lane, where the agent IS the run and may therefore admit against the whole cap.
    ///
    /// <para>Every other projection fans work out and admits it at the FAN-OUT's own grain BEFORE any agent run
    /// exists: <c>WorkflowEngine.AdmitBranchAsync</c> reserves cap÷N per map branch, and
    /// <c>RealSupervisorActionExecutor</c> reserves one <c>agent-attempt</c> per staged agent. An agent under those
    /// must not claim the ceiling a second time — the money is already claimed on its behalf, so a second claim
    /// would refuse its own siblings, and on a map whose branches already sum to the whole cap it would refuse
    /// EVERY agent the run staged. Those agents still RECORD their spend; they just record it unbudgeted.</para>
    ///
    /// <para>A run with no route (manual / trigger / subworkflow) returns false the same way a malformed column
    /// does: nobody declared a ceiling, so nothing here may enforce one.</para>
    /// </summary>
    public static bool AgentOwnsTheRunCap(string? routePlanJson) =>
        Read(routePlanJson)?.ProjectionKind == Messages.Tasks.TaskProjectionKinds.SingleAgent;

    /// <summary>The one parse every reader above shares. A malformed / legacy column degrades to null, never to a fabricated route.</summary>
    private static Messages.Tasks.RoutePlan? Read(string? routePlanJson)
    {
        if (string.IsNullOrWhiteSpace(routePlanJson)) return null;

        try { return JsonSerializer.Deserialize<Messages.Tasks.RoutePlan>(routePlanJson, RouteJson); }
        catch (JsonException) { return null; }
    }
}
