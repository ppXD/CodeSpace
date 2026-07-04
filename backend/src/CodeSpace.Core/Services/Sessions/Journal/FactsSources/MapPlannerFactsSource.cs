using System.Text.Json;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each flow.map PLAN beat with the subtasks its planner authored — the plan itself, read off the planner
/// node's outputs (via the shared <see cref="MapPlan"/>) and keyed by the plan event id
/// (<see cref="MapPlannerTimelineMap.EventId"/>, the same id the describer stamps on the beat), so the plan renders
/// inline under its own PLAN beat exactly like a supervisor plan — the causal spine plan → dispatch → agents. Read-only:
/// a workflow planner's plan is never up for confirmation, so the frontend renders it as a read-only card. A map whose
/// planner authored no subtasks contributes nothing.
/// </summary>
public sealed class MapPlannerFactsSource : IJournalFactsSource
{
    private readonly IWorkflowService _workflows;

    public MapPlannerFactsSource(IWorkflowService workflows)
    {
        _workflows = workflows;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _workflows.GetRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        if (run == null) return EmptyFacts;

        var facts = new Dictionary<string, JournalStepFacts>();

        foreach (var planner in MapPlan.PlannersOf(run))
        {
            var subtasks = ReadSubtasks(planner.Subtasks);

            if (subtasks.Count > 0)
                facts[MapPlannerTimelineMap.EventId(planner.Producer.NodeId)] = new JournalStepFacts { Plan = subtasks };
        }

        return facts;
    }

    /// <summary>Project each subtask object (camelCase <c>id</c> + <c>title</c>) to the bare <see cref="JournalSubtask"/> — the SAME shape the supervisor plan facts source produces, so both render through one inline-plan path.</summary>
    private static IReadOnlyList<JournalSubtask> ReadSubtasks(JsonElement subtasks) =>
        subtasks.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e => new JournalSubtask
            {
                SubtaskId = ReadString(e, "id"),
                Title = ReadString(e, "title"),
            })
            .ToList();

    private static string ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static readonly IReadOnlyDictionary<string, JournalStepFacts> EmptyFacts = new Dictionary<string, JournalStepFacts>();
}
