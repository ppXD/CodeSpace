using System.Text.Json;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each flow.map PLAN beat with the subtasks its planner authored — the plan itself, read off the planner
/// producer's bounded output leaves (via <see cref="IWorkflowMapPlanObservationBundle"/>) and keyed by the plan event id
/// (<see cref="MapPlannerTimelineMap.EventId"/>, the same id the describer stamps on the beat), so the plan renders
/// inline under its own PLAN beat exactly like a supervisor plan — the causal spine plan → dispatch → agents. Read-only:
/// a workflow planner's plan is never up for confirmation, so the frontend renders it as a read-only card. A map whose
/// planner authored no subtasks contributes nothing.
/// </summary>
public sealed class MapPlannerFactsSource : IJournalFactsSource
{
    private readonly IWorkflowMapPlanObservationBundle _plans;

    public MapPlannerFactsSource(IWorkflowMapPlanObservationBundle plans)
    {
        _plans = plans;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _plans.GetAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        if (run == null || run.Availability != CodeSpace.Messages.Dtos.Workflows.WorkflowRunViewAvailability.Available) return EmptyFacts;

        var facts = new Dictionary<string, JournalStepFacts>();

        foreach (var planner in run.Planners)
        {
            var coverage = new List<JournalObservationCoverage>();
            IReadOnlyList<JournalSubtask>? subtasks = null;
            if (planner.SubtasksState == WorkflowMapPlanLeafState.Exact && planner.Subtasks is { } exact)
                subtasks = ReadSubtasks(exact);
            else if (Incomplete(planner.SubtasksState))
                coverage.Add(Coverage(planner, JournalObservationCoverageSourceKinds.MapPlanSubtasks, planner.SubtasksState));

            JournalModelCall? modelCall = null;
            if (planner.ModelUsageState == WorkflowMapPlanLeafState.Exact && planner.ModelOutputs is { } model)
                modelCall = ModelCallFromOutputs(model);
            else if (Incomplete(planner.ModelUsageState))
                coverage.Add(Coverage(planner, JournalObservationCoverageSourceKinds.MapPlanModelUsage, planner.ModelUsageState));

            if ((subtasks?.Count ?? 0) > 0 || modelCall is not null || coverage.Count > 0)
                facts[MapPlannerTimelineMap.EventId(planner.ProducerNodeId)] = new JournalStepFacts
                {
                    Plan = subtasks,
                    ModelCall = modelCall,
                    ObservationCoverage = coverage.Count == 0 ? null : coverage,
                };
        }

        return facts;
    }

    private static bool Incomplete(WorkflowMapPlanLeafState state) => state is WorkflowMapPlanLeafState.Truncated or WorkflowMapPlanLeafState.Invalid or WorkflowMapPlanLeafState.Unavailable;

    private static JournalObservationCoverage Coverage(WorkflowMapPlannerObservation planner, string sourceKind, WorkflowMapPlanLeafState state) => new()
    {
        SourceKind = sourceKind,
        Reason = state switch
        {
            WorkflowMapPlanLeafState.Truncated => JournalObservationCoverageReason.TruncatedLeaf,
            WorkflowMapPlanLeafState.Invalid => JournalObservationCoverageReason.InvalidLeaf,
            _ => JournalObservationCoverageReason.UnavailableLeaf,
        },
        ObservedCount = 0,
        OmittedCount = sourceKind == JournalObservationCoverageSourceKinds.MapPlanSubtasks ? planner.SubtasksTotalCount : 1,
        OmittedCountIsLowerBound = false,
        DecisionId = planner.StateRecordId,
        StoryOrder = planner.StateRecordSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>The planner's authoring model call, read off the planner node's OWN outputs (model · input/output tokens · cost). A flow.map planner stamps these on its output whether or not a separate interaction record was written, so the plan beat can always show what authored it. Cost prefers the recorded value, else the shared pricing (fail-open null on an unpriced model). Null when the outputs name no model.</summary>
    internal static JournalModelCall? ModelCallFromOutputs(JsonElement outputs)
    {
        if (outputs.ValueKind != JsonValueKind.Object) return null;

        var model = ReadString(outputs, "model");

        if (string.IsNullOrWhiteSpace(model)) return null;

        var input = ReadInt(outputs, "inputTokens");
        var output = ReadInt(outputs, "outputTokens");
        var tokens = input is null && output is null ? (int?)null : (input ?? 0) + (output ?? 0);

        return new JournalModelCall
        {
            Purpose = "plan.author",
            Model = model,
            InputTokens = input,
            OutputTokens = output,
            Tokens = tokens,
            LatencyMs = null,
            CostUsd = ReadDecimal(outputs, "costUsd") ?? (tokens is null ? null : AgentCostPricing.CostUsd(model, input ?? 0, output ?? 0)),
            Status = "completed",
        };
    }

    private static int? ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static decimal? ReadDecimal(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    /// <summary>
    /// Project each subtask to the bare <see cref="JournalSubtask"/> — the SAME shape the supervisor plan facts source
    /// produces, so both render through one inline-plan path. A map's items come in TWO generic shapes: an OBJECT subtask
    /// (a <c>plan.author</c> plan — camelCase <c>id</c> + <c>title</c>) OR a plain STRING (a simpler planner whose
    /// <c>json.subtasks</c> is a string array). A string carries no id, so its id is the positional index and the string
    /// itself is the title. A non-object / non-string element is skipped. Uses the SAME numbering the timeline beat counts.
    /// </summary>
    internal static IReadOnlyList<JournalSubtask> ReadSubtasks(JsonElement subtasks) =>
        subtasks.EnumerateArray()
            .Select((e, i) => e.ValueKind switch
            {
                JsonValueKind.Object => new JournalSubtask { SubtaskId = ReadString(e, "id"), Title = ReadString(e, "title") },
                JsonValueKind.String => new JournalSubtask { SubtaskId = $"item-{i}", Title = e.GetString() ?? "" },
                _ => null,
            })
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

    private static string ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static readonly IReadOnlyDictionary<string, JournalStepFacts> EmptyFacts = new Dictionary<string, JournalStepFacts>();
}
