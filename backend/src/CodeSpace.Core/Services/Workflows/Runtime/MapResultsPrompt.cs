using System.Text.Json;
using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Runtime;

/// <summary>
/// Projects a <c>flow.map</c>'s reduced result array into a PROMPT-READY string that fits a character budget —
/// the bound a downstream reduce (the plan-map synthesizer) is handed instead of the raw array — TOGETHER WITH the
/// <see cref="MapResultsCoverage"/> stating how much of the input that string carries.
///
/// <para><b>Why this exists.</b> A reduce prompt that interpolates the whole result array grows with the branch
/// count AND with each branch's output, so a large fan-out (or a few branches with large outputs) builds a prompt
/// past the model's context window. The transport then throws — a 400 the classifier reads as <c>BadRequest</c> or
/// <c>ContextLengthExceeded</c>, neither of which is parkable — so the reduce node fails AFTER every branch has
/// already run and billed. This projection makes the input bounded instead.</para>
///
/// <para><b>The three guarantees.</b> (1) The returned text is never longer than <c>budgetChars</c> whenever that
/// budget is positive. (2) Whenever any content was dropped, the returned <see cref="MapResultsCoverage"/> says so
/// — how many branches are present out of how many exist, which included branches were shortened, and a single
/// <see cref="MapResultsCoverage.Complete"/> flag. (3) The text ALSO says so, opening with a notice and marking
/// each shortened branch inline.</para>
///
/// <para><b>Why (2) is not just a restatement of (3).</b> The notice in the text is addressed to a MODEL, and
/// whether a model honors it is unverifiable — so on its own it leaves a run that synthesized 3 of 20 branches
/// indistinguishable, in the data, from one that synthesized 20 of 20. The coverage is the same fact as a value:
/// produced here, by the code that did the cutting, on the way to the map's output bag, where a downstream node can
/// bind it, a projection can read it, and a test can assert it. A projection that dropped content and recorded only
/// a request to the model would be a worse defect than the overflow it replaces, because a synthesis over a
/// silently partial view reads as complete.</para>
///
/// <para><b>Fair share, not first-come.</b> Every included branch is guaranteed an equal slice of the payload
/// budget; only the SURPLUS left by branches needing less than their share is redistributed among the branches
/// that want more. One pathological branch therefore cannot consume the budget and starve its siblings.</para>
///
/// <para><b>Under budget it is byte-identical to the unbounded binding.</b> The within-budget case returns exactly
/// <c>JsonSerializer.Serialize(resultsArray)</c> as its text — the same call <c>VariableResolver</c>'s array arm
/// makes on the same element — so the ordinary small fan-out reaches the model unchanged, character for character,
/// and its coverage reads complete.</para>
///
/// <para>Pure + deterministic (no clock, no env, no I/O), so a replayed run projects the identical prompt.</para>
/// </summary>
public static class MapResultsPrompt
{
    /// <summary>The smallest payload slice a SHOWN branch may be given. It is what makes an excerpt worth reading — a slice of a few characters would be noise — and it is what bounds how many branches can be shown at all: a budget holds at most <c>payload / this</c> of them, and the notice declares the rest absent.</summary>
    public const int MinBranchChars = 400;

    /// <summary>
    /// The bounded prompt form of <paramref name="resultsArray"/> (a map's reduced array), plus the coverage that
    /// form achieved. <paramref name="budgetChars"/> &lt;= 0 disables the bound. A non-array element, and an empty
    /// array, are returned as their own serialization — what a non-array binding means is the caller's concern, not
    /// this projection's — and count as zero branches, so a cut one is reported incomplete over a zero total rather
    /// than described in a branch vocabulary that does not apply to it.
    /// </summary>
    public static MapResultsProjection Project(JsonElement resultsArray, int budgetChars)
    {
        var whole = JsonSerializer.Serialize(resultsArray);
        var total = resultsArray.ValueKind == JsonValueKind.Array ? resultsArray.GetArrayLength() : 0;

        if (budgetChars <= 0 || whole.Length <= budgetChars) return Whole(whole, total);
        if (resultsArray.ValueKind != JsonValueKind.Array) return NothingIncluded(Cut(whole, budgetChars), total);

        var branches = resultsArray.EnumerateArray().Select(element => JsonSerializer.Serialize(element)).ToList();

        if (branches.Count == 0) return NothingIncluded(Cut(whole, budgetChars), total);

        return BuildExcerpt(branches, budgetChars);
    }

    /// <summary>
    /// The over-budget form: the notice, then the shown branches as a JSON array, each sliced to its fair share.
    /// The notice is reserved at its WIDEST possible rendering before any slicing, so the assembled length is
    /// <c>notice + newline + array</c> ≤ <paramref name="budgetChars"/> by construction.
    /// </summary>
    private static MapResultsProjection BuildExcerpt(IReadOnlyList<string> branches, int budgetChars)
    {
        var payloadBudget = budgetChars - WidestNoticeWidth(branches.Count) - 1;   // -1 for the newline after the notice

        // A budget too small to hold the notice plus one readable slice cannot show any branch. It still says so —
        // cut only because the caller's budget is narrower than the sentence (unreachable from the engine, whose
        // MapPlan floor is far wider); the text that survives still opens with "EXCERPT — NOT the complete".
        if (payloadBudget < MinBranchChars) return NothingIncluded(Cut(Notice(branches.Count, shown: 0), budgetChars), branches.Count);

        var shown = Math.Min(branches.Count, payloadBudget / MinBranchChars);
        var lengths = branches.Take(shown).Select(branch => branch.Length).ToList();
        var slices = FairShares(lengths, payloadBudget - JsonArrayOverhead(shown));

        var sliced = branches.Take(shown).Select((branch, i) => Slice(branch, slices[i])).ToList();

        return new()
        {
            Text = $"{Notice(branches.Count, shown)}\n[{string.Join(",", sliced)}]",
            Coverage = new()
            {
                Complete = false,
                TotalBranches = branches.Count,
                IncludedBranches = shown,
                ShortenedBranches = ShortenedIn(branches, sliced),
            },
        };
    }

