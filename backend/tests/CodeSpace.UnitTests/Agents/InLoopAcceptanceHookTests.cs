using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the pure, CLI-free half of P3.3's in-loop verify: which tasks get a Stop hook wired
/// (<see cref="InLoopAcceptanceHook.AppliesTo"/>), the operator's block-ceiling override
/// (<see cref="InLoopAcceptanceHook.MaxBlocks"/>), and the exact generated script shape
/// (<see cref="InLoopAcceptanceHook.BuildScript"/>) — argv-safe quoting, the fail-soft exit paths, the
/// CodeSpace-owned counter (never the harness's own native block cap), and the one line that IS
/// harness-specific: the config-home variable the calling harness names. The generated script's actual RUNTIME
/// behavior (does it really block/allow/fail-soft when invoked) is proved separately by a real-shell integration
/// test — this file pins the STRING it produces.
/// </summary>
[Trait("Category", "Unit")]
public class InLoopAcceptanceHookTests
{
    // ── AppliesTo — mirrors AgentAcceptanceContract.RequiresGrade exactly ──

    [Fact]
    public void AppliesTo_is_false_with_no_acceptance_contract()
    {
        InLoopAcceptanceHook.AppliesTo(new AgentTask { Goal = "g", Harness = "claude-code", Model = "m" }).ShouldBeFalse();
    }

