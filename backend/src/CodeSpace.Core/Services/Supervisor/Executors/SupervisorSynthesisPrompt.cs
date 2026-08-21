using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// Pure, deterministic projection of merge inputs into a bounded synthesis prompt. Small inputs retain the exact
/// legacy rendering. Large inputs are fairly sliced across agents/repositories and carry machine-readable coverage,
/// so a partial view can never masquerade as the whole result set.
/// </summary>
internal static class SupervisorSynthesisPrompt
{
    private const int MinPartChars = 400;

    public static SupervisorSynthesisProjection Project(string goal, IReadOnlyList<SupervisorSynthesisSource> agents, int authoredBudgetChars)
    {
        var budgetChars = SupervisorSynthesisBudget.Normalize(authoredBudgetChars);
        var whole = RenderWhole(goal, agents);
        var sources = RoundRobinSources(agents);

        if (whole.Length <= budgetChars) return Whole(whole, sources.Count, budgetChars);

        return Excerpt(goal, sources, budgetChars);
    }

    private static string RenderWhole(string goal, IReadOnlyList<SupervisorSynthesisSource> agents)
    {
        var text = new StringBuilder();
        text.Append("Goal: ").Append(goal).Append("\n\n");

        foreach (var agent in agents)
        {
            text.Append("=== Agent ").Append(agent.AgentRunId).Append(" (").Append(agent.Status).Append(") ===\n");
            if (!string.IsNullOrWhiteSpace(agent.Summary)) text.Append("Summary: ").Append(agent.Summary).Append('\n');

            foreach (var diff in agent.Diffs)
                AppendDiff(text, diff);
        }

        return text.ToString();
    }

    private static SupervisorSynthesisProjection Excerpt(string goal, IReadOnlyList<SupervisorSynthesisPart> sources, int budgetChars)
    {
        var noticeBudget = WidestNoticeWidth(sources.Count) + 1;
        var payloadBudget = Math.Max(0, budgetChars - noticeBudget);
        var availableSlots = payloadBudget / MinPartChars;
        var shown = Math.Min(sources.Count, Math.Max(0, availableSlots - 1));
        var goalPart = $"Goal: {goal}\n\n";
        var selected = sources.Take(shown).Select(RenderPart).Prepend(goalPart).ToList();
        var slices = FairShares(selected.Select(part => part.Length).ToList(), payloadBudget);
        var rendered = selected.Select((part, index) => Slice(part, slices[index])).ToList();
        var shortenedSources = Enumerable.Range(1, rendered.Count - 1).Count(index => !string.Equals(rendered[index], selected[index], StringComparison.Ordinal));
        var notice = Notice(sources.Count, shown);
        var text = $"{notice}\n{string.Concat(rendered)}";

        return new SupervisorSynthesisProjection(text, new SupervisorSynthesisCoverage(
            Complete: false,
            TotalSources: sources.Count,
            IncludedSources: shown,
            ShortenedSources: shortenedSources,
            OmittedSources: sources.Count - shown,
            GoalShortened: !string.Equals(rendered[0], selected[0], StringComparison.Ordinal),
            BudgetChars: budgetChars,
            EmittedChars: text.Length));
    }

    /// <summary>Repository ordinal first, agent ordinal second: every agent's first repo is considered before any agent's second repo.</summary>
    private static IReadOnlyList<SupervisorSynthesisPart> RoundRobinSources(IReadOnlyList<SupervisorSynthesisSource> agents)
    {
        var result = new List<SupervisorSynthesisPart>();
        var maxDiffs = agents.Count == 0 ? 0 : agents.Max(agent => agent.Diffs.Count);

        for (var diffIndex = 0; diffIndex < maxDiffs; diffIndex++)
            foreach (var agent in agents)
                if (agent.Diffs.Count > diffIndex)
                    result.Add(new SupervisorSynthesisPart(agent.AgentRunId, agent.Status, agent.Summary, agent.Diffs[diffIndex]));

        return result;
    }

