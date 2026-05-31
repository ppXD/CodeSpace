using CodeSpace.Core.Services.Workflows.Engine;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The engine reads its intra-run max-parallelism from an env var (Rule 8 escape hatch) so an operator
/// can tune throughput or pin to 1 to force fully-sequential execution. These pin the env-var NAME (a
/// rename would silently break any operator who set it) and that the value is parsed + clamped safely:
/// unset / garbage falls back to the default, out-of-range clamps into [1, ceiling].
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowEngineParallelismTests
{
    [Fact]
    public void EnvVar_name_is_pinned()
    {
        // Renaming this constant orphans every operator who pinned a value via the old name. Hard-pin.
        WorkflowEngine.MaxParallelismEnvVar.ShouldBe("CODESPACE_WORKFLOW_MAX_PARALLELISM");
    }

    [Theory]
    [InlineData(null)]      // unset → default
    [InlineData("")]        // empty → default
    [InlineData("   ")]     // whitespace → default
    [InlineData("abc")]     // non-numeric → default
    [InlineData("4.5")]     // non-integer → default
    public void Unparseable_falls_back_to_the_default(string? raw)
    {
        WorkflowEngine.ParseMaxParallelism(raw).ShouldBe(WorkflowEngine.DefaultMaxParallelism);
    }

    [Theory]
    [InlineData("0", 1)]                                                              // floor: 0 → 1
    [InlineData("-5", 1)]                                                             // negative → 1
    [InlineData("1", 1)]
    [InlineData("8", 8)]
    [InlineData("16", 16)]
    public void Parses_and_clamps_into_range(string raw, int expected)
    {
        WorkflowEngine.ParseMaxParallelism(raw).ShouldBe(expected);
    }

    [Theory]
    [InlineData("64")]      // at ceiling
    [InlineData("65")]      // above ceiling → clamped down
    [InlineData("100000")]
    public void Clamps_above_the_ceiling(string raw)
    {
        WorkflowEngine.ParseMaxParallelism(raw).ShouldBe(WorkflowEngine.MaxParallelismCeiling);
    }
}