    /// <summary>
    /// Which included branches were cut, read off the SLICES THEMSELVES: a branch is shortened exactly when its
    /// projected text differs from its own serialization. Deliberately not a re-statement of <see cref="Slice"/>'s
    /// own condition — a recorded fact derived from the emitted text cannot drift from what was actually emitted.
    /// </summary>
    private static IReadOnlyList<int> ShortenedIn(IReadOnlyList<string> branches, IReadOnlyList<string> sliced) =>
        Enumerable.Range(0, sliced.Count).Where(i => !string.Equals(sliced[i], branches[i], StringComparison.Ordinal)).ToList();

    /// <summary>The unbounded / within-budget projection: the WHOLE serialization, so every branch is present and none was shortened.</summary>
    private static MapResultsProjection Whole(string text, int total) =>
        new() { Text = text, Coverage = new() { Complete = true, TotalBranches = total, IncludedBranches = total } };

    /// <summary>The degenerate over-budget forms — a non-array element, an empty array, or a budget too narrow for even one readable slice. Content was dropped and NO branch is represented, so the coverage says exactly that.</summary>
    private static MapResultsProjection NothingIncluded(string text, int total) =>
        new() { Text = text, Coverage = new() { Complete = false, TotalBranches = total, IncludedBranches = 0 } };

    /// <summary>The brackets + element separators a <paramref name="shown"/>-element JSON array costs on top of the elements themselves.</summary>
    private static int JsonArrayOverhead(int shown) => 2 + Math.Max(0, shown - 1);

    /// <summary>
    /// Split <paramref name="budget"/> across branches so none can be starved: each gets an EQUAL share first,
    /// then the surplus released by branches needing less than their share is spread evenly over the branches that
    /// wanted more. The returned slices sum to at most <paramref name="budget"/>.
    /// </summary>
    private static IReadOnlyList<int> FairShares(IReadOnlyList<int> needs, int budget)
    {
        var share = budget / needs.Count;
        var slices = new int[needs.Count];
        var wantMore = new List<int>();
        var surplus = 0;

        for (var i = 0; i < needs.Count; i++)
        {
            if (needs[i] <= share) { slices[i] = needs[i]; surplus += share - needs[i]; }
            else wantMore.Add(i);
        }

        if (wantMore.Count == 0) return slices;

        var extra = surplus / wantMore.Count;

        foreach (var i in wantMore) slices[i] = share + extra;

        return slices;
    }

    /// <summary>
    /// One branch's serialization cut to EXACTLY <paramref name="budget"/> when it overflows, keeping a head-heavy
    /// head+tail around a marker naming what was dropped (the 2/3 split mirrors <c>OutputCap</c> — the signal sits
    /// at a result's start and at its conclusion). Deliberately NOT <c>OutputCap.Apply</c>: that primitive returns
    /// the value UNTRUNCATED when a marker would not shrink it, which is right for a preview and wrong here —
    /// summed over branches it would put the prompt back over the budget this projection exists to hold.
    /// </summary>
    private static string Slice(string branch, int budget)
    {
        if (branch.Length <= budget) return branch;

        var marker = $"…[{branch.Length - budget} of {branch.Length} chars of this subtask result omitted]…";

        // Total function: a marker wider than the slice cannot arise on the engine path, where every slice is at
        // least MinBranchChars and the marker is far narrower than that.
        if (marker.Length >= budget) return Cut(marker, budget);

        var keep = budget - marker.Length;
        var head = Math.Max(1, keep * 2 / 3);
        var tail = keep - head;

        return tail > 0 ? branch[..head] + marker + branch[^tail..] : branch[..head] + marker;
    }

    private static string Cut(string text, int budget) => text.Length <= budget ? text : text[..Math.Max(0, budget)];

    /// <summary>
    /// The widest the notice can render for a given branch count: both variable counts are bounded by
    /// <paramref name="total"/>, so widening BOTH to that many digits gives an upper bound the real notice can
    /// never exceed. Computed from the same builder as the notice itself, so the reservation cannot drift from
    /// the sentence.
    /// </summary>
    private static int WidestNoticeWidth(int total)
    {
        var widest = new string('9', total.ToString().Length);

        return NoticeFor(widest, total.ToString(), widest).Length;
    }

    private static string Notice(int total, int shown) => NoticeFor(shown.ToString(), total.ToString(), (total - shown).ToString());

    /// <summary>
    /// The excerpt notice. It leads the prompt so the model reads the caveat BEFORE the data, and it states the
    /// three things a reader needs in order not to mistake a part for the whole: that this is an excerpt, how many
    /// of how many results are present, and that a present result may itself be shortened.
    /// </summary>
    private static string NoticeFor(string shown, string total, string absent) =>
        $"[EXCERPT — NOT the complete per-subtask results. {shown} of {total} subtask results appear below; {absent} are absent entirely. " +
        "An included result may itself be shortened, marked inline as \"…[N of M chars of this subtask result omitted]…\". " +
        "Synthesize only from what is present, and state in the answer that it is based on a partial view of the results.]";
}
