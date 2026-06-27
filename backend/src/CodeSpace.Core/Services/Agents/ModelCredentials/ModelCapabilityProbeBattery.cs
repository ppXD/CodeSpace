using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>The difficulty band of a probe task. An EASY task gates <see cref="ModelCapabilityTier.Basic"/> (basic coding correctness); a HARD task additionally gates <see cref="ModelCapabilityTier.Strong"/> (structured output + multi-step reasoning a weaker model fails).</summary>
public enum ProbeBand { Easy, Hard }

/// <summary>One deterministic, known-answer probe task — a prompt + the in-code oracle that grades a model's raw completion pass/fail. No self-rating: <see cref="Passes"/> reads the response text and checks the objective answer.</summary>
public sealed record ProbeTask(string Prompt, ProbeBand Band, Func<string, bool> Passes);

/// <summary>
/// The FIXED in-code micro-battery the capability PROBE runs against an OPAQUE model (one the brain tiered
/// <see cref="ModelCapabilityTier.Unknown"/>). Deterministic known-answer coding + structured-output tasks graded IN
/// CODE — the model DEMONSTRATES capability, it never rates itself. The score maps to a COARSE tier capped at
/// <see cref="ModelCapabilityTier.Strong"/> (a small battery cannot honestly separate Strong from Frontier; Frontier
/// stays exclusive to brain-tiering of recognised ids). The task SET is pinned by a unit test so a careless edit that
/// breaks the Easy/Hard discrimination is caught.
/// </summary>
public static class ModelCapabilityProbeBattery
{
    public static IReadOnlyList<ProbeTask> Tasks { get; } =
    [
        // EASY — basic deterministic coding correctness. A competent model clears these; a broken / tiny one does not.
        new("What is the greatest common divisor of 48 and 36? Reply with ONLY the integer, no words.", ProbeBand.Easy, r => HasNumber(r, 12)),
        new("A crate holds 3 rows of 7 apples. How many apples in total? Reply with ONLY the integer.", ProbeBand.Easy, r => HasNumber(r, 21)),
        new("Reverse this string: stream. Reply with ONLY the reversed string.", ProbeBand.Easy, r => r.Contains("maerts", StringComparison.OrdinalIgnoreCase)),

        // HARD — structured output + multi-step reasoning. A weaker model botches the JSON shape, the multi-step order, or the strict format.
        new("Return ONLY a JSON object (no markdown fences, no prose) with integer keys \"sum\" and \"product\" for the numbers 6 and 7.", ProbeBand.Hard, r => GradeSumProductJson(r, 13, 42)),
        new("Sort the list [30, 10, 20] in ascending order, then add the first and last elements of the sorted list. Reply with ONLY the integer.", ProbeBand.Hard, r => HasNumber(r, 40)),
        new("List the first three prime numbers as a comma-separated list with NO spaces. Reply with ONLY the list.", ProbeBand.Hard, r => HasIntSequence(r, "2", "3", "5")),
    ];

    /// <summary>
    /// Map a battery score to a probed tier. A MAJORITY (&gt;50%, N-of-M) of the EASY tasks ⇒ at least
    /// <see cref="ModelCapabilityTier.Basic"/>; ALSO a majority of the HARD tasks ⇒ <see cref="ModelCapabilityTier.Strong"/>
    /// (the cap — never Frontier). Below the Easy majority ⇒ <c>null</c> (no verdict; the row stays Unknown and re-probes
    /// later). The majority threshold tolerates a single flaky miss; combined with the caller's monotonic-upgrade write it
    /// makes one bad day non-destructive.
    /// </summary>
    public static ModelCapabilityTier? MapToTier(int easyPasses, int hardPasses)
    {
        var easyMajority = easyPasses >= Majority(CountBand(ProbeBand.Easy));
        var hardMajority = hardPasses >= Majority(CountBand(ProbeBand.Hard));

        if (!easyMajority) return null;

        return hardMajority ? ModelCapabilityTier.Strong : ModelCapabilityTier.Basic;
    }

    public static int CountBand(ProbeBand band) => Tasks.Count(t => t.Band == band);

    private static int Majority(int total) => total / 2 + 1;

    /// <summary>True when <paramref name="value"/> appears as a STANDALONE number in the response — guarded only against an adjacent DIGIT (so "12" isn't satisfied by "1200"), NOT against punctuation, so a terse "12." / "The GCD is 12." still matches (a trailing-period habit must not fail the Easy band).</summary>
    private static bool HasNumber(string response, int value) => Regex.IsMatch(response, $@"(?<!\d){value}(?!\d)");

    /// <summary>True when the integers in the response, in order, are EXACTLY <paramref name="expected"/> — so "2,3,5" / "2, 3, and 5" pass but "2,3,5,7" (too many), "1,2,3,5" (off-by-one) and "12,3,5" (leading digit) fail. Anchored on the full extracted sequence, separator-agnostic.</summary>
    private static bool HasIntSequence(string response, params string[] expected) =>
        Regex.Matches(response, @"\d+").Select(m => m.Value).SequenceEqual(expected);

    /// <summary>Check whether ANY flat <c>{…}</c> object in the response parses to the expected integer fields — tolerating markdown fences / prose (incl. an incidental earlier brace group) around the real answer. A missing / wrong-shape / wrong-value object ⇒ false — the discriminating Strong signal (never false-passes, since the exact values are required).</summary>
    private static bool GradeSumProductJson(string response, int expectedSum, int expectedProduct)
    {
        foreach (Match match in Regex.Matches(response, @"\{[^{}]*\}"))
        {
            try
            {
                using var doc = JsonDocument.Parse(match.Value);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object
                    && TryInt(root, "sum", out var sum) && sum == expectedSum
                    && TryInt(root, "product", out var product) && product == expectedProduct)
                    return true;
            }
            catch (JsonException)
            {
                // not the answer object — keep scanning
            }
        }

        return false;
    }

    private static bool TryInt(JsonElement obj, string key, out int value)
    {
        value = 0;
        return obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
    }
}
