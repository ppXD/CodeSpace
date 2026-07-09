using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the pure, harness-agnostic half of P3.3's in-loop verify: which tasks get a Stop hook wired
/// (<see cref="InLoopAcceptanceHook.AppliesTo"/>), the operator's block-ceiling override
/// (<see cref="InLoopAcceptanceHook.MaxBlocks"/>), and the exact generated script shape
/// (<see cref="InLoopAcceptanceHook.BuildScript"/>) — argv-safe quoting, the fail-soft exit paths, and the
/// CodeSpace-owned counter (never the harness's own native block cap). The generated script's actual RUNTIME
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

    [Fact]
    public void BuildScript_drains_stdin_and_never_parses_it()
    {
        // The fail-soft guarantee starts here: a malformed hook payload can never reach any parsing logic because
        // there IS none — stdin is piped straight to /dev/null.
        InLoopAcceptanceHook.BuildScript(new[] { "sh", "check.sh" }, 1).ShouldContain("cat >/dev/null 2>&1");
    }

    [Fact]
    public void BuildScript_resolves_either_harnesss_config_home_with_the_shared_fallback_idiom()
    {
        InLoopAcceptanceHook.BuildScript(new[] { "sh", "check.sh" }, 1)
            .ShouldContain("CFG=\"${CLAUDE_CONFIG_DIR:-$CODEX_HOME}\"");
    }

    [Fact]
    public void BuildScript_bakes_in_the_max_blocks_value()
    {
        InLoopAcceptanceHook.BuildScript(new[] { "sh", "check.sh" }, 2).ShouldContain("MAX_BLOCKS=2");
    }

    [Fact]
    public void BuildScript_quotes_a_plain_argv_safely()
    {
        var script = InLoopAcceptanceHook.BuildScript(new[] { "sh", "check.sh" }, 1);

        script.ShouldContain("set -- 'sh' 'check.sh'");
    }

    [Fact]
    public void BuildScript_escapes_an_embedded_single_quote_so_the_token_reaches_the_check_unchanged()
    {
        // The standard POSIX trick: close the quote, escaped-quote, reopen — 'it'\''s' expands to it's.
        var script = InLoopAcceptanceHook.BuildScript(new[] { "sh", "-c", "echo it's fine" }, 1);

        script.ShouldContain("'echo it'\\''s fine'");
    }

    [Fact]
    public void BuildScript_never_lets_the_shell_re_split_a_token_containing_spaces()
    {
        // A NAIVE `sh -c "$COMMAND"` embedding would re-split "sh check.sh" into ["sh", "check.sh"] correctly by luck,
        // but would SILENTLY corrupt a token that itself contains an internal space (e.g. a path with a space) by
        // re-splitting it into two argv entries. The set -- 'token' form quotes each token as ONE atomic unit.
        var script = InLoopAcceptanceHook.BuildScript(new[] { "sh", "my check.sh" }, 1);

        script.ShouldContain("set -- 'sh' 'my check.sh'", Case.Sensitive,
            "the space-containing token must stay ONE quoted argv entry, never split into two");
    }

    [Fact]
    public void BuildScript_exits_2_with_a_legible_stderr_reason_on_a_genuine_failure_path()
    {
        var script = InLoopAcceptanceHook.BuildScript(new[] { "sh", "check.sh" }, 1);

        script.ShouldContain("exit 2");
        script.ShouldContain("still failing", Case.Insensitive);
    }

    [Fact]
    public void BuildScript_treats_a_launch_failure_126_or_127_as_infra_not_a_genuine_failure()
    {
        // Mirrors AgentAcceptanceContract.IsInfraFailure's own philosophy: the check machinery couldn't even RUN,
        // so no verdict was reached — that's fail-soft territory, not "the agent's code is wrong."
        InLoopAcceptanceHook.BuildScript(new[] { "sh", "check.sh" }, 1)
            .ShouldContain("[ \"$CHECK_EXIT\" -lt 126 ] || exit 0");
    }

    [Fact]
    public void BuildScript_treats_an_unreadable_or_non_numeric_counter_as_zero_never_crashing()
    {
        InLoopAcceptanceHook.BuildScript(new[] { "sh", "check.sh" }, 1)
            .ShouldContain("case \"$COUNT\" in ''|*[!0-9]*) COUNT=0 ;; esac");
    }

    [Fact]
    public void BuildScript_gives_up_fail_soft_when_the_counter_cannot_be_persisted()
    {
        InLoopAcceptanceHook.BuildScript(new[] { "sh", "check.sh" }, 1)
            .ShouldContain("echo \"$NEXT\" > \"$COUNTER_FILE\" 2>/dev/null || exit 0");
    }

    [Fact]
    public void ScriptRelativePath_is_pinned()
    {
        InLoopAcceptanceHook.ScriptRelativePath.ShouldBe("hooks/stop-acceptance-check.sh");
    }
}
