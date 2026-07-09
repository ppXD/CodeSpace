using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// P3.3 — the harness-NATIVE half of loop-engineer's "in-loop verify": a Stop hook that re-runs the task's OWN
/// <see cref="AgentTask.Acceptance"/> command, inside the SAME conversation, before the harness ever lets the agent
/// end its turn. This is a control-plane / harness-loop distinction (see the loop-engineer plan): the CONTROL PLANE
/// (<c>SupervisorAcceptanceGrader</c> / the per-unit fold / the terminal stop gate) grades a SETTLED result AFTER the
/// process has already exited — this hook instead gives the SAME check a chance to fail FAST, in-process, so the
/// model can fix its own work before ever producing a result the control plane has to reject-and-retry from scratch.
///
/// <para><b>The control plane remains the final judge, unconditionally.</b> A hook PASS (or a hook that never fired
/// at all — no CLI support, disabled feature flag, a broken script) means nothing more than "the harness didn't see
/// a reason to keep going" — it is NEVER treated as an acceptance verdict. Nothing in this class, or in either
/// harness's wiring of it, feeds a hook's outcome into <c>AcceptancePassed</c> or skips the post-hoc grade; the
/// exact same <see cref="AgentTask.Acceptance"/> command is re-run, independently, against the settled result, by
/// the unchanged control-plane path. This hook can only ever save a retry round-trip, never replace a verdict.</para>
///
/// <para><b>Fail-soft by design.</b> The generated script never depends on parsing anything the harness hands it
/// (hook stdin is drained and ignored outright — a malformed/absent JSON payload can't affect it), and treats every
/// internal problem — a missing counter directory, the acceptance binary itself not existing (exit 126/127, the
/// same OS-launch-failure convention <c>AgentAcceptanceContract.IsInfraFailure</c>'s grader-side prefixes already
/// treat as infra, not a genuine failure) — as "let the harness stop"; the control plane is the fallback authority.
/// A broken hook can degrade the loop back to the ledger-terminal-only behavior that predates this slice; it can
/// never crash the agent or fabricate a false pass.</para>
///
/// <para><b>The block counter is CodeSpace's own</b> — deliberately NOT the harness's native cap (Claude Code's own
/// built-in "stop after 8 consecutive blocks" is undocumented for Codex, and this arc never wants two harnesses to
/// silently diverge on how many self-correction attempts a run gets). <see cref="MaxBlocks"/> is a small, Rule-8
/// env-var-overridable ceiling the generated script enforces itself via a per-run counter file, independent of
/// whatever native cap each CLI happens to apply on top.</para>
/// </summary>
public static class InLoopAcceptanceHook
{
    /// <summary>Operator escape hatch (Rule 8): how many times the in-loop Stop hook may block a turn before it gives up and lets the harness stop (control-plane grading + the existing retry/revise machinery take it from there). Small on purpose — this saves a round-trip, it isn't meant to replace the control plane's own retry budget.</summary>
    public const string MaxBlocksEnvVar = "CODESPACE_AGENT_STOP_HOOK_MAX_BLOCKS";

    internal const int DefaultMaxBlocks = 1;

    /// <summary>The config-home-relative path (both harnesses' isolated config dir) the generated Stop-hook script is written to.</summary>
    public const string ScriptRelativePath = "hooks/stop-acceptance-check.sh";

    private const string CounterRelativePath = "hooks/.stop-hook-counter";
    private const string OutputRelativePath = "hooks/.stop-hook-output";

    /// <summary>How much of the failing check's own output rides in the block reason — enough to be actionable (which assertion, which file), bounded so a noisy test suite never floods the model's next turn.</summary>
    private const int OutputTailBytes = 800;

    /// <summary>The operator's configured block ceiling, or <see cref="DefaultMaxBlocks"/> when unset/unparseable/negative.</summary>
    public static int MaxBlocks =>
        int.TryParse(Environment.GetEnvironmentVariable(MaxBlocksEnvVar), out var n) && n >= 0 ? n : DefaultMaxBlocks;

    /// <summary>Whether a task carries an acceptance command real enough to wire an in-loop Stop hook for — mirrors <see cref="AgentAcceptanceContract.RequiresGrade"/> exactly (the same "is there really a check here" test the control plane already uses), so the hook is wired for precisely the tasks the control plane will later grade.</summary>
    public static bool AppliesTo(AgentTask task) => AgentAcceptanceContract.RequiresGrade(task);

