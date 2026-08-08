using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The acceptance-amendment co-sign surface (amend-acceptance arc, B1) — the FOURTH marker-carrying ask card,
/// sibling of <see cref="SupervisorPlanConfirmation"/> / <see cref="SupervisorApprovalRequest"/> /
/// <see cref="SupervisorGateEscalation"/>. When the decider proposes <c>amend_acceptance</c> (rewrite or waive ONE
/// subtask's oracle — the "broken oracle is the binding constraint" repair verb), the proposal does NOT execute:
/// the projector rewrites it into this <c>ask_human</c> card and the run parks on the standard Action wait — the
/// HUMAN co-signs an oracle change, because a run must never mark its own homework.
///
/// <para>The rewrite inherits the wait/token/fold/crash-repark machinery verbatim (zero fold-gate changes — the
/// MAJOR-7 ruling), and the card carries the STRUCTURED proposal as a root-level <c>amend</c> node beside the
/// question: the future co-sign overlay (B3) reads the exact spec back from the approved card, never re-parsing
/// prose. That node is server-attached ONLY — a model-authored <c>ask_human</c> payload cannot smuggle one, because
/// <see cref="Deciders.SupervisorModelDecision"/> binding erases undeclared fields, and the marker sentence itself
/// is stripped from model questions by <see cref="SupervisorAskQuestionClamp"/> (the FATAL-2 anti-minting pair).</para>
///
/// <para>B1 is INERT: the verb is absent from the model-facing decision schema, so no production path can reach
/// <see cref="IntoAskHuman"/> yet — this class exists so its shape is unit-pinned before the overlay (B3) and the
/// Waived verdict state (B2) build on it. An ANSWERED amend card already counts as engagement in the no-progress
/// fold (a human ruling on an oracle is not a stall), exactly like its three siblings.</para>
/// </summary>
public static class SupervisorAmendAcceptance
{
    /// <summary>The marker phrase EVERY amend card carries — the stable tail detectors match to recognise this card (vs the other three markers / a content ask). Pinned by a unit test so a reword is a visible decision.</summary>
    public const string AmendMarker = "Reply 'approve' to apply this acceptance amendment, or describe what to do instead.";

    /// <summary>Per-fragment cap on the quoted reason — the card is a headline, not the failure dossier (the failing grade's evidence lives on the tape).</summary>
    internal const int MaxQuotedChars = 200;

    /// <summary>Rewrite the model's amend proposal into the parked ask card: the question names the target subtask, the waive-vs-amend shape, the reason, and the proposed command; the structured payload rides beside it as the root <c>amend</c> node; the decision-level rationale is appended last, mirroring the projector's own rationale merge (deterministic order → replay-stable idempotency key).</summary>
    public static SupervisorDecision IntoAskHuman(SupervisorAmendAcceptancePayload payload, SupervisorRationale? rationale = null)
    {
        var ask = JsonSerializer.Serialize(new SupervisorAskHumanPayload { Question = QuestionFor(payload) }, AgentJson.Options);

        var root = JsonNode.Parse(ask)!.AsObject();
        root["amend"] = JsonSerializer.SerializeToNode(payload, AgentJson.Options);

        if (rationale is not null && (rationale.Why is not null || rationale.Evidence is not null))
            root["rationale"] = JsonSerializer.SerializeToNode(rationale, AgentJson.Options);

        return new()
        {
            Kind = SupervisorDecisionKinds.AskHuman,
            ServerAuthored = true,
            PayloadJson = root.ToJsonString(AgentJson.Options),
        };
    }

    /// <summary>The structured proposal a card carries, or null when the payload has no (object-valued) <c>amend</c> node or does not parse — never throws on tape bytes.</summary>
    public static SupervisorAmendAcceptancePayload? ReadAmend(string? askHumanPayloadJson)
    {
        if (string.IsNullOrEmpty(askHumanPayloadJson)) return null;

        try
        {
            var root = JsonDocument.Parse(askHumanPayloadJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("amend", out var amend) || amend.ValueKind != JsonValueKind.Object)
                return null;

            return amend.Deserialize<SupervisorAmendAcceptancePayload>(AgentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A decision is an amend card iff it is an ask_human whose question carries the marker AND whose payload carries a parseable structured proposal — BOTH, so neither a minted marker sentence (clamp-stripped anyway) nor a smuggled bare node (bind-erased anyway) can ever pass alone.</summary>
    public static bool IsAmendCard(SupervisorPriorDecision decision) =>
        decision.DecisionKind == SupervisorDecisionKinds.AskHuman && QuestionCarriesMarker(decision.PayloadJson) && ReadAmend(decision.PayloadJson) is not null;

    /// <summary>An ANSWERED amend card — the human RULED on the oracle proposal (approve or redirect). The no-progress fold counts it as engagement, exactly like the other three cards: the answer only exists once a resolved Action wait wrote it, so a counted card always cost a real human interaction.</summary>
    public static bool IsAnsweredAmendCard(SupervisorPriorDecision decision) =>
        IsAmendCard(decision) && SupervisorOutcome.ReadAskHumanAnswer(decision.OutcomeJson) != null;

    /// <summary>Whether an ask_human payload's question carries the amend marker — payload-level, so a content ask or another gate's card never matches.</summary>
    public static bool QuestionCarriesMarker(string? askHumanPayloadJson)
    {
        if (string.IsNullOrEmpty(askHumanPayloadJson)) return false;

        try
        {
            var root = JsonDocument.Parse(askHumanPayloadJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("question", out var q)
                && q.ValueKind == JsonValueKind.String && (q.GetString() ?? "").Contains(AmendMarker, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string QuestionFor(SupervisorAmendAcceptancePayload payload)
    {
        var question = new StringBuilder();

        question.Append(payload.Waive
            ? $"The supervisor proposes to WAIVE subtask '{payload.SubtaskId}'s acceptance check — the unit would proceed WITHOUT objective verification."
            : $"The supervisor proposes to AMEND subtask '{payload.SubtaskId}'s acceptance check.");

        if (!string.IsNullOrWhiteSpace(payload.Reason))
            question.Append($"\nReason: {Quote(payload.Reason)}");

        if (!payload.Waive && payload.Acceptance is not null)
            question.Append($"\nProposed check: {Quote(string.Join(" ", payload.Acceptance.Command))}");

        question.Append($"\n{AmendMarker}");

        return question.ToString();
    }

    private static string Quote(string text) => text.Length <= MaxQuotedChars ? text : text[..MaxQuotedChars] + "…";
}
