using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the Codex adapter's stable contract: the exact CLI invocation it builds, the native-stream →
/// normalized-event mapping (incl. unknown → Warning so nothing is dropped), the result fold, and the
/// air-gapped version-override env var (Rule 8). Exact Codex JSONL field names are calibrated against
/// real output at B0.4; these tests pin the normalization shape, which is the contract.
/// </summary>
[Trait("Category", "Unit")]
public class CodexHarnessTests
{
    private static readonly CodexHarness Harness = new();

    private static AgentTask Task(string goal = "Fix the failing billing tests", string? model = "gpt-5.3-codex", AgentWriteScope scope = AgentWriteScope.Workspace) => new()
    {
        Goal = goal,
        Harness = CodexHarness.HarnessKind,
        Model = model,
        WorkspaceDirectory = "/tmp/ws",
        Permissions = new AgentPermissions { WriteScope = scope },
        TimeoutSeconds = 900,
    };

    [Fact]
    public void Kind_is_codex_cli() => Harness.Kind.ShouldBe("codex-cli");

    [Fact]
    public void Projects_bound_skills_into_config_home_skill_files()
    {
        var task = Task() with { Skills = new[] { new AgentSkill { Slug = "tdd", Description = "Use when implementing.", Body = "Write the test first." } } };

        var spec = Harness.BuildInvocation(task);

        spec.ConfigHomeFiles.Select(f => f.RelativePath).ShouldBe(new[] { "skills/tdd/SKILL.md" },
            customMessage: "Codex scans CODEX_HOME/skills/<slug>/SKILL.md — the per-run config home the runner sets");
        spec.ConfigHomeFiles.Single().Content.ShouldContain("Write the test first.");
    }

    [Fact]
    public void No_skills_means_no_config_home_files()
    {
        Harness.BuildInvocation(Task()).ConfigHomeFiles.ShouldBeEmpty();
    }

    [Fact]
    public void SkillsRoot_is_pinned_to_the_codex_home_relative_skills_dir()
    {
        // Rule 8: pin the projection-root constant so an ACCIDENTAL edit is a deliberate, visible change. ($CODEX_HOME/skills
        // is Codex's backward-compat path; this pins the CONSTANT, not Codex's runtime behavior — a version bump that
        // dropped the path would surface only in a real-codex E2E, so re-verify on a Codex upgrade.)
        CodexHarness.SkillsRoot.ShouldBe("skills");
    }

    [Fact]
    public void Renders_an_mcp_server_into_config_toml_with_the_run_socket_and_token()
    {
        // Codex reads MCP-server declarations from an [mcp_servers.<name>] table in its config home's config.toml. The
        // harness OWNS the format — it renders the TOML content from the run-scoped context (socket + token + proxy).
        var context = new McpDeclarationContext { ProxyCommand = "/abs/codespace-mcp", SocketPath = "/tmp/cs/mcp.sock", Token = "tok-xyz", ServerName = "codespace" };

        var declaration = ((IMcpHarnessDeclaration)Harness).BuildMcpDeclaration(context);

        declaration.RelativeFileName.ShouldBe("config.toml");
        declaration.Content.ShouldContain("[mcp_servers.codespace]");
        declaration.Content.ShouldContain("command = \"/abs/codespace-mcp\"");
        declaration.Content.ShouldContain("CODESPACE_MCP_SOCKET = \"/tmp/cs/mcp.sock\"");
        declaration.Content.ShouldContain("CODESPACE_RUN_TOKEN = \"tok-xyz\"");

        // Pinned (Rule 8): a rename silently relocates the declaration so the CLI never reads it.
        CodexHarness.McpDeclarationFile.ShouldBe("config.toml");
    }

    [Fact]
    public void Builds_a_codex_exec_json_invocation_from_the_task()
    {
        var spec = Harness.BuildInvocation(Task());

        spec.Command.ShouldBe("codex");
        spec.Args.ShouldBe(new[] { "exec", "--json", "--model", "gpt-5.3-codex", "--sandbox", "workspace-write", "Fix the failing billing tests" });
        spec.WorkingDirectory.ShouldBe("/tmp/ws");
        spec.TimeoutSeconds.ShouldBe(900);
    }

