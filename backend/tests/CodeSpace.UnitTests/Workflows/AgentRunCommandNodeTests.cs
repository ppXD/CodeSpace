using System.Text.Json;
using CodeSpace.Core.Services.Agents.Commands;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <c>agent.run_command</c> — drives the real node against a stub <see cref="IRunCommandService"/> that
/// records the <see cref="RunCommandRequest"/> and returns a canned <see cref="SandboxResult"/> (or throws),
/// pinning: input parsing (required command, optional repositoryId/branch/network/timeout/runnerKind, args
/// pass-through), the secure-default network=off, the output shape, the key contract that a non-zero exit is
/// a SUCCESSFUL node carrying status+exitCode (so workflows branch on it), and workspace-failure → node fail.
/// </summary>
[Trait("Category", "Unit")]
public class AgentRunCommandNodeTests
{
    private const string Repo = "11111111-1111-1111-1111-111111111111";

    private sealed class StubRunCommandService : IRunCommandService
    {
        public RunCommandRequest? Request;
        public int Calls;
        public Exception? Throw;
        public SandboxResult Result = new() { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "ok", Stderr = "" };

        public Task<SandboxResult> RunAsync(RunCommandRequest request, CancellationToken cancellationToken)
        {
            Request = request; Calls++;
            if (Throw != null) throw Throw;
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task Runs_and_outputs_exit_status_stdout_stderr()
    {
        var stub = new StubRunCommandService { Result = new() { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "all tests passed", Stderr = "" } };

        var result = await new AgentRunCommandNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.Calls.ShouldBe(1);
        stub.Request!.Command.ShouldBe("npm");
        stub.Request.Args.ShouldBe(new[] { "test" });
        stub.Request.RepositoryId.ShouldBe(Guid.Parse(Repo));
        stub.Request.AllowNetwork.ShouldBeFalse("network is off by default — the secure posture");

        result.Outputs["exitCode"].GetInt32().ShouldBe(0);
        result.Outputs["status"].GetString().ShouldBe("Success");
        result.Outputs["stdout"].GetString().ShouldBe("all tests passed");
        result.Outputs["stderr"].GetString().ShouldBe("");
    }

    [Fact]
    public async Task A_non_zero_exit_is_a_successful_node_carrying_the_status_and_code()
    {
        // Load-bearing contract: a failing command (tests red) is NOT a node failure — the node succeeds
        // with status=Failed + the exit code so the workflow can branch (tests-passed? → open PR).
        var stub = new StubRunCommandService { Result = new() { Status = SandboxStatus.Failed, ExitCode = 1, Stdout = "1 test failed", Stderr = "boom" } };

        var result = await new AgentRunCommandNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, "a non-zero command exit must NOT fail the node — the workflow branches on the status output");
        result.Outputs["status"].GetString().ShouldBe("Failed");
        result.Outputs["exitCode"].GetInt32().ShouldBe(1);
        result.Outputs["stderr"].GetString().ShouldBe("boom");
    }

    [Fact]
    public async Task A_timeout_surfaces_as_status_TimedOut()
    {
        var stub = new StubRunCommandService { Result = new() { Status = SandboxStatus.TimedOut, ExitCode = -1, Stdout = "", Stderr = "" } };

        var result = await new AgentRunCommandNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["status"].GetString().ShouldBe("TimedOut");
        result.Outputs["exitCode"].GetInt32().ShouldBe(-1);
    }

    [Fact]
    public async Task Runs_ephemerally_when_no_repository_is_given()
    {
        var stub = new StubRunCommandService();

        await new AgentRunCommandNode(stub).RunAsync(ContextFrom(new()
        {
            ["command"] = JsonSerializer.SerializeToElement("echo"),
        }), CancellationToken.None);

        stub.Request!.RepositoryId.ShouldBeNull("absent repositoryId → an ephemeral run with no checkout");
    }

