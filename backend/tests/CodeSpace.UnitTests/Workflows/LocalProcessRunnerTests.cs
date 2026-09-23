using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// LocalProcessRunner satisfies the full <see cref="ISandboxRunner"/> behavioral contract (inherited
/// from <see cref="SandboxRunnerContractTests"/>) against a REAL OS process, plus its own kind tag.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LocalProcessRunnerTests : SandboxRunnerContractTests
{
    protected override ISandboxRunner Runner { get; } = new LocalProcessRunner();

    [Fact]
    public void Kind_is_local() => Runner.Kind.ShouldBe("local");

    [Fact]
    public void The_durable_supervisor_script_never_lets_the_agent_inherit_the_worker_stdin()
    {
        // Without a redirect, a durable agent process INHERITS the worker's stdin, and a stdin-reading harness on an
        // inherited never-closing stdin (a supervising pipe) hangs the run forever with zero output. The redirect is
        // from a spooled FILE when the spec carries StandardInput — a regular file ends, so the reader still gets EOF —
        // and from /dev/null otherwise. Removing the default silently reintroduces the hang — pin it (Rule 8).
        LocalProcessRunner.SupervisorScript.ShouldContain("in_path=\"${CSP_IN:-/dev/null}\"", Case.Sensitive,
            "a launch with no StandardInput must still redirect from /dev/null, never inherit");
        LocalProcessRunner.SupervisorScript.ShouldContain("<\"$in_path\"", Case.Sensitive,
            "the agent command must redirect stdin from the resolved path");
        LocalProcessRunner.SupervisorScript.ShouldNotContain("</dev/null;", Case.Sensitive,
            "a hardcoded /dev/null on the agent command would silently drop every spooled prompt");
    }

    [Fact]
    public void The_durable_supervisor_script_unsets_the_stdin_path_before_the_agent_runs()
    {
        // The same rule as every other CSP_* path: the child never learns where the host spooled anything.
        var unset = LocalProcessRunner.SupervisorScript.IndexOf("unset CSP_", StringComparison.Ordinal);
        var agent = LocalProcessRunner.SupervisorScript.IndexOf("\"$@\"", StringComparison.Ordinal);

        LocalProcessRunner.SupervisorScript[unset..agent].ShouldContain("CSP_IN", Case.Sensitive);
    }

    [Fact]
    public async Task Streaming_hands_the_child_its_standard_input_byte_for_byte()
    {
        if (OperatingSystem.IsWindows()) return;
        var lines = new List<string>();

        var result = await new LocalProcessRunner().RunStreamingAsync(ContractSpecs.EchoStdin(LargeStandardInput), (line, _) => { lines.Add(line); return Task.CompletedTask; }, CancellationToken.None);

        result.Status.ShouldBe(SandboxStatus.Success);
        (string.Join('\n', lines) + "\n").ShouldBe(LargeStandardInput);
    }

    [Fact]
    public async Task Bounded_capture_hands_the_child_its_standard_input_byte_for_byte()
    {
        if (OperatingSystem.IsWindows()) return;

        var result = await new LocalProcessRunner().RunAsync(ContractSpecs.EchoStdin(LargeStandardInput) with { CaptureBudget = new SandboxCaptureBudget() }, CancellationToken.None);

        result.Status.ShouldBe(SandboxStatus.Success);
        result.Stdout.ShouldBe(LargeStandardInput);
    }
}
