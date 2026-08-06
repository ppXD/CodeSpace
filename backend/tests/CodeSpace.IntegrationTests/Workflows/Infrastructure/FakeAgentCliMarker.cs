namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// How a REAL-CLI test tells that the command env var is pointing at one of this suite's fakes rather than at a real
/// binary. A fake arms a PROCESS-WIDE env var, so while it is armed every other test in the same process sees it —
/// and the real-CLI gates all decide "is the CLI available?" by asking whether the configured path is a file that
/// EXISTS. A fake script exists. So those gates run the fake and assert real-CLI semantics against it, instead of
/// self-skipping.
///
/// <para>Scheduling alone cannot fix this: the real-CLI resume classes declare no <c>[Collection]</c>, so xUnit may
/// run them beside a fake-arming class, and any future collection reshuffle would silently re-open the hole. A gate
/// that checks WHAT it resolved holds regardless of who runs when.</para>
///
/// <para>The convention this keys on is pinned by <c>FakeAgentCliMarkerConventionTests</c> — a new fake that names
/// its script differently reds there rather than quietly becoming invisible to these gates.</para>
/// </summary>
public static class FakeAgentCliMarker
{
    /// <summary>Every fake CLI writes its shell script under a name starting with this.</summary>
    public const string ScriptNamePrefix = "fake-";

    /// <summary>Every fake CLI stages its script in a temp directory whose name carries this marker.</summary>
    public const string DirectoryMarker = "-fakecli-";

    /// <summary>Whether a resolved CLI path is one of this suite's fakes. A real binary (on PATH or an operator's own override) matches neither marker.</summary>
    public static bool IsFakeCli(string? resolvedPath) =>
        !string.IsNullOrWhiteSpace(resolvedPath)
        && (Path.GetFileName(resolvedPath).StartsWith(ScriptNamePrefix, StringComparison.Ordinal)
            || resolvedPath.Contains(DirectoryMarker, StringComparison.Ordinal));
}
