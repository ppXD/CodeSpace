using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
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
    public void The_durable_supervisor_script_redirects_the_agent_stdin_from_dev_null()
    {
        // Without this, a durable agent process INHERITS the worker's stdin. codex exec reads "additional input from
        // stdin" (the prompt itself rides argv), so an inherited never-closing stdin (a supervising pipe) hangs the run
        // forever with zero output. Removing the redirect silently reintroduces that hang — pin it (Rule 8).
        LocalProcessRunner.SupervisorScript.ShouldContain("</dev/null", Case.Sensitive,
            "the agent command must redirect stdin from /dev/null so a stdin-reading harness gets EOF, never the worker's inherited stdin");
    }
}
