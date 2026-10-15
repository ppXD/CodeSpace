using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the ONE rule for which acceptance argvs can be run (<see cref="AcceptanceArgv"/>). The launch projections,
/// the rehydrate and the stop grade, the in-loop hook and the local verifier all ask it, so what it refuses and what it
/// keeps is pinned here once, by the decision it makes rather than through any one caller — and
/// <c>AcceptanceArgvInventoryTests</c> pins that those callers really do ask it instead of keeping a copy.
/// </summary>
[Trait("Category", "Unit")]
public class AcceptanceArgvTests
{
    [Fact]
    public void No_argv_is_no_command()
    {
        AcceptanceArgv.NormalizeOrNull(null).ShouldBeNull("an absent floor is the ordinary shape, not a fault");
        AcceptanceArgv.NormalizeOrNull(Array.Empty<string>()).ShouldBeNull("an empty argv names no program");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public void A_blank_program_refuses_the_whole_argv_and_never_promotes_a_later_argument_into_it(string program)
    {
        AcceptanceArgv.NormalizeOrNull([program, "sh", "check.sh"]).ShouldBeNull("the argv is refused whole — promoting 'sh' into the program slot would run a command nobody wrote");
        AcceptanceArgv.NormalizeOrNull([program]).ShouldBeNull("a lone blank element is not a program either");
    }

    [Theory]
    [InlineData(new[] { "sh\0", "check.sh" }, "in the program")]
    [InlineData(new[] { "sh", "check\0.sh" }, "inside a later argument")]
    [InlineData(new[] { "sh", "\0" }, "as a later argument on its own")]
    public void A_nul_anywhere_refuses_the_whole_argv(string[] argv, string where)
    {
        AcceptanceArgv.NormalizeOrNull(argv).ShouldBeNull($"a NUL {where} cannot be handed to exec, so nothing of this argv may run");
    }

    [Fact]
    public void A_null_element_refuses_the_whole_argv()
    {
        AcceptanceArgv.NormalizeOrNull(new[] { "sh", null!, "check.sh" }).ShouldBeNull("a null element is a hole in the argument list, not an empty argument");
        AcceptanceArgv.NormalizeOrNull(new[] { null!, "sh" }).ShouldBeNull("a null program is no program");
    }

    [Theory]
    [InlineData(new[] { "sh", " ", "check.sh" }, "a whitespace argument between two others")]
    [InlineData(new[] { "sh", "" }, "an empty trailing argument")]
    [InlineData(new[] { "custom-check", "", "  ", "quoted argument" }, "empty, blank and space-bearing arguments together")]
    [InlineData(new[] { "dotnet", "test", "--filter", "Category=Unit|Category=Smoke" }, "a pipe and an equals sign are characters, not syntax")]
    public void Every_element_after_the_program_is_kept_verbatim_blank_ones_included(string[] argv, string what)
    {
        AcceptanceArgv.NormalizeOrNull(argv).ShouldBe(argv, $"{what} — the element boundaries ARE the command, and TaskLaunchFlowTests pins [\"sh\", \" \", \"check.sh\"] surviving launch");
    }

    [Fact]
    public void The_result_is_its_own_copy_so_a_caller_holding_the_config_cannot_move_a_graded_argv()
    {
        var argv = new[] { "sh", "check.sh" };

        var normalized = AcceptanceArgv.NormalizeOrNull(argv)!;

        normalized.ShouldNotBeSameAs(argv);

        argv[1] = "other.sh";

        normalized.ShouldBe(["sh", "check.sh"], "the argv the grader runs is the one that was normalised, whatever happens to the list it came from");
    }
}