    [Fact]
    public void Builds_a_resume_invocation_when_a_prior_session_is_set()
    {
        // P3.2: a CONTINUE re-stage rewrites the `exec --json` seed to `exec resume <id> --json` so Codex picks up
        // the prior thread. CRUCIAL: the sandbox rides as `-c sandbox_mode=<mode>`, NOT `--sandbox` — the resume
        // SUBCOMMAND rejects --sandbox (clap "unexpected argument", exit 2; verified against the real codex 0.142.2),
        // while -c is accepted on it and sandbox_mode is the config key the flag maps to. The Goal stays last.
        var spec = Harness.BuildInvocation(Task() with { ResumeFromSessionId = "thr-resume-1" });

        spec.Args.ShouldBe(new[] { "exec", "resume", "thr-resume-1", "--json", "--model", "gpt-5.3-codex", "-c", "sandbox_mode=workspace-write", "Fix the failing billing tests" });
    }

    [Fact]
    public void Resume_path_never_emits_the_exec_only_sandbox_flag()
    {
        // Regression guard for the real-binary defect a permissive fake-CLI test masked: `codex exec resume` rejects
        // `--sandbox` (it's an `exec`-only flag). A resume invocation must carry the confinement as `-c sandbox_mode=…`
        // and NEVER the bare flag — across read-only + workspace-write — or the run dies at clap before it starts.
        foreach (var scope in new[] { AgentWriteScope.Workspace, AgentWriteScope.ReadOnly })
        {
            var args = Harness.BuildInvocation(Task(scope: scope) with { ResumeFromSessionId = "thr-x" }).Args;

            args.ShouldNotContain("--sandbox", customMessage: $"a resume invocation (scope={scope}) must not carry the exec-only --sandbox flag");
            args.ShouldContain($"sandbox_mode={(scope == AgentWriteScope.ReadOnly ? "read-only" : "workspace-write")}", customMessage: "the confinement must still be applied via the resume-accepted -c override");
        }
    }

    [Fact]
    public void Omits_the_resume_subcommand_when_no_prior_session()
    {
        // Null prior session (a fresh run) → the plain `exec --json` seed, byte-identical to today.
        var spec = Harness.BuildInvocation(Task() with { ResumeFromSessionId = null });

        spec.Args.ShouldNotContain("resume");
        spec.Args.ShouldBe(new[] { "exec", "--json", "--model", "gpt-5.3-codex", "--sandbox", "workspace-write", "Fix the failing billing tests" });
    }

    [Fact]
    public void Restores_the_prior_rollout_under_sessions_on_a_continue()
    {
        // P3 (Codex continue producer): on a CONTINUE the restored transcript is laid at sessions/rollout-<id>.jsonl —
        // where `codex exec resume` finds it (it scans sessions/ and matches the id in the rollout filename; the
        // original timestamp is NOT needed, verified against codex 0.142.2). The Goal/argv is unchanged by the restore.
        var spec = Harness.BuildInvocation(Task() with { ResumeFromSessionId = "thr-x", RestoredTranscript = "{\"session_meta\":1}\n" });

        var rollout = spec.ConfigHomeFiles.Single(f => f.RelativePath.StartsWith("sessions/", StringComparison.Ordinal));
        rollout.RelativePath.ShouldBe("sessions/rollout-thr-x.jsonl", "codex resume matches the id in the rollout-<id> filename under sessions/");
        rollout.Content.ShouldBe("{\"session_meta\":1}\n");
    }

