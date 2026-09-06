using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The ONE adjudication surface both server stop gates park and release on — <see cref="SupervisorDeliveryGate"/>
/// (DC-2b) and <see cref="SupervisorPublishGate"/> (I3). A gate's card records WHAT it asked about as a structured
/// <see cref="SupervisorDeliveryGateReason"/> beside its question, and a later turn releases when the newest
/// attempt reports that SAME blocker — never on where the answer sits relative to work the brain did afterwards.
///
/// <para>Shared rather than mirrored per gate, deliberately: the two gates sit one rung apart on the same stop and
/// a run under an immutable policy hits BOTH. If "whose card is this", "what did it record" and "is this the same
/// blocker" were written twice, one gate's release could recognize a card the other's mint no longer writes — the
/// two would dead-end each other exactly as the state-change clamp dead-ended the delivery gate alone (live run
/// 34001620515). Each gate still owns its own <c>QuestionPrefix</c>, so a card only ever releases ITS OWN gate.</para>
///
/// Pure + stateless over the replayed tape — a re-entry re-derives the identical answer.
/// </summary>
public static class SupervisorGateAdjudication
{
    /// <summary>The root key the structured blocker rides under, beside the card's question. Durable tape bytes a later turn reads back — renaming it silently stops every in-flight parked run's release from recognizing its own card, so it is test-pinned.</summary>
    internal const string ReasonNode = "gateReason";

    /// <summary>
    /// The parked card: the human-readable question under <paramref name="questionPrefix"/> (the minting gate's
    /// identity on the tape), plus the STRUCTURED blocker it adjudicates as a root <see cref="ReasonNode"/> node
    /// beside it — the same server-attached shape <c>SupervisorAmendAcceptance</c> uses; a model-authored ask can
    /// never smuggle one, because binding erases undeclared fields. Written IN PLACE onto the canonical payload so
    /// the record stays the single source of the question's own serialization.
    /// </summary>
    public static SupervisorDecision IntoAskHuman(string questionPrefix, SupervisorDeliveryGateReason reason, string detail)
    {
        var root = JsonNode.Parse(JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = $"{questionPrefix}{detail}" }, AgentJson.Options))!.AsObject();

        root[ReasonNode] = JsonSerializer.SerializeToNode(reason, AgentJson.Options);

        return new SupervisorDecision
        {
            Kind = SupervisorDecisionKinds.AskHuman,
            ServerAuthored = true,
            PayloadJson = root.ToJsonString(AgentJson.Options),
        };
    }

    /// <summary>
    /// Whether one of <paramref name="questionPrefix"/>'s own cards was ANSWERED at a sequence in
    /// (<paramref name="after"/>, <paramref name="before"/>) — the POSITIONAL read, which the rungs that genuinely
    /// turn on tape position use: the re-arm (an answer AFTER the latest attempt buys exactly ONE fresh
    /// server-authored re-attempt) and the delivery gate's UNAUTHORIZED park (no attempt exists to anchor on at
    /// all). The RELEASE rung does not use it — see <see cref="AdjudicatedSameBlocker"/> for why position cannot
    /// decide that one.
    /// </summary>
    public static bool AnsweredCardExists(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string questionPrefix, long after, long before) =>
        priorDecisions.Any(d => d.Sequence > after && d.Sequence < before && IsAnsweredCard(d, questionPrefix));

    /// <summary>
    /// Whether a human ALREADY ANSWERED one of <paramref name="questionPrefix"/>'s cards for the SAME blocker as
    /// <paramref name="reason"/>, at any point before <paramref name="before"/> (the re-check attempt) — the
    /// adjudication release both gates key on. Deliberately UNBOUNDED below: a spawn or merge landing after the
    /// answer does not un-ask the question the human already ruled on, and the rung above has already forced a
    /// fresh attempt for that new work, so the verdict being released is never a stale one.
    /// </summary>
    public static bool AdjudicatedSameBlocker(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string questionPrefix, SupervisorDeliveryGateReason reason, long before) =>
        AnsweredCardBlockers(priorDecisions, questionPrefix, before).Any(adjudicated => SupervisorDeliveryGateReason.SameBlocker(adjudicated, reason));

    /// <summary>
    /// Every blocker a human ANSWERED one of <paramref name="questionPrefix"/>'s cards about, before
    /// <paramref name="before"/> — the raw adjudication record, for readers that ask a coarser question than
    /// "the same blocker" (the completion trace asks only WHETHER a policy skip was ever ruled on). A card that
    /// recorded no blocker yields null, so a reader can never mistake "unknowable" for a match.
    /// </summary>
    public static IEnumerable<SupervisorDeliveryGateReason?> AnsweredCardBlockers(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string questionPrefix, long before = long.MaxValue) =>
        priorDecisions.Where(d => d.Sequence < before && IsAnsweredCard(d, questionPrefix)).Select(d => ReadReason(d.PayloadJson));

    /// <summary>One of <paramref name="questionPrefix"/>'s own cards that a human ANSWERED — the shared predicate the positional re-arm and the blocker-identity release both key on, so "whose card is this" can never drift between them.</summary>
    private static bool IsAnsweredCard(SupervisorPriorDecision decision, string questionPrefix) =>
        decision.DecisionKind == SupervisorDecisionKinds.AskHuman
        && ReadQuestion(decision.PayloadJson)?.StartsWith(questionPrefix, StringComparison.Ordinal) == true
        && SupervisorOutcome.ReadAskHumanAnswer(decision.OutcomeJson) is not null;

    private static string? ReadQuestion(string? payloadJson)
    {
        if (payloadJson is null) return null;

        try { return JsonSerializer.Deserialize<SupervisorAskHumanPayload>(payloadJson, AgentJson.Options)?.Question; }
        catch (JsonException) { return null; }
    }

    /// <summary>The blocker a card recorded, or null when the payload carries no (object-valued) <see cref="ReasonNode"/> node, it does not parse, or it names no <c>kind</c> — never throws on tape bytes (mirrors <c>SupervisorAmendAcceptance.ReadAmend</c>).</summary>
    private static SupervisorDeliveryGateReason? ReadReason(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson)) return null;

        try
        {
            var root = JsonDocument.Parse(payloadJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(ReasonNode, out var reason) || reason.ValueKind != JsonValueKind.Object)
                return null;

            return reason.Deserialize<SupervisorDeliveryGateReason>(AgentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