    private static string RenderPart(SupervisorSynthesisPart part)
    {
        var text = new StringBuilder();
        text.Append("=== Agent ").Append(part.AgentRunId).Append(" (").Append(part.Status).Append(") ===\n");
        if (!string.IsNullOrWhiteSpace(part.Summary)) text.Append("Summary: ").Append(part.Summary).Append('\n');
        AppendDiff(text, part.Diff);
        return text.ToString();
    }

    private static void AppendDiff(StringBuilder text, SupervisorSynthesisDiff diff)
    {
        if (diff.Alias is { Length: > 0 }) text.Append("Diff [").Append(diff.Alias).Append("]:\n");
        else text.Append("Diff:\n");

        text.Append(string.IsNullOrEmpty(diff.Text) ? "(no diff captured)" : diff.Text).Append("\n\n");
    }

    private static IReadOnlyList<int> FairShares(IReadOnlyList<int> needs, int budget)
    {
        if (needs.Count == 0) return Array.Empty<int>();

        var remaining = budget;
        var unresolved = Enumerable.Range(0, needs.Count).ToList();
        var slices = new int[needs.Count];

        while (unresolved.Count > 0)
        {
            var share = remaining / unresolved.Count;
            var satisfied = unresolved.Where(index => needs[index] <= share).ToList();

            if (satisfied.Count == 0)
            {
                foreach (var index in unresolved) slices[index] = share;
                break;
            }

            foreach (var index in satisfied)
            {
                slices[index] = needs[index];
                remaining -= needs[index];
                unresolved.Remove(index);
            }
        }

        return slices;
    }

    private static string Slice(string value, int budget)
    {
        if (value.Length <= budget) return value;
        if (budget <= 0) return "";

        const string marker = "…[content shortened to fit synthesis prompt budget]…";
        if (marker.Length >= budget) return SafePrefix(marker, budget);

        var keep = budget - marker.Length;
        var headChars = keep * 2 / 3;
        var tailChars = keep - headChars;
        var head = SafePrefix(value, headChars);
        var tail = SafeSuffix(value, tailChars);

        return head + marker + tail;
    }

    private static string SafePrefix(string value, int count)
    {
        count = Math.Clamp(count, 0, value.Length);
        if (count > 0 && count < value.Length && char.IsHighSurrogate(value[count - 1]) && char.IsLowSurrogate(value[count])) count--;
        return value[..count];
    }

    private static string SafeSuffix(string value, int count)
    {
        count = Math.Clamp(count, 0, value.Length);
        var start = value.Length - count;
        if (start > 0 && start < value.Length && char.IsLowSurrogate(value[start]) && char.IsHighSurrogate(value[start - 1])) start++;
        return value[start..];
    }

    private static SupervisorSynthesisProjection Whole(string text, int totalSources, int budgetChars) =>
        new(text, new SupervisorSynthesisCoverage(true, totalSources, totalSources, 0, 0, false, budgetChars, text.Length));

    private static int WidestNoticeWidth(int total)
    {
        var widest = new string('9', Math.Max(1, total.ToString().Length));
        return NoticeFor(widest, total.ToString(), widest).Length;
    }

    private static string Notice(int total, int shown) => NoticeFor(shown.ToString(), total.ToString(), (total - shown).ToString());

    private static string NoticeFor(string shown, string total, string omitted) =>
        $"[EXCERPT — NOT the complete supervisor synthesis input. {shown} of {total} diff sources appear below; {omitted} are omitted. Included sources and the goal may be shortened inline. Synthesize only from present evidence and state that coverage is partial.]";
}

internal sealed record SupervisorSynthesisSource(Guid AgentRunId, string Status, string? Summary, IReadOnlyList<SupervisorSynthesisDiff> Diffs);
internal sealed record SupervisorSynthesisDiff(string? Alias, string Text);
internal sealed record SupervisorSynthesisProjection(string Text, SupervisorSynthesisCoverage Coverage);
internal sealed record SupervisorSynthesisCoverage(bool Complete, int TotalSources, int IncludedSources, int ShortenedSources, int OmittedSources, bool GoalShortened, int BudgetChars, int EmittedChars);
internal sealed record SupervisorSynthesisPart(Guid AgentRunId, string Status, string? Summary, SupervisorSynthesisDiff Diff);