    [Fact]
    public void AppliesTo_is_false_when_the_command_is_empty_or_all_blank()
    {
        var empty = new AgentTask { Goal = "g", Harness = "claude-code", Model = "m", Acceptance = new SupervisorAcceptanceSpec { Command = Array.Empty<string>() } };
        var blank = new AgentTask { Goal = "g", Harness = "claude-code", Model = "m", Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "", "  " } } };

        InLoopAcceptanceHook.AppliesTo(empty).ShouldBeFalse();
        InLoopAcceptanceHook.AppliesTo(blank).ShouldBeFalse();
    }

    [Fact]
    public void AppliesTo_is_true_with_a_real_command()
    {
        var task = new AgentTask { Goal = "g", Harness = "claude-code", Model = "m", Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } } };

        InLoopAcceptanceHook.AppliesTo(task).ShouldBeTrue();
    }

    // ── MaxBlocks — the Rule-8 operator escape hatch ──

    [Fact]
    public void MaxBlocksEnvVar_name_is_pinned()
    {
        // Renaming this constant silently breaks any operator who already set it — hard-pin (Rule 8).
        InLoopAcceptanceHook.MaxBlocksEnvVar.ShouldBe("CODESPACE_AGENT_STOP_HOOK_MAX_BLOCKS");
    }

    [Theory]
    [InlineData(null, InLoopAcceptanceHook.DefaultMaxBlocks)]
    [InlineData("", InLoopAcceptanceHook.DefaultMaxBlocks)]
    [InlineData("not-a-number", InLoopAcceptanceHook.DefaultMaxBlocks)]
    [InlineData("-1", InLoopAcceptanceHook.DefaultMaxBlocks)]
    [InlineData("0", 0)]
    [InlineData("3", 3)]
    public void MaxBlocks_reads_the_env_var_and_falls_back_to_the_default(string? raw, int expected)
    {
        var prior = Environment.GetEnvironmentVariable(InLoopAcceptanceHook.MaxBlocksEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(InLoopAcceptanceHook.MaxBlocksEnvVar, raw);
            InLoopAcceptanceHook.MaxBlocks.ShouldBe(expected);
        }
        finally { Environment.SetEnvironmentVariable(InLoopAcceptanceHook.MaxBlocksEnvVar, prior); }
    }

    [Fact]
    public void DefaultMaxBlocks_is_pinned_to_one()
    {
        // "Save a retry round-trip", not "replace the control-plane retry budget" — deliberately small.
        InLoopAcceptanceHook.DefaultMaxBlocks.ShouldBe(1);
    }

    // ── BuildScript — the generated shell content ──

    /// <summary>A config-home variable name belonging to NO shipped harness — the cases below pin the parts of the body that don't vary with it, and using a stand-in keeps them honest about not caring which harness called.</summary>
    private const string StandInConfigHomeEnvVar = "STAND_IN_CONFIG_HOME";

    private static string Script(IReadOnlyList<string> acceptanceCommand, int maxBlocks) => InLoopAcceptanceHook.BuildScript(acceptanceCommand, maxBlocks, StandInConfigHomeEnvVar);

    [Fact]
    public void BuildScript_drains_stdin_and_never_parses_it()
    {
        // The fail-soft guarantee starts here: a malformed hook payload can never reach any parsing logic because
        // there IS none — stdin is piped straight to /dev/null.
        Script(new[] { "sh", "check.sh" }, 1).ShouldContain("cat >/dev/null 2>&1");
    }

    [Theory]
    [InlineData("CLAUDE_CONFIG_DIR")]   // Claude Code's — pinned against the real constant by ClaudeCodeHarnessTests
    [InlineData("CODEX_HOME")]          // Codex's — likewise by CodexHarnessTests
    [InlineData("FAKE_THIRD_HARNESS_HOME")]   // a harness this file has never heard of: the whole point of the parameter
    public void BuildScript_resolves_the_config_home_from_the_variable_the_calling_harness_named(string configHomeEnvVar)
    {
        var script = InLoopAcceptanceHook.BuildScript(new[] { "sh", "check.sh" }, 1, configHomeEnvVar);

        script.ShouldContain($"CFG=\"${configHomeEnvVar}\"", Case.Sensitive,
            "the caller's OWN variable is what the script reads — that is what lets a harness the hook has never heard of get a working hook by dropping a folder");
        script.ShouldNotContain("${CLAUDE_CONFIG_DIR:-$CODEX_HOME}", Case.Sensitive,
            "the body used to try two baked-in names: for any third harness $CFG resolved to empty, the fail-soft guard exited 0, and the in-loop check silently never ran");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2_LEADING_DIGIT")]
    [InlineData("HAS SPACE")]
    [InlineData("HAS\"QUOTE")]
    [InlineData("$INJECTED; rm -rf /")]
    public void BuildScript_rejects_a_config_home_name_that_is_not_a_posix_shell_name(string configHomeEnvVar)
    {
        // The name is interpolated into generated shell as $NAME. A mistyped harness constant must be a loud throw
        // here, not a script whose $CFG quietly resolves to nothing (the exact silent degradation this parameter exists
        // to end) — and certainly not one that carries the typo's own shell syntax into the hook body.
        Should.Throw<ArgumentException>(() => InLoopAcceptanceHook.BuildScript(new[] { "sh", "check.sh" }, 1, configHomeEnvVar));
    }

    [Fact]
    public void BuildScript_bakes_in_the_max_blocks_value()
    {
        Script(new[] { "sh", "check.sh" }, 2).ShouldContain("MAX_BLOCKS=2");
    }

    [Fact]
    public void BuildScript_quotes_a_plain_argv_safely()
    {
        var script = Script(new[] { "sh", "check.sh" }, 1);

        script.ShouldContain("set -- 'sh' 'check.sh'");
    }

    [Fact]
    public void BuildScript_escapes_an_embedded_single_quote_so_the_token_reaches_the_check_unchanged()
    {
        // The standard POSIX trick: close the quote, escaped-quote, reopen — 'it'\''s' expands to it's.
        var script = Script(new[] { "sh", "-c", "echo it's fine" }, 1);

        script.ShouldContain("'echo it'\\''s fine'");
    }

    [Fact]
    public void BuildScript_never_lets_the_shell_re_split_a_token_containing_spaces()
    {
        // A NAIVE `sh -c "$COMMAND"` embedding would re-split "sh check.sh" into ["sh", "check.sh"] correctly by luck,
        // but would SILENTLY corrupt a token that itself contains an internal space (e.g. a path with a space) by
        // re-splitting it into two argv entries. The set -- 'token' form quotes each token as ONE atomic unit.
        var script = Script(new[] { "sh", "my check.sh" }, 1);

        script.ShouldContain("set -- 'sh' 'my check.sh'", Case.Sensitive,
            "the space-containing token must stay ONE quoted argv entry, never split into two");
    }

    [Fact]
    public void BuildScript_exits_2_with_a_legible_stderr_reason_on_a_genuine_failure_path()
    {
        var script = Script(new[] { "sh", "check.sh" }, 1);

        script.ShouldContain("exit 2");
        script.ShouldContain("still failing", Case.Insensitive);
    }

    [Fact]
    public void BuildScript_treats_a_launch_failure_126_or_127_as_infra_not_a_genuine_failure()
    {
        // Mirrors AgentAcceptanceContract.IsInfraFailure's own philosophy: the check machinery couldn't even RUN,
        // so no verdict was reached — that's fail-soft territory, not "the agent's code is wrong."
        Script(new[] { "sh", "check.sh" }, 1)
            .ShouldContain("[ \"$CHECK_EXIT\" -lt 126 ] || exit 0");
    }

    [Fact]
    public void BuildScript_treats_an_unreadable_or_non_numeric_counter_as_zero_never_crashing()
    {
        Script(new[] { "sh", "check.sh" }, 1)
            .ShouldContain("case \"$COUNT\" in ''|*[!0-9]*) COUNT=0 ;; esac");
    }

    [Fact]
    public void BuildScript_gives_up_fail_soft_when_the_counter_cannot_be_persisted()
    {
        Script(new[] { "sh", "check.sh" }, 1)
            .ShouldContain("echo \"$NEXT\" > \"$COUNTER_FILE\" 2>/dev/null || exit 0");
    }

    [Fact]
    public void ScriptRelativePath_is_pinned()
    {
        InLoopAcceptanceHook.ScriptRelativePath.ShouldBe("hooks/stop-acceptance-check.sh");
    }

    // ── The golden pin: parameterizing the config home changed that ONE line and nothing else ──

    /// <summary>
    /// The complete script <see cref="InLoopAcceptanceHook.BuildScript"/> emitted for <c>['sh','check.sh']</c> at
    /// <c>maxBlocks: 1</c> BEFORE it took a config-home-variable parameter, captured by running the parent commit's
    /// code. The literal omits the script's trailing newline, which <see cref="PreParameterizationScript"/> appends.
    /// </summary>
    private const string PreParameterizationScriptBody = """
        #!/bin/sh
        # CodeSpace in-loop acceptance Stop hook (P3.3), generated per run. Fail-soft: any problem here
        # (unreadable counter dir, the check binary missing, anything unexpected) lets the harness stop —
        # the control-plane grader is the unconditional final judge regardless of what this hook decides.
        cat >/dev/null 2>&1
        CFG="${CLAUDE_CONFIG_DIR:-$CODEX_HOME}"
        [ -n "$CFG" ] || exit 0
        COUNTER_FILE="$CFG/hooks/.stop-hook-counter"
        MAX_BLOCKS=1
        COUNT=$(cat "$COUNTER_FILE" 2>/dev/null)
        case "$COUNT" in ''|*[!0-9]*) COUNT=0 ;; esac
        [ "$COUNT" -lt "$MAX_BLOCKS" ] || exit 0
        set -- 'sh' 'check.sh'
        OUTPUT_FILE="$CFG/hooks/.stop-hook-output"
        "$@" >"$OUTPUT_FILE" 2>&1
        CHECK_EXIT=$?
        [ "$CHECK_EXIT" -ne 0 ] || exit 0
        [ "$CHECK_EXIT" -lt 126 ] || exit 0
        NEXT=$((COUNT + 1))
        echo "$NEXT" > "$COUNTER_FILE" 2>/dev/null || exit 0
        REASON=$(tail -c 800 "$OUTPUT_FILE" 2>/dev/null)
        printf 'In-loop acceptance check still failing (attempt %s of %s). Output:\n%s\n' "$NEXT" "$MAX_BLOCKS" "$REASON" >&2
        exit 2
        """;

    private static readonly string PreParameterizationScript = PreParameterizationScriptBody + "\n";

    /// <summary>The one line the parameter replaced — the two-name guess that left <c>$CFG</c> empty for any third harness.</summary>
    private const string PreParameterizationConfigHomeLine = "CFG=\"${CLAUDE_CONFIG_DIR:-$CODEX_HOME}\"\n";

    [Theory]
    [InlineData("CLAUDE_CONFIG_DIR")]
    [InlineData("CODEX_HOME")]
    public void BuildScript_differs_from_the_pre_parameterization_body_in_the_config_home_line_ALONE(string configHomeEnvVar)
    {
        // Both shipped harnesses export exactly ONE of these two names, so reading their own name instead of the
        // guess leaves their RUNTIME behavior identical — and putting the old line back has to reproduce the old
        // bytes exactly. Byte equality is the proof no other exit path, guard, counter step or reason string moved
        // while the parameter was threaded through. Change any of them and this fails until it is re-pinned.
        var script = InLoopAcceptanceHook.BuildScript(new[] { "sh", "check.sh" }, 1, configHomeEnvVar);

        var reverted = script.Replace($"CFG=\"${configHomeEnvVar}\"\n", PreParameterizationConfigHomeLine);

        reverted.ShouldBe(PreParameterizationScript,
            "restoring the old config-home line must reproduce the pre-parameterization script byte for byte — anything else means this change altered live hook behavior beyond which variable it reads");
    }
}
