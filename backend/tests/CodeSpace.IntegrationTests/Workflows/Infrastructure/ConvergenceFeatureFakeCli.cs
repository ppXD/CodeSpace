using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A goal-aware fake Codex CLI for the SESSION CONVERGENCE whole-loop: it writes feature A's file on the turn whose
/// goal carries the <see cref="AlphaMarker"/>, and feature B's file on the turn whose goal carries the
/// <see cref="BetaMarker"/> — keyed off the prompt exactly as <see cref="FileWritingFakeCli"/> /
/// <see cref="SolutionWritingFakeCli"/> do, so two sequential session turns produce two DISJOINT features. Because a
/// continue clones turn 1's PRODUCED branch, turn 2 starts from a workspace that already contains feature A, so the B
/// turn — which only writes <c>b.sh</c> — leaves a working tree with BOTH features. That carried-forward A is the
/// convergence the test proves objectively (it is NOT written by the B turn).
///
/// <para><b>The teeth.</b> Construct with <paramref name="preservePriorWork"/> = <c>false</c> to simulate an agent that
/// DISCARDS the prior turn's work (it clobbers <c>a.sh</c> with a broken body while doing B), so the both-features
/// acceptance oracle goes RED — proving the grade distinguishes "built on the carried branch" from "redid / lost A".</para>
///
/// <para><b>Marker ordering.</b> A continue composes turn 2's goal as {prior-turn digest + the new ask}, so the digest
/// ECHOES turn 1's goal — which carries <see cref="AlphaMarker"/>. The script therefore matches <see cref="BetaMarker"/>
/// FIRST: turn 2 (digest has A, ask has B) takes the B branch; turn 1 (A only) takes the A branch.</para>
///
/// <para>POSIX <c>/bin/sh</c> only (the runner spawns it via the shebang; may be dash — no bashisms). No env, no
/// network, no codex binary — just <c>/bin/sh</c>, a heredoc file write, and <c>printf</c>.</para>
/// </summary>
public sealed class ConvergenceFeatureFakeCli : IDisposable
{
    /// <summary>Put this token in turn 1's goal — the script writes feature A (<c>a.sh</c> ⇒ prints <c>A-OK</c>).</summary>
    public const string AlphaMarker = "FEATURE_ALPHA";

    /// <summary>Put this token in turn 2's (follow-up) goal — the script writes feature B (<c>b.sh</c> ⇒ prints <c>B-OK</c>).</summary>
    public const string BetaMarker = "FEATURE_BETA";

    /// <summary>The summary prefix the executor's BuildResult folds (mirrors <see cref="FileWritingFakeCli.SummaryPrefix"/>).</summary>
    public const string SummaryPrefix = "DONE: ";

    /// <summary>
    /// The both-features acceptance oracle to seed as <c>check.sh</c> on the base branch: exit 0 iff the carried-forward
    /// <c>a.sh</c> still prints <c>A-OK</c> AND the new <c>b.sh</c> prints <c>B-OK</c> — output equality, not file presence,
    /// so a clobbered A fails. Carried forward to every produced branch, so the grader runs it on turn 2's head.
    /// </summary>
    public const string BothFeaturesCheckSh = "#!/bin/sh\n[ \"$(sh a.sh 2>/dev/null)\" = \"A-OK\" ] && [ \"$(sh b.sh 2>/dev/null)\" = \"B-OK\" ]\n";

    private readonly string _originalCommand;
    private readonly string _dir;

    public ConvergenceFeatureFakeCli(bool preservePriorWork)
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-convergence-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-codex.sh");
        File.WriteAllText(script, ScriptBody(preservePriorWork));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _originalCommand = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar) ?? "";
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _originalCommand.Length == 0 ? null : _originalCommand);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Walk the positional args so <c>$goal</c> ends as the LAST one (Codex puts the prompt last), then write the
    /// feature whose marker the goal carries (BETA first — see the type doc on marker ordering) via a QUOTED heredoc
    /// (literal body), and print the three-line codex-shaped JSONL stream the real Codex ParseEvent folds. When
    /// <paramref name="preservePriorWork"/> is false, the B branch also overwrites <c>a.sh</c> with a broken body.
    /// </summary>
    private static string ScriptBody(bool preservePriorWork)
    {
        var clobberA = preservePriorWork
            ? ""
            : "    cat > a.sh <<'CS_CLOBBER_EOF'\n#!/bin/sh\necho BROKEN\nCS_CLOBBER_EOF\n    chmod +x a.sh\n";

        return
            "#!/bin/sh\n" +
            "goal=\"\"\n" +
            "for goal in \"$@\"; do :; done\n" +
            "case \"$goal\" in\n" +
            "  *" + BetaMarker + "*)\n" +
            "    cat > b.sh <<'CS_FEAT_EOF'\n" +
            "#!/bin/sh\necho B-OK\n" +
            "CS_FEAT_EOF\n" +
            "    chmod +x b.sh\n" +
            clobberA +
            "    msg=\"" + SummaryPrefix + "implemented " + BetaMarker + "\"\n" +
            "    ;;\n" +
            "  *" + AlphaMarker + "*)\n" +
            "    cat > a.sh <<'CS_FEAT_EOF'\n" +
            "#!/bin/sh\necho A-OK\n" +
            "CS_FEAT_EOF\n" +
            "    chmod +x a.sh\n" +
            "    msg=\"" + SummaryPrefix + "implemented " + AlphaMarker + "\"\n" +
            "    ;;\n" +
            "  *)\n" +
            "    msg=\"" + SummaryPrefix + "no-op\"\n" +
            "    ;;\n" +
            "esac\n" +
            "esc=$(printf '%s' \"$msg\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
            "printf '{\"type\":\"agent_reasoning\",\"message\":\"working on the feature\"}\\n'\n" +
            "printf '{\"type\":\"agent_message\",\"message\":\"%s\"}\\n' \"$esc\"\n" +
            "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
            "exit 0\n";
    }
}
