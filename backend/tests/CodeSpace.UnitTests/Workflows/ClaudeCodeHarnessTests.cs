using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Skills.Parsers.ClaudeCode;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the Claude Code adapter's stable contract: the exact CLI invocation (print + stream-json, which
/// requires --verbose; model omitted when blank; permission-mode from the write scope), the stream-json →
/// normalized-event mapping (text / tool_use / tool_result / result, unknown → Warning so nothing is
/// dropped), the result fold, and the air-gapped version-override env var (Rule 8). Exact stream-json field
/// names are calibrated against real output when execution is wired; these tests pin the normalization
/// shape, which is the contract.
/// </summary>
[Trait("Category", "Unit")]
public class ClaudeCodeHarnessTests
{
    private static readonly ClaudeCodeHarness Harness = new();

    private static AgentTask Task(string goal = "Fix the failing billing tests", string? model = "claude-opus-4-8", AgentWriteScope scope = AgentWriteScope.Workspace, IReadOnlyList<string>? tools = null) => new()
    {
        Goal = goal,
        Harness = ClaudeCodeHarness.HarnessKind,
        Model = model,
        Tools = tools,
        WorkspaceDirectory = "/tmp/ws",
        Permissions = new AgentPermissions { WriteScope = scope },
        TimeoutSeconds = 900,
    };

    [Fact]
    public void Kind_is_claude_code() => Harness.Kind.ShouldBe("claude-code");

    [Fact]
    public void Projects_bound_skills_into_config_home_skill_files()
    {
        var task = Task() with { Skills = new[] { new AgentSkill { Slug = "tdd", Description = "Use when implementing.", Body = "Write the test first." } } };

        var spec = Harness.BuildInvocation(task);

        spec.ConfigHomeFiles.Select(f => f.RelativePath).ShouldBe(new[] { "skills/tdd/SKILL.md" },
            customMessage: "Claude Code scans CLAUDE_CONFIG_DIR/skills/<slug>/SKILL.md — the config-home the runner sets");
        spec.ConfigHomeFiles.Single().Content.ShouldContain("Write the test first.");
    }

    [Fact]
    public void No_skills_means_no_config_home_files()
    {
        Harness.BuildInvocation(Task()).ConfigHomeFiles.ShouldBeEmpty();
    }

    [Fact]
    public void A_bound_skill_round_trips_from_build_invocation_through_materialization_to_a_parseable_skill_md()
    {
        // The end-to-end production chain in-process (no CLI binary): a resolved task carrying skills →
        // harness BuildInvocation → runner materialization → re-parse the file on disk. This is the test that
        // proves the slice's whole point — a bound skill reaches the agent as a parseable SKILL.md — and would
        // catch a skills-root drift or harness-emitted malformed frontmatter that the per-stage tests miss.
        var task = Task() with { Skills = new[] { new AgentSkill { Slug = "tdd", Description = "Use when implementing any feature.", Body = "# TDD\n\nWrite the test first." } } };

        var spec = Harness.BuildInvocation(task);

        var configHome = Path.Combine(Path.GetTempPath(), "cs-skills-" + Guid.NewGuid().ToString("N"));
        try
        {
            LocalProcessRunner.WriteConfigHomeFiles(spec.ConfigHomeFiles, configHome);

            var onDisk = File.ReadAllText(Path.Combine(configHome, "skills", "tdd", "SKILL.md"));
            var parsed = new ClaudeCodeSkillParser().Parse(onDisk, "skills/tdd/SKILL.md");

            parsed.Name.ShouldBe("tdd");
            parsed.Description.ShouldBe("Use when implementing any feature.");
            parsed.Body.ShouldBe("# TDD\n\nWrite the test first.");
            parsed.Diagnostics.ShouldBeEmpty(customMessage: "the harness-emitted bytes, written to disk, must re-parse as valid SKILL.md");
        }
        finally
        {
            if (Directory.Exists(configHome)) Directory.Delete(configHome, recursive: true);
        }
    }

