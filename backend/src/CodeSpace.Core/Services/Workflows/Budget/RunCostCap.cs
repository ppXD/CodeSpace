using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Workflows.Budget;

/// <summary>
/// The ONE reader of a workflow run's own cost ceiling: the launch-stamped route provenance
/// (<see cref="WorkflowRun.RoutePlanJson"/> → <c>Caps.MaxCostUsd</c>, written by <c>TaskRunSnapshotFactory</c>).
/// It exists so the engine's per-node model-call scope and the Room's budget block read the run's cap from the
/// same column with the same options — a run the Room DISPLAYS as capped must be a run whose model calls were
/// actually admitted against that cap, and two readers of one JSON column is exactly how that stops being true.
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
        if (string.IsNullOrWhiteSpace(routePlanJson)) return null;

        try
        {
            var cap = JsonSerializer.Deserialize<Messages.Tasks.RoutePlan>(routePlanJson, RouteJson)?.Caps.MaxCostUsd;

            return cap is > 0 ? cap : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