    [Fact]
    public void Fresh_run_restores_no_rollout_byte_identical_config_home()
    {
        // Both-or-neither: a fresh run (no resume, no transcript) adds no sessions file — byte-identical to today.
        Harness.BuildInvocation(Task()).ConfigHomeFiles.ShouldNotContain(f => f.RelativePath.StartsWith("sessions/", StringComparison.Ordinal));

        // A resume id WITHOUT the captured bytes must NOT lay a rollout (a partial restore would fail "no rollout found").
        Harness.BuildInvocation(Task() with { ResumeFromSessionId = "thr-x", RestoredTranscript = null })
            .ConfigHomeFiles.ShouldNotContain(f => f.RelativePath.StartsWith("sessions/", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionTranscriptRelativePath_globs_the_id_bearing_rollout_under_sessions()
    {
        // CAPTURE-locate: Codex writes sessions/<date>/rollout-<ts>-<id>.jsonl with a timestamp not known ahead of
        // time, so the file is FOUND by globbing the config home's sessions/ for the id-bearing rollout — not computed.
        var resumable = (IAgentSessionTranscript)Harness;
        var configHome = Path.Combine(Path.GetTempPath(), "cs-codex-locate-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(configHome, "sessions", "2026", "07", "02");
        Directory.CreateDirectory(nested);

        try
        {
            var fileName = "rollout-2026-07-02T15-24-25-thread-abc.jsonl";
            File.WriteAllText(Path.Combine(nested, fileName), "{}\n");

            resumable.SessionTranscriptRelativePath(configHome, "/tmp/ws", "thread-abc")
                .ShouldBe(Path.Combine("sessions", "2026", "07", "02", fileName), "globs the date-nested rollout whose filename carries the thread id");

            resumable.SessionTranscriptRelativePath(configHome, "/tmp/ws", "thread-does-not-exist").ShouldBeNull("no rollout carries this id → cold-start");
            resumable.SessionTranscriptRelativePath(configHome, "/tmp/ws", null).ShouldBeNull("no session id → nothing to locate");
            resumable.SessionTranscriptRelativePath(Path.Combine(configHome, "nonexistent"), "/tmp/ws", "thread-abc").ShouldBeNull("no sessions dir → cold-start, no throw");
        }
        finally
        {
            try { Directory.Delete(configHome, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public void SessionsRoot_is_pinned_to_the_codex_home_relative_sessions_dir()
    {
        // Rule 8: pin the rollout-root constant. codex resume scans $CODEX_HOME/sessions — a rename silently breaks
        // both capture (glob finds nothing) and restore (the CLI never sees the rollout). Re-verify on a Codex upgrade.
        CodexHarness.SessionsRoot.ShouldBe("sessions");
    }

    [Theory]
    [InlineData(null)]   // persona Model=empty rule: blank model → let Codex pick its own default
    [InlineData("")]
    [InlineData("   ")]
    public void Omits_the_model_flag_when_no_model_is_set(string? model)
    {
        var spec = Harness.BuildInvocation(Task(model: model));

        spec.Args.ShouldBe(new[] { "exec", "--json", "--sandbox", "workspace-write", "Fix the failing billing tests" },
            customMessage: "a blank model must omit --model entirely (not emit `--model \"\"`, which Codex rejects) so the CLI uses its default");
    }

    [Fact]
    public void Tools_are_not_projected_codex_has_no_global_allow_list()
    {
        // Codex restricts via --sandbox + per-MCP enabled_tools, not a global allow-list, so task.Tools must
        // NOT leak a fabricated flag — the args are identical to a no-tools run (Codex bounds via the sandbox).
        var withTools = Harness.BuildInvocation(Task() with { Tools = new[] { "Read", "Grep" } });

        withTools.Args.ShouldNotContain("--allowed-tools");
        withTools.Args.ShouldBe(new[] { "exec", "--json", "--model", "gpt-5.3-codex", "--sandbox", "workspace-write", "Fix the failing billing tests" },
            customMessage: "a tools list must not change the Codex invocation — it has no faithful projection there");
    }

    [Fact]
    public void Does_not_yet_inject_the_operating_contract_codex_has_no_native_system_prompt_flag()
    {
        // B1 asymmetry pin: Claude injects AgentOperatingContract via --append-system-prompt; Codex exec has no native
        // system-prompt flag, and prepending to the prompt would conflate it with the goal, so the Codex projection is a
        // deferred follow-up. This pins the CURRENT state so wiring it later is a conscious change, not a silent surprise.
        Harness.BuildInvocation(Task()).Args[^1].ShouldBe("Fix the failing billing tests", "the prompt is the bare goal — no operating contract is prepended (deferred)");
    }

    [Fact]
    public void Read_only_scope_maps_to_the_read_only_sandbox()
    {
        var spec = Harness.BuildInvocation(Task(scope: AgentWriteScope.ReadOnly));

        spec.Args.ShouldContain("read-only");
        spec.Args.ShouldNotContain("workspace-write");
    }

    [Fact]
    public void A_gateway_base_url_is_injected_as_a_model_provider_via_config_overrides_before_the_prompt()
    {
        // Codex 0.142.x ignores OPENAI_BASE_URL as an env var (it routes via a config model-provider), so the projected
        // base URL must be re-injected as `-c model_provider` overrides — over the `responses` wire, key from OPENAI_API_KEY.
        var task = Task() with { Environment = new Dictionary<string, string> { [CodexHarness.BaseUrlEnvVar] = "http://gw.local:40000/v1" } };

        var spec = Harness.BuildInvocation(task);

        spec.Args.ShouldContain("-c");
        spec.Args.ShouldContain("model_provider=codespace");
        spec.Args.ShouldContain("model_providers.codespace.base_url=http://gw.local:40000/v1");
        spec.Args.ShouldContain("model_providers.codespace.wire_api=responses");
        spec.Args.ShouldContain("model_providers.codespace.env_key=OPENAI_API_KEY");

        // The overrides are flags: they must land AFTER --sandbox and BEFORE the goal positional (which stays last).
        var args = new List<string>(spec.Args);
        args.IndexOf("--sandbox").ShouldBeLessThan(args.IndexOf("model_provider=codespace"));
        args.IndexOf("model_provider=codespace").ShouldBeLessThan(args.Count - 1);
        spec.Args[^1].ShouldBe("Fix the failing billing tests");
    }

    [Fact]
    public void No_provider_override_without_a_gateway_base_url_codex_keeps_its_default_openai_provider()
    {
        // Plain OpenAI (no base-URL override) must NOT inject a model-provider — Codex's built-in provider hits api.openai.com.
        Harness.BuildInvocation(Task()).Args.ShouldNotContain("model_provider=codespace");
    }

    [Fact]
    public void Model_provider_config_constants_are_pinned()
    {
        // Rule 8: these are the wire the agent authenticates over. wire_api MUST be `responses` (0.142.x dropped `chat`).
        CodexHarness.ModelProviderWireApi.ShouldBe("responses");
        CodexHarness.ModelProviderId.ShouldBe("codespace");
    }

    [Theory]
    [InlineData("http://gw:40000", "http://gw:40000/v1")]            // root → append /v1 (Codex appends /responses)
    [InlineData("http://gw:40000/", "http://gw:40000/v1")]           // trailing slash trimmed first
    [InlineData("http://gw:40000/v1", "http://gw:40000/v1")]         // already /v1 → unchanged (no /v1/v1)
    [InlineData("http://gw:40000/v1/", "http://gw:40000/v1")]        // /v1 + slash → unchanged
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1")] // /api/v1 ends in /v1 → unchanged
    public void EnsureOpenAiVersionPath_is_idempotent(string input, string expected) =>
        CodexHarness.EnsureOpenAiVersionPath(input).ShouldBe(expected);

    [Fact]
    public void A_root_gateway_base_url_is_normalized_to_v1_in_the_override()
    {
        // The operator's root base URL must reach Codex as host/v1 so it hits /v1/responses, not /responses → 404.
        var task = Task() with { Environment = new Dictionary<string, string> { [CodexHarness.BaseUrlEnvVar] = "http://gw:40000" } };

        Harness.BuildInvocation(task).Args.ShouldContain("model_providers.codespace.base_url=http://gw:40000/v1");
    }

    [Fact]
    public void Seals_telemetry_on_an_allowlist_egress_run()
    {
        // A deny-by-default run pins model+git only; silence Codex's Statsig OTEL exporter + analytics so nothing leaks off-allowlist.
        var args = Harness.BuildInvocation(Task() with { Permissions = new AgentPermissions { Egress = AgentEgressPolicy.Allowlist } }).Args;

        args.ShouldContain("otel.metrics_exporter=none");
        args.ShouldContain("analytics.enabled=false");
    }

    [Fact]
    public void Leaves_telemetry_untouched_on_a_full_egress_run() =>
        Harness.BuildInvocation(Task()).Args.ShouldNotContain("otel.metrics_exporter=none");

    [Fact]
    public void Gateway_override_and_telemetry_seal_coexist_on_an_allowlist_run()
    {
        // A sealed gateway run gets BOTH the model-provider routing AND the telemetry seal — neither drops the other.
        var task = Task() with
        {
            Permissions = new AgentPermissions { Egress = AgentEgressPolicy.Allowlist },
            Environment = new Dictionary<string, string> { [CodexHarness.BaseUrlEnvVar] = "http://gw:40000" },
        };

        var args = Harness.BuildInvocation(task).Args;

        args.ShouldContain("model_providers.codespace.base_url=http://gw:40000/v1");
        args.ShouldContain("otel.metrics_exporter=none");
    }

    [Theory]
    [InlineData("{\"type\":\"agent_message\",\"message\":\"hi\"}", AgentEventKind.AssistantMessage)]
    [InlineData("{\"type\":\"agent_reasoning\"}", AgentEventKind.Reasoning)]
    [InlineData("{\"type\":\"plan_update\"}", AgentEventKind.PlanUpdate)]
    [InlineData("{\"type\":\"exec_command\",\"command\":\"npm test\"}", AgentEventKind.CommandExecuted)]
    [InlineData("{\"type\":\"apply_patch\",\"path\":\"src/a.ts\"}", AgentEventKind.FileChanged)]
    [InlineData("{\"type\":\"mcp_tool_call\"}", AgentEventKind.ToolCall)]
    [InlineData("{\"type\":\"error\",\"message\":\"boom\"}", AgentEventKind.Error)]
    [InlineData("{\"type\":\"task_complete\"}", AgentEventKind.Completed)]
    [InlineData("{\"type\":\"some_future_event\"}", AgentEventKind.Warning)]
    public void Parses_native_types_to_normalized_kinds(string line, AgentEventKind expected)
    {
        Harness.ParseEvents(line).Single().Kind.ShouldBe(expected);
    }

    [Fact]
    public void Parses_the_nested_msg_envelope()
    {
        Harness.ParseEvents("{\"msg\":{\"type\":\"agent_message\",\"message\":\"done\"}}").Single().Kind.ShouldBe(AgentEventKind.AssistantMessage);
    }

    [Fact]
    public void Extracts_a_human_readable_line_into_text()
    {
        Harness.ParseEvents("{\"type\":\"exec_command\",\"command\":\"npm test\"}").Single().Text.ShouldBe("npm test");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Codex v0.2.0 starting…")]
    [InlineData("not json {")]
    public void Returns_no_events_for_blank_or_non_json_lines(string line)
    {
        Harness.ParseEvents(line).ShouldBeEmpty();
    }

    [Fact]
    public void Builds_a_succeeded_result_with_summary_and_changed_files()
    {
        var events = new[]
        {
            new AgentEvent { Kind = AgentEventKind.FileChanged, Text = "src/a.ts" },
            new AgentEvent { Kind = AgentEventKind.FileChanged, Text = "src/b.ts" },
            new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "Fixed the tests." },
        };

        var result = Harness.BuildResult(events, exitCode: 0);

        result.Status.ShouldBe(AgentRunStatus.Succeeded);
        result.ExitReason.ShouldBe("completed");
        result.Summary.ShouldBe("Fixed the tests.");
        result.ChangedFiles.ShouldBe(new[] { "src/a.ts", "src/b.ts" });
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
    public void Build_result_falls_back_to_an_exit_code_error_when_a_failed_run_has_no_diagnostic()
    {
        Harness.BuildResult(System.Array.Empty<AgentEvent>(), exitCode: 137).Error.ShouldContain("137");
    }

    [Fact]
    public void Builds_a_failed_result_with_the_error_on_nonzero_exit()
    {
        var events = new[] { new AgentEvent { Kind = AgentEventKind.Error, Text = "patch failed to apply" } };

        var result = Harness.BuildResult(events, exitCode: 1);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.ExitReason.ShouldBe("non-zero-exit");
        result.Error.ShouldBe("patch failed to apply");
    }

    [Fact]
    public void Build_result_populates_token_usage_from_a_token_count_event()
    {
        // D3b-i: a real codex token_count event carries the cumulative usage under info.total_token_usage.
        // Parsed via the harness (so Data is the real root), it must surface as AgentRunResult.TokenUsage.
        var tokenCount = Harness.ParseEvents("{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1850,\"output_tokens\":420}}}").Single();
        var events = new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "done" }, tokenCount };

        var result = Harness.BuildResult(events, exitCode: 0);

        result.TokenUsage.ShouldNotBeNull("the run's token usage is captured for cost accounting");
        result.TokenUsage!.InputTokens.ShouldBe(1850);
        result.TokenUsage.OutputTokens.ShouldBe(420);
    }

    [Fact]
    public void Build_result_leaves_token_usage_null_when_the_stream_reported_none()
    {
        var events = new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "done" } };

        Harness.BuildResult(events, exitCode: 0).TokenUsage.ShouldBeNull("no usage event → no figure, never a fabricated zero");
    }

    [Fact]
    public void Build_result_captures_the_thread_id_from_the_thread_started_line()
    {
        // P3.1a: Codex's thread.started event carries the run's thread_id; parsed via the harness (so Data is the
        // real root) it must surface on AgentRunResult.SessionId so a later rerun can `codex exec resume <id>`.
        var threadStarted = Harness.ParseEvents("{\"type\":\"thread.started\",\"thread_id\":\"thr-codex-9c21\"}").Single();
        var events = new[] { threadStarted, new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "done" } };

        Harness.BuildResult(events, exitCode: 0).SessionId.ShouldBe("thr-codex-9c21", "the captured thread id is the handle a CONTINUE resumes");
    }

    [Fact]
    public void Build_result_leaves_session_id_null_when_the_stream_carried_no_thread_id()
    {
        var events = new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "done" } };

        Harness.BuildResult(events, exitCode: 0).SessionId.ShouldBeNull("no thread.started → null");
    }

    [Fact]
    public void Build_result_captures_the_thread_id_even_on_a_failed_run()
    {
        // A FAILED run still has a resumable thread — the failure return must carry SessionId too, so a rerun can
        // `codex exec resume <id>` to continue from where it broke.
        var threadStarted = Harness.ParseEvents("{\"type\":\"thread.started\",\"thread_id\":\"thr-codex-failed\"}").Single();
        var events = new[] { threadStarted, new AgentEvent { Kind = AgentEventKind.Error, Text = "patch failed to apply" } };

        Harness.BuildResult(events, exitCode: 1).SessionId.ShouldBe("thr-codex-failed", "a failed run's thread id is captured so a rerun can continue the broken conversation");
    }

    [Fact]
    public void Version_uses_the_default_then_the_env_override()
    {
        var original = System.Environment.GetEnvironmentVariable(CodexHarness.VersionEnvVar);
        try
        {
            System.Environment.SetEnvironmentVariable(CodexHarness.VersionEnvVar, null);
            new CodexHarness().Version.ShouldBe(CodexHarness.DefaultVersion);   // tracks the Dockerfile pin (HarnessVersionPinTests)

            System.Environment.SetEnvironmentVariable(CodexHarness.VersionEnvVar, "9.9.9");
            new CodexHarness().Version.ShouldBe("9.9.9");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(CodexHarness.VersionEnvVar, original);
        }
    }

    [Fact]
    public void VersionEnvVar_constant_name_is_pinned()
    {
        // Renaming this breaks every air-gapped operator who pinned a private Codex build via env.
        CodexHarness.VersionEnvVar.ShouldBe("CODESPACE_CODEX_CLI_VERSION");
    }

    [Fact]
    public void CommandEnvVar_constant_name_is_pinned()
    {
        // Renaming this breaks every air-gapped operator who repointed the Codex binary via env.
        CodexHarness.CommandEnvVar.ShouldBe("CODESPACE_CODEX_CLI_PATH");
    }

    [Fact]
    public void Requests_config_home_isolation_so_the_agent_ignores_the_operators_personal_codex_config() =>
        // The runner redirects this var to a per-run isolated dir, so a run never reads the operator's
        // ~/.codex (whose config.toml model-provider base-URL would otherwise override our injected gateway).
        Harness.BuildInvocation(Task()).ConfigHomeEnvVars.ShouldBe(new[] { "CODEX_HOME" });

    [Fact]
    public void ConfigHomeEnvVar_constant_name_is_pinned() =>
        // Codex reads its config home from this exact name; renaming it would silently re-leak the operator's ~/.codex.
        CodexHarness.ConfigHomeEnvVar.ShouldBe("CODEX_HOME");

    [Theory]
    [InlineData(AgentNetworkAccess.On, true)]
    [InlineData(AgentNetworkAccess.Off, false)]
    public void Projects_the_network_permission_onto_AllowNetwork(AgentNetworkAccess network, bool expected) =>
        // The sandbox runner severs egress when AllowNetwork is false — so the (previously dead) Network toggle finally bites.
        Harness.BuildInvocation(Task() with { Permissions = new AgentPermissions { Network = network } }).AllowNetwork.ShouldBe(expected);

    [Fact]
    public void Command_uses_the_default_then_the_env_override()
    {
        var original = System.Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar);
        try
        {
            System.Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, null);
            Harness.BuildInvocation(Task()).Command.ShouldBe("codex", "default binary name when the env var is unset");

            System.Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, "/opt/codex/bin/codex");
            Harness.BuildInvocation(Task()).Command.ShouldBe("/opt/codex/bin/codex", "the override redirects the spawned binary");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, original);
        }
    }
}