    [Fact]
    public void Renders_an_mcp_server_into_a_dot_mcp_json_with_the_run_socket_and_token()
    {
        // Claude Code reads MCP-server declarations from a JSON .mcp.json in its config dir. The harness OWNS the format
        // — it renders the JSON content from the run-scoped context (socket + token + proxy command baked in).
        var context = new McpDeclarationContext { ProxyCommand = "/abs/codespace-mcp", SocketPath = "/tmp/cs/mcp.sock", Token = "tok-xyz", ServerName = "codespace" };

        var declaration = ((IMcpHarnessDeclaration)Harness).BuildMcpDeclaration(context);

        declaration.RelativeFileName.ShouldBe(".mcp.json");

        using var doc = System.Text.Json.JsonDocument.Parse(declaration.Content);   // valid JSON the harness rendered
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("codespace");
        server.GetProperty("command").GetString().ShouldBe("/abs/codespace-mcp");
        server.GetProperty("env").GetProperty("CODESPACE_MCP_SOCKET").GetString().ShouldBe("/tmp/cs/mcp.sock");
        server.GetProperty("env").GetProperty("CODESPACE_RUN_TOKEN").GetString().ShouldBe("tok-xyz");

        // Pinned (Rule 8): a rename silently relocates the declaration so the CLI never reads it.
        ClaudeCodeHarness.McpDeclarationFile.ShouldBe(".mcp.json");
    }

    [Fact]
    public void Builds_a_claude_print_stream_json_invocation_from_the_task()
    {
        var spec = Harness.BuildInvocation(Task());

        spec.Command.ShouldBe("claude");
        spec.Args.ShouldBe(new[] { "--print", "--output-format", "stream-json", "--verbose", "--append-system-prompt", AgentOperatingContract.SystemDirective, "--model", "claude-opus-4-8", "--permission-mode", "bypassPermissions", "Fix the failing billing tests" });
        spec.WorkingDirectory.ShouldBe("/tmp/ws");
        spec.TimeoutSeconds.ShouldBe(900);
    }

    [Fact]
    public void Stream_json_always_carries_verbose_since_the_cli_requires_it()
    {
        // The CLI rejects `--print --output-format stream-json` without --verbose; the invocation must always include it.
        Harness.BuildInvocation(Task()).Args.ShouldContain("--verbose");
    }

    [Fact]
    public void Builds_a_resume_invocation_when_a_prior_session_is_set()
    {
        // P3.2: a CONTINUE re-stage carries the prior session id → `--resume <id>` to pick up the conversation. It
        // sits right after the seed (before the variadic --allowed-tools / --permission-mode) so the Goal stays the
        // trailing positional and the prompt is never swallowed.
        var spec = Harness.BuildInvocation(Task() with { ResumeFromSessionId = "sess-resume-1" });

        spec.Args.ShouldBe(new[] { "--print", "--output-format", "stream-json", "--verbose", "--resume", "sess-resume-1", "--append-system-prompt", AgentOperatingContract.SystemDirective, "--model", "claude-opus-4-8", "--permission-mode", "bypassPermissions", "Fix the failing billing tests" });
    }

