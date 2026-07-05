using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// PURE segmentation of a supervisor decision tape into ROUNDS — a Plan and every decision up to the NEXT Plan, in
/// <c>Sequence</c> order (a re-plan opens a new round). Testable without a DbContext: the projector reads the tape, calls
/// <see cref="Segment"/>, and hands the result to the pure <see cref="RoomNarrative"/>. Spawns/retries produce a round's
/// agents (the retained Agent-group blocks); a stop produces the turn's final answer.
/// </summary>
public static class RoomRounds
{
    public static IReadOnlyList<RoomRound> Segment(IReadOnlyList<SupervisorDecisionRecord> tape)
    {
        var rounds = new List<RoomRound>();

        var index = 0;
        var subtasks = new List<string>();
        var agentIds = new List<Guid>();

        void Flush()
        {
            if (index > 0) rounds.Add(new RoomRound { Index = index, Subtasks = subtasks, AgentRunIds = agentIds });
        }

        foreach (var d in tape)
        {
            if (d.DecisionKind == SupervisorDecisionKinds.Plan)
            {
                Flush();

                index++;
                subtasks = SupervisorOutcome.ReadPlanSubtasks(d.PayloadJson).Select(s => s.Title).ToList();
                agentIds = new List<Guid>();

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
        }

        Flush();

        return rounds;
    }
}