    /// <summary>
    /// The POSIX-<c>sh</c> Stop-hook script content, harness-agnostic (both Claude Code's <c>settings.json</c> and
    /// Codex's <c>hooks.json</c> point a <c>command</c> handler at the SAME generated file). Resolves its own config
    /// home at runtime via <c>${CLAUDE_CONFIG_DIR:-$CODEX_HOME}</c> (mirroring the harness-agnostic fallback already
    /// used elsewhere for exactly this purpose) so one script body serves either harness without needing to know
    /// which one invoked it.
    /// </summary>
    public static string BuildScript(IReadOnlyList<string> acceptanceCommand, int maxBlocks)
    {
        var sb = new StringBuilder();

        sb.Append("#!/bin/sh\n");
        sb.Append("# CodeSpace in-loop acceptance Stop hook (P3.3), generated per run. Fail-soft: any problem here\n");
        sb.Append("# (unreadable counter dir, the check binary missing, anything unexpected) lets the harness stop —\n");
        sb.Append("# the control-plane grader is the unconditional final judge regardless of what this hook decides.\n");
        sb.Append("cat >/dev/null 2>&1\n");   // drain + ignore stdin — never depend on parsing the harness's hook payload
        sb.Append("CFG=\"${CLAUDE_CONFIG_DIR:-$CODEX_HOME}\"\n");
        sb.Append("[ -n \"$CFG\" ] || exit 0\n");
        sb.Append($"COUNTER_FILE=\"$CFG/{CounterRelativePath}\"\n");
        sb.Append($"MAX_BLOCKS={maxBlocks}\n");
        sb.Append("COUNT=$(cat \"$COUNTER_FILE\" 2>/dev/null)\n");
        sb.Append("case \"$COUNT\" in ''|*[!0-9]*) COUNT=0 ;; esac\n");   // any unreadable/non-numeric counter ⇒ treat as 0, never crash
        sb.Append("[ \"$COUNT\" -lt \"$MAX_BLOCKS\" ] || exit 0\n");   // cap reached — let the harness stop, defer to the control plane

        AppendArgv(sb, acceptanceCommand);

        sb.Append($"OUTPUT_FILE=\"$CFG/{OutputRelativePath}\"\n");
        sb.Append("\"$@\" >\"$OUTPUT_FILE\" 2>&1\n");
        sb.Append("CHECK_EXIT=$?\n");
        sb.Append("[ \"$CHECK_EXIT\" -ne 0 ] || exit 0\n");   // the check passed — nothing to block on
        sb.Append("[ \"$CHECK_EXIT\" -lt 126 ] || exit 0\n");   // 126/127 = command not found/not executable — infra, not a genuine failure

        sb.Append("NEXT=$((COUNT + 1))\n");
        sb.Append("echo \"$NEXT\" > \"$COUNTER_FILE\" 2>/dev/null || exit 0\n");   // can't persist the counter ⇒ fail-soft, let it stop

        // A bounded TAIL of the check's own output — actionable ("which assertion failed"), not a placeholder message;
        // a missing/unreadable output file degrades to an empty tail rather than aborting the hook (still fail-soft).
        // printf (not echo — its backslash-escape handling is NOT portable across /bin/sh implementations) for the
        // reliable literal newline between the header and the captured output.
        sb.Append($"REASON=$(tail -c {OutputTailBytes} \"$OUTPUT_FILE\" 2>/dev/null)\n");
        sb.Append("printf 'In-loop acceptance check still failing (attempt %s of %s). Output:\\n%s\\n' \"$NEXT\" \"$MAX_BLOCKS\" \"$REASON\" >&2\n");
        sb.Append("exit 2\n");

        return sb.ToString();
    }

    /// <summary>Emits <c>set -- 'arg1' 'arg2' ...</c> with each argv token single-quoted (internal single quotes escaped as <c>'\''</c>) so an arbitrary acceptance command — spaces, quotes, globs and all — reaches the check UNCHANGED, never re-split or glob-expanded by the shell. The standard, injection-safe way to embed a dynamic argv in a generated POSIX script.</summary>
    private static void AppendArgv(StringBuilder sb, IReadOnlyList<string> argv)
    {
        sb.Append("set --");

        foreach (var token in argv)
            sb.Append(' ').Append('\'').Append(token.Replace("'", "'\\''")).Append('\'');

        sb.Append('\n');
    }
}