    [Fact]
    public async Task Passes_branch_network_timeout_runnerKind_through()
    {
        var stub = new StubRunCommandService();

        await new AgentRunCommandNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
            ["command"] = JsonSerializer.SerializeToElement("make"),
            ["args"] = JsonSerializer.SerializeToElement(new[] { "build", "--jobs", "4" }),
            ["branch"] = JsonSerializer.SerializeToElement("release/x"),
            ["network"] = JsonSerializer.SerializeToElement(true),
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(120),
            ["runnerKind"] = JsonSerializer.SerializeToElement("local"),
        }), CancellationToken.None);

        stub.Request!.Args.ShouldBe(new[] { "build", "--jobs", "4" });
        stub.Request.Ref.ShouldBe("release/x");
        stub.Request.AllowNetwork.ShouldBeTrue("an explicit network:true opts into egress");
        stub.Request.TimeoutSeconds.ShouldBe(120);
        stub.Request.RunnerKind.ShouldBe("local");
    }

    [Fact]
    public async Task Fails_when_command_is_missing()
    {
        var stub = new StubRunCommandService();

        var result = await new AgentRunCommandNode(stub).RunAsync(ContextFrom(new()
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("command");
        stub.Calls.ShouldBe(0, "a missing required field must short-circuit before the run");
    }

    [Fact]
    public async Task A_workspace_failure_fails_the_node_with_an_actionable_message()
    {
        var stub = new StubRunCommandService { Throw = new WorkspaceException("clone failed: ref not found") };

        var result = await new AgentRunCommandNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("workspace");
        result.Error.ShouldContain("clone failed");
    }

    [Fact]
    public async Task Caps_stdout_and_stderr_when_maxOutputChars_is_set_but_always_reports_the_full_size()
    {
        var bigOut = new string('o', 5000);
        var bigErr = new string('e', 4000);
        var stub = new StubRunCommandService { Result = new() { Status = SandboxStatus.Success, ExitCode = 0, Stdout = bigOut, Stderr = bigErr } };

        var result = await new AgentRunCommandNode(stub).RunAsync(ContextFrom(new()
        {
            ["command"] = JsonSerializer.SerializeToElement("npm"),
            ["maxOutputChars"] = JsonSerializer.SerializeToElement(200),
        }), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["stdout"].GetString()!.Length.ShouldBeLessThan(5000, "stdout is capped to a preview");
        result.Outputs["stderr"].GetString()!.Length.ShouldBeLessThan(4000, "stderr is capped to a preview");
        result.Outputs["stdoutBytes"].GetInt32().ShouldBe(5000, "the full original size is reported even when capped");
        result.Outputs["stderrBytes"].GetInt32().ShouldBe(4000);
    }

    [Fact]
    public async Task Without_maxOutputChars_the_full_output_is_kept_and_sizes_are_still_reported()
    {
        var stub = new StubRunCommandService { Result = new() { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "all 42 tests passed", Stderr = "" } };

        var result = await new AgentRunCommandNode(stub).RunAsync(Context(), CancellationToken.None);

        result.Outputs["stdout"].GetString().ShouldBe("all 42 tests passed", "no cap → verbatim (non-breaking default)");
        result.Outputs["stdoutBytes"].GetInt32().ShouldBe("all 42 tests passed".Length);
        result.Outputs["stderrBytes"].GetInt32().ShouldBe(0);
    }

    private static NodeRunContext Context() => ContextFrom(new()
    {
        ["repositoryId"] = JsonSerializer.SerializeToElement(Repo),
        ["command"] = JsonSerializer.SerializeToElement("npm"),
        ["args"] = JsonSerializer.SerializeToElement(new[] { "test" }),
    });

    private static NodeRunContext ContextFrom(Dictionary<string, JsonElement> inputs) => new()
    {
        Inputs = inputs,
        Config = new Dictionary<string, JsonElement>(),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
    };
}