    [Fact]
    public void Omits_the_resume_flag_when_no_prior_session()
    {
        // Null prior session (a fresh run) → argv byte-identical to today, never a stray `--resume`.
        var spec = Harness.BuildInvocation(Task() with { ResumeFromSessionId = null });

        spec.Args.ShouldNotContain("--resume");
        spec.Args.ShouldBe(new[] { "--print", "--output-format", "stream-json", "--verbose", "--append-system-prompt", AgentOperatingContract.SystemDirective, "--model", "claude-opus-4-8", "--permission-mode", "bypassPermissions", "Fix the failing billing tests" });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Omits_the_model_flag_when_no_model_is_set(string? model)
    {
        var spec = Harness.BuildInvocation(Task(model: model));

        spec.Args.ShouldNotContain("--model", customMessage: "a blank model must omit --model so the CLI uses its own default (the Model=empty rule)");
        spec.Args.ShouldBe(new[] { "--print", "--output-format", "stream-json", "--verbose", "--append-system-prompt", AgentOperatingContract.SystemDirective, "--permission-mode", "bypassPermissions", "Fix the failing billing tests" });
    }

    [Fact]
    public void Injects_the_operating_contract_as_a_system_prompt_before_the_prompt_positional()
    {
        // B1: the unattended operating contract rides as --append-system-prompt (composing with any persona/goal), and
        // the goal stays the trailing positional — so the directive never swallows or reorders the prompt.
        var args = Harness.BuildInvocation(Task()).Args.ToList();

        var at = args.IndexOf("--append-system-prompt");
        at.ShouldBeGreaterThanOrEqualTo(0, "the operating contract is injected as a system prompt");
        args[at + 1].ShouldBe(AgentOperatingContract.SystemDirective);
        args[^1].ShouldBe("Fix the failing billing tests", "the goal remains the trailing positional argument");
    }

    [Theory]
    [InlineData(AgentWriteScope.ReadOnly, "plan")]            // analysis-only → no edits
    [InlineData(AgentWriteScope.Workspace, "bypassPermissions")] // autonomous edits, bounded by the OS sandbox
    public void Maps_the_write_scope_to_a_permission_mode(AgentWriteScope scope, string expectedMode)
    {
        var args = Harness.BuildInvocation(Task(scope: scope)).Args.ToList();

        var i = args.IndexOf("--permission-mode");
        i.ShouldBeGreaterThanOrEqualTo(0);
        args[i + 1].ShouldBe(expectedMode);
    }

    [Fact]
    public void Projects_the_tool_allow_list_before_permission_mode_so_the_prompt_is_not_swallowed()
    {
        var args = Harness.BuildInvocation(Task(tools: new[] { "Read", "Grep", "Bash" })).Args.ToList();

        // --allowed-tools + its values must sit BEFORE --permission-mode (the variadic stops at the next flag),
        // and the prompt stays the trailing positional argument.
        var toolsAt = args.IndexOf("--allowed-tools");
        var modeAt = args.IndexOf("--permission-mode");
        toolsAt.ShouldBeGreaterThanOrEqualTo(0);
        toolsAt.ShouldBeLessThan(modeAt, "the variadic --allowed-tools must be bounded by --permission-mode");
        args.GetRange(toolsAt + 1, 3).ShouldBe(new[] { "Read", "Grep", "Bash" });
        args[^1].ShouldBe("Fix the failing billing tests", "the prompt remains the trailing positional argument");
    }

    [Fact]
    public void Omits_the_tool_allow_list_when_tools_are_null_or_empty()
    {
        Harness.BuildInvocation(Task(tools: null)).Args.ShouldNotContain("--allowed-tools", customMessage: "null tools = inherit the harness default");
        Harness.BuildInvocation(Task(tools: Array.Empty<string>())).Args.ShouldNotContain("--allowed-tools");
    }

    [Fact]
    public void Parses_an_assistant_text_block_as_an_assistant_message()
    {
        var ev = Harness.ParseEvents("""{"type":"assistant","message":{"content":[{"type":"text","text":"Looking into the billing tests."}]}}""").Single();

        ev.Kind.ShouldBe(AgentEventKind.AssistantMessage);
        ev.Text.ShouldBe("Looking into the billing tests.");
    }

    [Fact]
    public void Emits_every_content_block_of_a_multi_block_assistant_turn_in_order()
    {
        // D3b-ii crown jewel: ONE assistant line routinely carries reasoning + text + a tool_use. The faithful log
        // must surface ALL of them, in stream order — not just the first block. A regression to first-block-only
        // (the prior behavior) silently drops the agent's reasoning and its tool call from the durable trace.
        var events = Harness.ParseEvents("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"The failing test is in Invoice.cs — I should read it first."},{"type":"text","text":"Let me inspect the billing code."},{"type":"tool_use","name":"Bash","input":{"command":"npm test"}}]}}""");

        events.Count.ShouldBe(3, "every block becomes its own event — reasoning, message, and tool call");

        events[0].Kind.ShouldBe(AgentEventKind.Reasoning, "the thinking block is captured as reasoning — not dropped");
        events[0].Text.ShouldBe("The failing test is in Invoice.cs — I should read it first.");

        events[1].Kind.ShouldBe(AgentEventKind.AssistantMessage);
        events[1].Text.ShouldBe("Let me inspect the billing code.");

        events[2].Kind.ShouldBe(AgentEventKind.CommandExecuted, "the tool_use after the text is still surfaced");
        events[2].Text.ShouldBe("npm test");
    }

    [Fact]
    public void A_thinking_only_assistant_turn_surfaces_one_reasoning_event()
    {
        var events = Harness.ParseEvents("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"Planning the fix."}]}}""");

        events.ShouldHaveSingleItem();
        events[0].Kind.ShouldBe(AgentEventKind.Reasoning);
        events[0].Text.ShouldBe("Planning the fix.");
    }

    [Fact]
    public void An_assistant_turn_with_two_tool_use_blocks_emits_both()
    {
        // The exact regression first-block-only caused: a turn that calls two tools must surface BOTH, in order
        // — the prior code returned on the first block and silently dropped the second tool call.
        var events = Harness.ParseEvents("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"src/a.cs"}},{"type":"tool_use","name":"Bash","input":{"command":"npm test"}}]}}""");

        events.Count.ShouldBe(2, "both tool_use blocks of the same turn are surfaced, not just the first");
        events[0].Kind.ShouldBe(AgentEventKind.ToolCall);
        events[0].Text.ShouldBe("src/a.cs");
        events[1].Kind.ShouldBe(AgentEventKind.CommandExecuted);
        events[1].Text.ShouldBe("npm test");
    }

    [Fact]
    public void An_assistant_turn_with_no_meaningful_blocks_emits_nothing()
    {
        // An empty content array (or only unrecognized / empty blocks) yields zero events — never a phantom row.
        Harness.ParseEvents("""{"type":"assistant","message":{"content":[]}}""").ShouldBeEmpty();
    }

    [Theory]
    // shell tool → command; edit/write tools → file change; anything else → a generic tool call.
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"npm test"}}]}}""", AgentEventKind.CommandExecuted, "npm test")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"src/Invoice.cs"}}]}}""", AgentEventKind.FileChanged, "src/Invoice.cs")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"src/Invoice.cs"}}]}}""", AgentEventKind.ToolCall, "src/Invoice.cs")]
    public void Classifies_tool_use_blocks_by_tool_name(string json, AgentEventKind expected, string expectedText)
    {
        var ev = Harness.ParseEvents(json).Single();

        ev.Kind.ShouldBe(expected);
        ev.Text.ShouldBe(expectedText, customMessage: "the tool line renders its most descriptive input field");
    }

    [Fact]
    public void Parses_a_tool_result_as_command_output()
    {
        var ev = Harness.ParseEvents("""{"type":"user","message":{"content":[{"type":"tool_result","content":"3 passing, 0 failing"}]}}""").Single();

        ev.Kind.ShouldBe(AgentEventKind.CommandExecuted);
        ev.Text.ShouldBe("3 passing, 0 failing");
    }

    [Fact]
    public void Parses_the_result_event_as_completed_with_its_summary()
    {
        var ev = Harness.ParseEvents("""{"type":"result","subtype":"success","result":"Fixed the billing tests.","is_error":false}""").Single();

        ev.Kind.ShouldBe(AgentEventKind.Completed);
        ev.Text.ShouldBe("Fixed the billing tests.");
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"error_during_execution","result":"API Error: Request rejected (429) AccountQuotaExceeded","is_error":true}""")]
    [InlineData("""{"type":"result","subtype":"error_max_turns","result":"Reached the turn limit."}""")]   // is_error absent → fall back to the error-flavored subtype
    public void Parses_an_error_result_event_as_an_error_not_completed(string json)
    {
        // A failed result must surface as Error so the timeline doesn't render a failure as a clean "done".
        var ev = Harness.ParseEvents(json).Single();

        ev.Kind.ShouldBe(AgentEventKind.Error);
    }

    [Fact]
    public void System_init_lines_carry_no_event()
    {
        Harness.ParseEvents("""{"type":"system","subtype":"init","cwd":"/tmp/ws"}""").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]                 // valid json but not an object
    [InlineData("""{"foo":"bar"}""")]       // object with no type
    public void Returns_no_events_for_blank_or_typeless_lines(string line)
    {
        Harness.ParseEvents(line).ShouldBeEmpty();
    }

    [Fact]
    public void An_unknown_event_type_is_surfaced_as_a_warning_never_dropped()
    {
        var ev = Harness.ParseEvents("""{"type":"some_future_event_kind","detail":"x"}""").Single();

        ev.Kind.ShouldBe(AgentEventKind.Warning);
        ev.Text.ShouldBe("some_future_event_kind");
    }

    [Fact]
    public void Build_result_folds_a_successful_run()
    {
        var events = new[]
        {
            new AgentEvent { Kind = AgentEventKind.FileChanged, Text = "src/Invoice.cs" },
            new AgentEvent { Kind = AgentEventKind.FileChanged, Text = "src/Invoice.cs" },   // duplicate collapses
            new AgentEvent { Kind = AgentEventKind.CommandExecuted, Text = "npm test" },
            new AgentEvent { Kind = AgentEventKind.Completed, Text = "Fixed the billing tests." },
        };

        var result = Harness.BuildResult(events, exitCode: 0);

        result.Status.ShouldBe(AgentRunStatus.Succeeded);
        result.ExitReason.ShouldBe("completed");
        result.Summary.ShouldBe("Fixed the billing tests.");
        result.ChangedFiles.ShouldBe(new[] { "src/Invoice.cs" });
    }

    [Fact]
    public void Build_result_folds_a_failed_run_with_the_last_error()
    {
        var events = new[] { new AgentEvent { Kind = AgentEventKind.Error, Text = "patch did not apply" } };

        var result = Harness.BuildResult(events, exitCode: 1);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.ExitReason.ShouldBe("non-zero-exit");
        result.Error.ShouldBe("patch did not apply");
    }

    [Fact]
    public void Build_result_populates_token_usage_from_the_result_lines_usage_object()
    {
        // D3b-i: Claude's final result line carries a usage object; parsed via the harness (so Data is the
        // real root) it must surface as AgentRunResult.TokenUsage for cost accounting.
        var resultLine = Harness.ParseEvents("""{"type":"result","subtype":"success","result":"done","is_error":false,"usage":{"input_tokens":920,"output_tokens":175,"cache_read_input_tokens":40}}""").Single();
        var events = new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "working" }, resultLine };

        var result = Harness.BuildResult(events, exitCode: 0);

        result.TokenUsage.ShouldNotBeNull("the run's token usage is captured for cost accounting");
        result.TokenUsage!.InputTokens.ShouldBe(920);
        result.TokenUsage.OutputTokens.ShouldBe(175);
    }

    [Fact]
    public void Build_result_leaves_token_usage_null_when_the_stream_reported_none()
    {
        var events = new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "done" } };

        Harness.BuildResult(events, exitCode: 0).TokenUsage.ShouldBeNull("no usage object → no figure, never a fabricated zero");
    }

    [Fact]
    public void Build_result_captures_the_session_id_from_the_result_line()
    {
        // P3.1a: Claude's result line carries the run's session_id; parsed via the harness (so Data is the real
        // root) it must surface on AgentRunResult.SessionId so a later rerun can `claude --resume <id>`.
        var resultLine = Harness.ParseEvents("""{"type":"result","subtype":"success","result":"done","is_error":false,"session_id":"sess-claude-7f3a"}""").Single();
        var events = new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "working" }, resultLine };

        Harness.BuildResult(events, exitCode: 0).SessionId.ShouldBe("sess-claude-7f3a", "the captured session id is the handle a CONTINUE resumes");
    }

    [Fact]
    public void Build_result_leaves_session_id_null_when_the_stream_carried_none()
    {
        // A result line with no session_id (or a pre-session CLI) → null, byte-identical to today; never fabricated.
        var resultLine = Harness.ParseEvents("""{"type":"result","subtype":"success","result":"done","is_error":false}""").Single();

        Harness.BuildResult(new[] { resultLine }, exitCode: 0).SessionId.ShouldBeNull("no session_id in the stream → null");
    }

    [Fact]
    public void Build_result_captures_the_session_id_even_on_a_failed_run()
    {
        // A FAILED run still has a resumable conversation — the failure return must carry SessionId too, so a rerun
        // can CONTINUE from where it broke (the whole point of P3 for the "continue-from-where-it-broke" intent).
        var errorResult = Harness.ParseEvents("""{"type":"result","subtype":"error_during_execution","result":"API Error (429)","is_error":true,"session_id":"sess-claude-failed"}""").Single();

        Harness.BuildResult(new[] { errorResult }, exitCode: 1).SessionId.ShouldBe("sess-claude-failed", "a failed run's session id is captured so a rerun can continue the broken conversation");
    }

    [Fact]
    public void Build_result_falls_back_to_an_exit_code_error_when_no_error_event()
    {
        Harness.BuildResult(Array.Empty<AgentEvent>(), exitCode: 137).Error.ShouldContain("137");
    }

    [Fact]
    public void Build_result_surfaces_the_final_summary_as_the_error_when_a_failed_run_has_no_error_event()
    {
        // The CLI printed its failure reason as a final message (e.g. a gateway 401) but emitted no
        // structured Error event — the run must still fail with that reason, not an opaque exit code.
        var events = new[] { new AgentEvent { Kind = AgentEventKind.FinalSummary, Text = "API Error: 401 Authentication Error" } };

        var result = Harness.BuildResult(events, exitCode: 1);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.Error.ShouldBe("API Error: 401 Authentication Error");
        result.Summary.ShouldBe("API Error: 401 Authentication Error");
    }

    [Fact]
    public void Version_env_var_constant_name_is_pinned()
    {
        // Renaming this breaks every air-gapped operator who pinned a private build via env. Hard-pin (Rule 8).
        ClaudeCodeHarness.VersionEnvVar.ShouldBe("CODESPACE_CLAUDE_CODE_VERSION");
    }

    [Fact]
    public void Version_is_overridable_by_the_env_var()
    {
        var original = System.Environment.GetEnvironmentVariable(ClaudeCodeHarness.VersionEnvVar);
        try
        {
            System.Environment.SetEnvironmentVariable(ClaudeCodeHarness.VersionEnvVar, "9.9.9-pinned");
            new ClaudeCodeHarness().Version.ShouldBe("9.9.9-pinned");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(ClaudeCodeHarness.VersionEnvVar, original);
        }
    }

    [Fact]
    public void CommandEnvVar_constant_name_is_pinned()
    {
        // Renaming this breaks every air-gapped operator who repointed the Claude Code binary via env.
        ClaudeCodeHarness.CommandEnvVar.ShouldBe("CODESPACE_CLAUDE_CODE_PATH");
    }

    [Fact]
    public void Requests_config_dir_isolation_so_the_agent_ignores_the_operators_personal_claude_config() =>
        // The runner redirects this var to a per-run isolated dir, so a run never reads the operator's
        // ~/.claude (whose env.ANTHROPIC_BASE_URL would otherwise override our injected gateway → ConnectionRefused).
        Harness.BuildInvocation(Task()).ConfigHomeEnvVars.ShouldBe(new[] { "CLAUDE_CONFIG_DIR" });

    [Fact]
    public void ConfigDirEnvVar_constant_name_is_pinned() =>
        // Claude Code reads its config dir from this exact name; renaming it would silently re-leak the operator's ~/.claude.
        ClaudeCodeHarness.ConfigDirEnvVar.ShouldBe("CLAUDE_CONFIG_DIR");

    [Theory]
    [InlineData(AgentNetworkAccess.On, true)]
    [InlineData(AgentNetworkAccess.Off, false)]
    public void Projects_the_network_permission_onto_AllowNetwork(AgentNetworkAccess network, bool expected) =>
        // The sandbox runner severs egress when AllowNetwork is false — so the (previously dead) Network toggle finally bites.
        Harness.BuildInvocation(Task() with { Permissions = new AgentPermissions { Network = network } }).AllowNetwork.ShouldBe(expected);

    [Fact]
    public void Disables_non_essential_traffic_for_an_allowlist_egress_run()
    {
        // B3.3c: a deny-by-default (Allowlist) egress run pins only model + git hosts, so the harness asks the Claude CLI
        // to disable non-essential traffic — otherwise it would stall reaching telemetry hosts (statsig) the allowlist drops.
        var env = Harness.BuildInvocation(Task() with { Permissions = new AgentPermissions { Egress = AgentEgressPolicy.Allowlist } }).Environment;

        env[ClaudeCodeHarness.DisableNonEssentialTrafficEnvVar].ShouldBe("1");
    }

    [Fact]
    public void Leaves_non_essential_traffic_untouched_for_a_full_egress_run() =>
        // Full egress (the default) reaches every host — nothing to disable, and the env stays byte-identical to before.
        Harness.BuildInvocation(Task()).Environment.ContainsKey(ClaudeCodeHarness.DisableNonEssentialTrafficEnvVar).ShouldBeFalse();

    [Fact]
    public void An_explicit_task_env_value_for_the_disable_flag_wins_over_the_harness_default()
    {
        // Operator intent wins (the runner's NonInteractiveEnv convention): an explicit task env entry is layered last,
        // so the harness default never clobbers it.
        var task = Task() with
        {
            Permissions = new AgentPermissions { Egress = AgentEgressPolicy.Allowlist },
            Environment = new Dictionary<string, string> { [ClaudeCodeHarness.DisableNonEssentialTrafficEnvVar] = "0" },
        };

        Harness.BuildInvocation(task).Environment[ClaudeCodeHarness.DisableNonEssentialTrafficEnvVar].ShouldBe("0");
    }

    [Fact]
    public void Adds_the_disable_flag_alongside_unrelated_task_env_entries_under_allowlist()
    {
        // The overlay ADDS the flag — it must not drop the operator's other env entries (the foreach preserves foreign keys).
        var task = Task() with
        {
            Permissions = new AgentPermissions { Egress = AgentEgressPolicy.Allowlist },
            Environment = new Dictionary<string, string> { ["ANTHROPIC_BASE_URL"] = "https://gw.example" },
        };

        var env = Harness.BuildInvocation(task).Environment;

        env["ANTHROPIC_BASE_URL"].ShouldBe("https://gw.example", "the operator's other env entries survive the overlay");
        env[ClaudeCodeHarness.DisableNonEssentialTrafficEnvVar].ShouldBe("1", "the disable flag is added alongside them");
    }

    // A Custom gateway is signalled by the projected ANTHROPIC_AUTH_TOKEN (ProjectToEnv sets it ONLY for non-Anthropic).
    private static AgentTask Gateway(string? model, params (string, string)[] extraEnv)
    {
        var env = new Dictionary<string, string> { [ClaudeCodeHarness.AuthTokenEnvVar] = "tok", ["ANTHROPIC_BASE_URL"] = "https://gw.example/v1" };
        foreach (var (k, v) in extraEnv) env[k] = v;
        return Task(model: model) with { Environment = env };
    }

    [Fact]
    public void Pins_every_background_model_tier_to_the_run_model_on_a_gateway()
    {
        // On a custom gateway Claude Code would otherwise resolve haiku/sonnet/opus/small-fast/subagent to DEFAULT
        // Anthropic model names the gateway doesn't host — pin them ALL to the run's model so none escapes.
        var env = Harness.BuildInvocation(Gateway("metis-coder-max")).Environment;

        env[ClaudeCodeHarness.SmallFastModelEnvVar].ShouldBe("metis-coder-max");
        env[ClaudeCodeHarness.DefaultHaikuModelEnvVar].ShouldBe("metis-coder-max");
        env[ClaudeCodeHarness.DefaultSonnetModelEnvVar].ShouldBe("metis-coder-max");
        env[ClaudeCodeHarness.DefaultOpusModelEnvVar].ShouldBe("metis-coder-max");
        env[ClaudeCodeHarness.SubagentModelEnvVar].ShouldBe("metis-coder-max");
    }

    [Fact]
    public void Leaves_background_tiers_untouched_on_direct_anthropic_no_base_url()
    {
        // Direct Anthropic (no base URL, no auth-token) serves real haiku — don't override; the tiers stay Claude's defaults.
        var env = Harness.BuildInvocation(Task(model: "claude-opus-4-8")).Environment;

        env.ContainsKey(ClaudeCodeHarness.SmallFastModelEnvVar).ShouldBeFalse();
        env.ContainsKey(ClaudeCodeHarness.DefaultHaikuModelEnvVar).ShouldBeFalse();
    }

    [Fact]
    public void Leaves_background_tiers_untouched_for_an_anthropic_proxy_with_a_base_url()
    {
        // An Anthropic-provider PROXY authenticates with the api-key (not the gateway auth-token) + a base URL — it fronts
        // REAL haiku, so the tiers must NOT be pinned. The auth-token (absent here) is the discriminator, not the base URL.
        var task = Task(model: "claude-opus-4-8") with
        {
            Environment = new Dictionary<string, string> { [ClaudeCodeHarness.ApiKeyEnvVar] = "sk-ant", ["ANTHROPIC_BASE_URL"] = "https://proxy.internal" },
        };

        Harness.BuildInvocation(task).Environment.ContainsKey(ClaudeCodeHarness.SmallFastModelEnvVar).ShouldBeFalse();
    }

    [Fact]
    public void A_blank_model_pins_no_tiers_even_on_a_gateway()
    {
        // No run model → nothing to pin to (Claude picks its own default); the gateway pinning must not write a blank.
        Harness.BuildInvocation(Gateway(model: null)).Environment.ContainsKey(ClaudeCodeHarness.SmallFastModelEnvVar).ShouldBeFalse();
    }

    [Fact]
    public void An_operator_small_fast_model_wins_over_the_gateway_pin()
    {
        // Operator intent wins (layered last): an explicit small-fast model is kept, not clobbered by the run-model pin.
        var task = Gateway("metis-coder-max", (ClaudeCodeHarness.SmallFastModelEnvVar, "metis-coder-auto"));

        Harness.BuildInvocation(task).Environment[ClaudeCodeHarness.SmallFastModelEnvVar].ShouldBe("metis-coder-auto");
    }

    [Fact]
    public void Gateway_tier_pins_and_the_allowlist_disable_flag_coexist()
    {
        // The two harness-injected concerns land in the same overlay dict — a deny-by-default gateway run gets BOTH.
        var task = Gateway("metis-coder-max") with { Permissions = new AgentPermissions { Egress = AgentEgressPolicy.Allowlist } };
        var env = Harness.BuildInvocation(task).Environment;

        env[ClaudeCodeHarness.SmallFastModelEnvVar].ShouldBe("metis-coder-max");
        env[ClaudeCodeHarness.DisableNonEssentialTrafficEnvVar].ShouldBe("1");
    }

    [Fact]
    public void Background_model_env_var_names_are_pinned()
    {
        // Rule 8: these are the contract Claude Code resolves its non-primary models from — a rename silently re-breaks haiku.
        ClaudeCodeHarness.SmallFastModelEnvVar.ShouldBe("ANTHROPIC_SMALL_FAST_MODEL");
        ClaudeCodeHarness.DefaultHaikuModelEnvVar.ShouldBe("ANTHROPIC_DEFAULT_HAIKU_MODEL");
        ClaudeCodeHarness.DefaultSonnetModelEnvVar.ShouldBe("ANTHROPIC_DEFAULT_SONNET_MODEL");
        ClaudeCodeHarness.DefaultOpusModelEnvVar.ShouldBe("ANTHROPIC_DEFAULT_OPUS_MODEL");
        ClaudeCodeHarness.SubagentModelEnvVar.ShouldBe("CLAUDE_CODE_SUBAGENT_MODEL");
    }

    [Theory]
    [InlineData("http://gw:40000", "http://gw:40000")]        // root → unchanged (SDK appends /v1/messages)
    [InlineData("http://gw:40000/", "http://gw:40000")]       // trailing slash trimmed
    [InlineData("http://gw:40000/v1", "http://gw:40000")]     // /v1 stripped (else /v1/v1/messages → 404)
    [InlineData("http://gw:40000/v1/", "http://gw:40000")]    // /v1 + slash stripped
    // A path-prefixed gateway: strip the trailing /v1 too → SDK re-adds /v1/messages giving host/api/v1/messages. The
    // SAME stored host/api/v1 stays host/api/v1 under Codex (it appends /responses) — different bases per wire, both right.
    [InlineData("https://gw/api/v1", "https://gw/api")]
    public void StripVersionSuffix_removes_a_trailing_v1(string input, string expected) =>
        ClaudeCodeHarness.StripVersionSuffix(input).ShouldBe(expected);

    [Fact]
    public void ProjectToEnv_strips_a_trailing_v1_from_the_base_url()
    {
        // An operator who entered the OpenAI-style host/v1 (or shares one URL with the Codex harness) must still connect.
        var env = Harness.ProjectToEnv(new ResolvedModelCredential { Provider = "Custom", ApiKey = "tok", BaseUrl = "http://gw:40000/v1" });

        env[ClaudeCodeHarness.BaseUrlEnvVar].ShouldBe("http://gw:40000");
    }

    [Fact]
    public void Delivers_skip_webfetch_preflight_on_an_allowlist_egress_run()
    {
        // The one inference-adjacent escape our env doesn't close — WebFetch preflights api.anthropic.com (off-allowlist).
        var args = Harness.BuildInvocation(Task() with { Permissions = new AgentPermissions { Egress = AgentEgressPolicy.Allowlist } }).Args;

        args.ShouldContain("--settings");
        args.ShouldContain("{\"skipWebFetchPreflight\":true}");
    }

    [Fact]
    public void No_webfetch_settings_on_a_full_egress_run() =>
        Harness.BuildInvocation(Task()).Args.ShouldNotContain("--settings");

    [Fact]
    public void DisableNonEssentialTrafficEnvVar_constant_name_is_pinned() =>
        // The Claude CLI reads this exact name to suppress telemetry/gating traffic; renaming it would silently re-stall
        // deny-by-default egress runs on an unreachable telemetry host.
        ClaudeCodeHarness.DisableNonEssentialTrafficEnvVar.ShouldBe("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC");

    [Fact]
    public void Command_uses_the_default_then_the_env_override()
    {
        var original = System.Environment.GetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar);
        try
        {
            System.Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, null);
            Harness.BuildInvocation(Task()).Command.ShouldBe("claude", "default binary name when the env var is unset");

            System.Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, "/opt/claude/bin/claude");
            Harness.BuildInvocation(Task()).Command.ShouldBe("/opt/claude/bin/claude", "the override redirects the spawned binary");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, original);
        }
    }
}
