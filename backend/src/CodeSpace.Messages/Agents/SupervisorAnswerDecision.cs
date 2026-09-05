namespace CodeSpace.Messages.Agents;

/// <summary>
/// The ONE structured answer envelope every human-in-the-loop supervisor card is ruled on — the
/// plan confirmation, the irreversible-action approval, the review-gate escalation, and the amend co-sign.
/// A card's verdict is now a FIELD the answering surface sends (<c>decision</c> = approve | revise | reject),
/// not a word the server matches at the front of the operator's free text.
///
/// <para>WHY: the prefix contract made the verdict a property of the ENGLISH the human happened to type. A
/// 繁中「批准」or「同意」— a genuine approval — read as revision feedback, and "approve nothing until the
/// tests pass" needed a defensive rewrite prefix to stop it releasing a gate. The decision is a choice the
/// UI already knows (the operator clicked Approve, not Request changes); carrying it verbatim removes the
/// natural-language step entirely.</para>
///
/// <para>The <c>approve</c>-prefix read survives ONLY as a LEGACY FALLBACK for an answer that carries no
/// <c>decision</c> field — an old Room client, a chat card clicked through the generic Action surface, or a
/// test responder typing the reply word. A card WITH a decision field never consults the text.</para>
/// </summary>
public static class SupervisorAnswerDecision
{
    /// <summary>The key the structured verdict rides under, both in the Action wait's <c>values</c> submission and on the folded ask_human outcome. Load-bearing across the resume path, the rehydrate fold, and all four cards — pinned by a unit test.</summary>
    public const string Field = "decision";

    /// <summary>Proceed with what the card proposed — release the gate / confirm the plan / apply the amendment.</summary>
    public const string Approve = "approve";

    /// <summary>Do not proceed as proposed; the accompanying note is the revision the supervisor folds into its next decision.</summary>
    public const string Revise = "revise";

    /// <summary>Do not proceed at all. Read exactly like <see cref="Revise"/> by every gate today (both are simply NOT an approval) — a distinct value so a surface can say which the human meant, and so a future hard-stop read has a name to key on.</summary>
    public const string Reject = "reject";

    /// <summary>The closed set a caller may send. An unknown value is refused at the endpoint rather than silently read as a non-approval.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Approve, Revise, Reject };

    /// <summary>Whether a caller-supplied verdict is a member of the closed set (trimmed, case-insensitive). Null → false; the caller sent no decision and the legacy text fallback applies.</summary>
    public static bool IsKnown(string? decision) => Normalize(decision) is not null;

    /// <summary>Whether the structured verdict APPROVES. Exact against <see cref="Approve"/> (trimmed, case-insensitive) — never a prefix, never a synonym, so no text can widen it.</summary>
    public static bool IsApprove(string? decision) => Normalize(decision) == Approve;

    /// <summary>The canonical lower-case member for a caller's value, or null when it is not one of <see cref="All"/>.</summary>
    public static string? Normalize(string? decision)
    {
        var trimmed = decision?.Trim().ToLowerInvariant();

        return trimmed is not null && All.Contains(trimmed) ? trimmed : null;
    }
}
