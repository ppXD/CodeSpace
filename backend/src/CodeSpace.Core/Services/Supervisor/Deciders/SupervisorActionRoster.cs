using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// The turn's VERB ROSTER: the vocabulary block the model chooses its <c>kind</c> from, rendered from
/// <see cref="SupervisorActionMask"/> so an offered verb and a masked verb can never be the same verb.
///
/// <para>It exists because the roster used to be a sentence in the turn-invariant SYSTEM prompt — seven verbs with
/// their meanings, listed identically on every turn — while the mask sat in the user prompt naming the one the
/// server would refuse. One prompt, two rosters, and the model picks whichever it read last: golden scenario
/// <c>resolve-cap-spent</c> (a conflicted merge whose resolve budget is spent; accepted answers {stop, ask_human})
/// passed four main runs in a row on the gating Anthropic wire and then failed 2 of 4 branch lanes — once choosing
/// <c>resolve</c>, the verb the mask three lines below forbade, and once <c>merge</c>, the merge that had already
/// conflicted. #1795 established the rule this block generalises: never NAME a masked verb. A roster that lists
/// every verb regardless of the mask names all of them, every turn.</para>
///
/// <para>The offered half and the withheld half are read from ONE table and ONE availability reader, so they cannot
/// drift into offering and forbidding the same verb — the shape of the miss above. The withheld half is
/// <see cref="SupervisorActionMask.Render"/>'s own output, byte for byte, rather than a second rendering of the
/// same facts: the reasons a verb is unavailable stay authored in exactly one place.</para>
///
/// <para>The vocabulary is the decision schema's <c>kind</c> enum — all EIGHT verbs, not the seven the rails used
/// to name. A block whose header says "the decision 'kind' values this turn accepts" and then omits a verb the
/// server binds is the same defect from the other side: golden <c>amended-oracle-discarded-by-replan</c> is graded
/// on <c>amend_acceptance</c>, and the nearest, loudest claim of exhaustiveness told the model it did not exist.
/// Its availability is a server verdict like <c>resolve</c>'s — <see cref="SupervisorAmendPrecondition"/> refuses a
/// proposal against a unit whose check RAN — so it is offered where some unit is genuinely amendable and named as
/// withheld, with the reason, everywhere else.</para>
/// </summary>
public static class SupervisorActionRoster
{
    /// <summary>The block's pinned header — a stable prompt landmark, mirroring <see cref="SupervisorActionMask.Header"/> and the bounds/budget recitations.</summary>
    public const string Header = "ACTIONS AVAILABLE THIS TURN (the decision 'kind' values this turn accepts — choose exactly one):";

    /// <summary>The generic pointer the SYSTEM prompt carries in place of the roster it used to hold. Turn-invariant by construction — it names no verb, so no turn can contradict it.</summary>
    public const string SystemPromptPointer = "The turn's AVAILABLE ACTIONS block lists the verbs you may emit THIS turn, with what each one does; a verb the same block reports as unavailable is refused by the server and cannot advance the run, so never choose one.";

    /// <summary>The vocabulary and each verb's one-line meaning — the SINGLE table both halves of the block read, so a verb can be offered or withheld but never both.</summary>
    private static readonly (string Verb, string Meaning)[] Vocabulary =
    [
        (SupervisorDecisionKinds.Plan, "decompose the goal into subtasks"),
        (SupervisorDecisionKinds.Spawn, "fan out coding agents over planned subtask ids"),
        (SupervisorDecisionKinds.Retry, "re-run one subtask"),
        (SupervisorDecisionKinds.Merge, "synthesize the agents' results"),
        (SupervisorDecisionKinds.Resolve, "reconcile a CONFLICTED integration — the server spawns ONE reconciling agent from the recorded conflict; you name no subtask and author no branches"),
        (SupervisorDecisionKinds.AmendAcceptance, "propose to rewrite or waive ONE subtask's acceptance check when the CHECK ITSELF could not run — it parks for a human co-sign; never retry into a check that cannot pass"),
        (SupervisorDecisionKinds.AskHuman, "ask a question"),
        (SupervisorDecisionKinds.Stop, "finish"),
    ];

    /// <summary>Render the block: the verbs this turn offers, then the mask's own withheld half when one applies. Never null — a turn the model must answer always has an offerable verb (<c>plan</c>, <c>ask_human</c> and <c>stop</c> are the escape hatches the mask may never take away).</summary>
    public static string Render(SupervisorTurnContext context)
    {
        var builder = new StringBuilder(Header);

        foreach (var verb in Offerable(context)) builder.Append('\n').Append("- ").Append(verb).Append(" — ").Append(MeaningFor(verb));

        return SupervisorActionMask.Render(context) is { } withheld ? $"{builder}\n{withheld}" : builder.ToString();
    }

    /// <summary>The verbs this turn may emit, in vocabulary order — every verb the mask has no reason to withhold.</summary>
    internal static IReadOnlyList<string> Offerable(SupervisorTurnContext context) =>
        Vocabulary.Where(v => UnavailableReasonFor(v.Verb, context) is null).Select(v => v.Verb).ToList();

    /// <summary>The verbs this turn withholds, in vocabulary order — the complement of <see cref="Offerable"/> over the SAME reader, so the two partition the vocabulary by construction.</summary>
    internal static IReadOnlyList<string> Withheld(SupervisorTurnContext context) =>
        Vocabulary.Where(v => UnavailableReasonFor(v.Verb, context) is not null).Select(v => v.Verb).ToList();

    /// <summary>Why this verb cannot advance the run this turn, else null. The ONLY availability authority the roster consults — <see cref="SupervisorActionMask"/>'s own per-verb reader — so the menu and the withheld half beneath it answer from one source. Every other verb is unmasked BY DESIGN, and the mask's own summary documents why: the escape hatches must always be reachable, while staging is left to model judgement.</summary>
    private static string? UnavailableReasonFor(string verb, SupervisorTurnContext context) => SupervisorActionMask.UnavailableReasonFor(verb, context);

    private static string MeaningFor(string verb) => Vocabulary.Single(v => v.Verb == verb).Meaning;
}
