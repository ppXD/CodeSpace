using CodeSpace.Core.Services.Workflows.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the Codex adapter's stable contract: the exact CLI invocation it builds, the native-stream →
/// normalized-event mapping (incl. unknown → Warning so nothing is dropped), the result fold, and the
/// air-gapped version-override env var (Rule 8). Exact Codex JSONL field names are calibrated against
/// real output at B0.4; these tests pin the normalization shape, which is the contract.
/// </summary>
[Trait("Category", "Unit")]
public class CodexCliHarnessTests
{
    private static readonly CodexCliHarness Harness = new();

    private static AgentTask Task(string goal = "Fix the failing billing tests", string model = "gpt-5.3-codex", AgentWriteScope scope = AgentWriteScope.Workspace) => new()
    {
        Goal = goal,
        Harness = CodexCliHarness.HarnessKind,
        Model = model,
        WorkspaceDirectory = "/tmp/ws",
        Permissions = new AgentPermissions { WriteScope = scope },
        TimeoutSeconds = 900,
    };

    [Fact]
    public void Kind_is_codex_cli() => Harness.Kind.ShouldBe("codex-cli");

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
    public void Read_only_scope_maps_to_the_read_only_sandbox()
    {
        var spec = Harness.BuildInvocation(Task(scope: AgentWriteScope.ReadOnly));

        spec.Args.ShouldContain("read-only");
        spec.Args.ShouldNotContain("workspace-write");
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
        Harness.ParseEvent(line)!.Kind.ShouldBe(expected);
    }

    [Fact]
    public void Parses_the_nested_msg_envelope()
    {
        Harness.ParseEvent("{\"msg\":{\"type\":\"agent_message\",\"message\":\"done\"}}")!.Kind.ShouldBe(AgentEventKind.AssistantMessage);
    }

    [Fact]
    public void Extracts_a_human_readable_line_into_text()
    {
        Harness.ParseEvent("{\"type\":\"exec_command\",\"command\":\"npm test\"}")!.Text.ShouldBe("npm test");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Codex v0.2.0 starting…")]
    [InlineData("not json {")]
    public void Returns_null_for_blank_or_non_json_lines(string line)
    {
        Harness.ParseEvent(line).ShouldBeNull();
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
    public void Builds_a_failed_result_with_the_error_on_nonzero_exit()
    {
        var events = new[] { new AgentEvent { Kind = AgentEventKind.Error, Text = "patch failed to apply" } };

        var result = Harness.BuildResult(events, exitCode: 1);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.ExitReason.ShouldBe("non-zero-exit");
        result.Error.ShouldBe("patch failed to apply");
    }

    [Fact]
    public void Version_uses_the_default_then_the_env_override()
    {
        var original = System.Environment.GetEnvironmentVariable(CodexCliHarness.VersionEnvVar);
        try
        {
            System.Environment.SetEnvironmentVariable(CodexCliHarness.VersionEnvVar, null);
            new CodexCliHarness().Version.ShouldBe("0.2.0");

            System.Environment.SetEnvironmentVariable(CodexCliHarness.VersionEnvVar, "9.9.9");
            new CodexCliHarness().Version.ShouldBe("9.9.9");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(CodexCliHarness.VersionEnvVar, original);
        }
    }

    [Fact]
    public void VersionEnvVar_constant_name_is_pinned()
    {
        // Renaming this breaks every air-gapped operator who pinned a private Codex build via env.
        CodexCliHarness.VersionEnvVar.ShouldBe("CODESPACE_CODEX_CLI_VERSION");
    }
}
