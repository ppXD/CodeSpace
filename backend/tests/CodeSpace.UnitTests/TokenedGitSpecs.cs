using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests;

/// <summary>
/// The call-site pins' view of a tokened git command, spelled out literally rather than through
/// <c>TokenedGitCommand</c> itself: the reset scoped to the remote leads the argv exactly once, and every trace2 target
/// is off. A spec carrying only part of that fails the test outright — it is neither shape.
/// </summary>
internal static class TokenedGitSpecs
{
    private static readonly string[] TraceOff = { "GIT_TRACE2", "GIT_TRACE2_EVENT", "GIT_TRACE2_PERF" };

    /// <summary>True when <paramref name="spec"/> runs as a tokened command for the remote at <paramref name="scope"/> (its scheme and authority, e.g. <c>https://example.test</c>); false when it carries no part of one.</summary>
    public static bool RunsTokened(SandboxSpec spec, string scope)
    {
        var leads = spec.Args.Take(2).SequenceEqual(new[] { "-c", $"credential.{scope}.helper=" });
        var resets = spec.Args.Count(a => a.StartsWith("credential.", StringComparison.Ordinal) && a.EndsWith(".helper=", StringComparison.Ordinal));
        var traceOff = TraceOff.Count(k => spec.Environment.TryGetValue(k, out var v) && v == "0");
        var traceKeys = TraceOff.Count(spec.Environment.ContainsKey);

        var tokened = leads && resets == 1 && traceOff == TraceOff.Length;
        var untokened = resets == 0 && traceKeys == 0;
        (tokened || untokened).ShouldBeTrue($"half a tokened command: {string.Join(' ', spec.Args)} | env {string.Join(' ', spec.Environment.Keys)}");

        return tokened;
    }
}
