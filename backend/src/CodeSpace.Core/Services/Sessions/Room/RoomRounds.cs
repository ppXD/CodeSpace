using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// PURE segmentation of a supervisor decision tape into ROUNDS — a Plan and every decision up to the NEXT Plan, in
/// <c>Sequence</c> order (a re-plan opens a new round). Also translates each round's closing supervisor operation
/// (merge / resolve / ask-human) into friendly copy. Testable without a DbContext: the projector reads the tape, calls
/// <see cref="Segment"/>, and hands the result to the pure <see cref="RoomNarrative"/>. Spawns/retries produce a round's
/// agents; a stop produces the turn's final answer (not a round operation).
/// </summary>
public static class RoomRounds
{
    public static IReadOnlyList<RoomRound> Segment(IReadOnlyList<SupervisorDecisionRecord> tape)
    {
        var rounds = new List<RoomRound>();

        var index = 0;
        var subtasks = new List<string>();
        var agentIds = new List<Guid>();
        RoomOperation? op = null;

        void Flush()
        {
            if (index > 0) rounds.Add(new RoomRound { Index = index, Subtasks = subtasks, AgentRunIds = agentIds, Operation = op });
        }

        foreach (var d in tape)
        {
            if (d.DecisionKind == SupervisorDecisionKinds.Plan)
            {
                Flush();

                index++;
                subtasks = SupervisorOutcome.ReadPlanSubtasks(d.PayloadJson).Select(s => s.Title).ToList();
                agentIds = new List<Guid>();
                op = null;

                continue;
            }

            if (index == 0)   // defensive: a spawn / decision before any plan → open an implicit round 1
            {
                index = 1;
                subtasks = new List<string>();
                agentIds = new List<Guid>();
            }

            if (SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
                agentIds.AddRange(SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson));

            if (OperationFor(d) is { } o) op = o;
        }

        Flush();

        return rounds;
    }

    /// <summary>Translate a supervisor operation into a user-facing one-liner. Null for plan / spawn / retry / stop (spawns stage agents; a stop drives the final answer).</summary>
    public static RoomOperation? OperationFor(SupervisorDecisionRecord d) => d.DecisionKind switch
    {
        SupervisorDecisionKinds.Merge => MergeOp(d),
        SupervisorDecisionKinds.Resolve => ResolveOp(d),
        SupervisorDecisionKinds.AskHuman => AskOp(d),
        _ => null,
    };

    private static RoomOperation MergeOp(SupervisorDecisionRecord d)
    {
        var i = SupervisorOutcome.ReadIntegration(d.OutcomeJson);

        if (i is null) return new RoomOperation("merge", "Merging results", NarrativeTone.Info);

        if (i.IsConflicted) return new RoomOperation("merge", $"Resolving {Count(i.ConflictedFiles.Count, "conflicting file")}", NarrativeTone.Error);

        if (string.Equals(i.Status, "Clean", StringComparison.OrdinalIgnoreCase))
            return new RoomOperation("merge", i.IntegratedBranch is { Length: > 0 } b ? $"Merged results into {b}" : "Merged results", NarrativeTone.Success);

        return new RoomOperation("merge", "Merging results", NarrativeTone.Info);
    }

    private static RoomOperation ResolveOp(SupervisorDecisionRecord d) => SupervisorOutcome.ReadResolutionVerdict(d.OutcomeJson) switch
    {
        SupervisorResolutionVerdict.Verified => new RoomOperation("resolve", "Conflicts resolved", NarrativeTone.Success),
        SupervisorResolutionVerdict.Unverified => new RoomOperation("resolve", "Resolution needs review", NarrativeTone.Error),
        _ => new RoomOperation("resolve", "Reconciling conflicts", NarrativeTone.Info),
    };

    private static RoomOperation AskOp(SupervisorDecisionRecord d)
    {
        if (SupervisorOutcome.ReadAskHumanAnswer(d.OutcomeJson) is { Length: > 0 } answer)
            return new RoomOperation("ask_human", $"You answered: {Clip(answer)}", NarrativeTone.Info);

        var question = SupervisorOutcome.ReadAskHumanQuestion(d.OutcomeJson);
        return new RoomOperation("ask_human", question is { Length: > 0 } ? $"Asking you: {Clip(question)}" : "Waiting for your input", NarrativeTone.Info);
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Clip(string s) => s.Length <= 120 ? s : s[..119] + "…";
}
