using System.Text.RegularExpressions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>
/// Drift detector for <see cref="ProcessLiveness"/> (Rule 12.5). The helper is an inline MIRROR of
/// <c>NativeProcess.IsAlive</c>'s Linux branch, which lives in <c>CodeSpace.RunnerHost</c> — an assembly this test
/// project does not reference and whose type is <c>internal</c> — so the mirror cannot be replaced by a call and can
/// only be kept honest by reading the production source.
///
/// <para>What would go wrong without it: a later slice adds a state to the product's dead set (say <c>t</c>, traced-stop
/// after a debugger attach) or drops one, every kill assertion keeps passing against the OLD set, and the tests quietly
/// stop measuring the thing they exist to measure. This fails instead, naming the exact literal to copy.</para>
/// </summary>
public sealed class ProcessLivenessDriftTests
{
    private const string SourcePath = "backend/src/CodeSpace.RunnerHost/Protocol/NativeProcess.cs";

    [Fact]
    public void The_mirrored_dead_state_set_is_still_the_products_own()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), SourcePath));

        var match = Regex.Match(source, @"DeadStates\s*=\s*\[([^\]]*)\]");

        match.Success.ShouldBeTrue($"NativeProcess no longer declares a DeadStates set, so {nameof(ProcessLiveness)} cannot be checked against it — re-derive the mirror by hand from {SourcePath} and update this detector's pattern");

        var productStates = Regex.Matches(match.Groups[1].Value, "\"([^\"]*)\"").Select(state => state.Groups[1].Value).ToArray();

        productStates.ShouldBe(ProcessLiveness.DeadLinuxStates, ignoreOrder: true,
            $"the product's dead-process states are now [{string.Join(", ", productStates)}] but {nameof(ProcessLiveness)}.{nameof(ProcessLiveness.DeadLinuxStates)} still mirrors [{string.Join(", ", ProcessLiveness.DeadLinuxStates)}] — every test that waits for a kill would be measuring the old set; copy the new literal into the mirror");
    }

    [Theory]
    // A comm with spaces and a bracket is why BOTH the product and this mirror read past the LAST ')' rather than
    // splitting on spaces: a naive split shifts the state field and every verdict with it.
    [InlineData("4242 (sleep) S 1 4242 4242 0 -1 4194304", "S")]
    [InlineData("4242 (sleep) Z 1 4242 4242 0 -1 4194304", "Z")]
    [InlineData("4242 (my prog (x)) X 1 4242 4242 0 -1 4194304", "X")]
    [InlineData("4242 (bash -c) R 1 4242 4242 0 -1 4194304", "R")]
    [InlineData("4242 () D 1 4242 4242 0 -1 4194304", "D")]
    public void The_state_field_is_read_past_the_comm(string stat, string expected)
    {
        ProcessLiveness.LinuxState(stat).ShouldBe(expected, "a mis-parsed state field would read a pid number as a process state and call every process alive");
    }

    [Theory]
    // ONLY a corpse is dead. A stopped, traced or uninterruptible-sleep process is very much alive, and folding any of
    // them into "gone" would make a kill assertion pass over a process that is still holding its workspace.
    [InlineData("R", true)]
    [InlineData("S", true)]
    [InlineData("D", true)]
    [InlineData("T", true)]
    [InlineData("t", true)]
    [InlineData("I", true)]
    [InlineData("Z", false)]
    [InlineData("X", false)]
    public void Only_an_exited_process_counts_as_dead(string state, bool expectedAlive)
    {
        ProcessLiveness.DeadLinuxStates.Contains(state).ShouldBe(!expectedAlive, $"/proc state '{state}' is {(expectedAlive ? "a live process" : "a corpse")}");
    }

    [Theory]
    // macOS answers GetProcessById(-1) without throwing, so an unguarded mirror called a pid that cannot exist ALIVE
    // — a kill assertion would then wait out its whole bound on a number the OS never issued.
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void A_pid_the_product_refuses_to_answer_for_is_not_alive(int pid)
    {
        ProcessLiveness.IsAliveLikeTheProduct(pid).ShouldBeFalse("the mirror must decline the same pids the product declines, or it is answering a question the product never asks");
    }

    [Fact]
    public void The_mirrored_lowest_answerable_pid_is_still_the_products_own()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), SourcePath));

        var match = Regex.Match(source, @"identity\.ProcessId <= (\d+)");

        match.Success.ShouldBeTrue($"NativeProcess.IsAlive no longer floors the pid it will answer for, so {nameof(ProcessLiveness)}.{nameof(ProcessLiveness.LowestAnswerablePid)} mirrors nothing — re-derive it from {SourcePath}");
        (int.Parse(match.Groups[1].Value) + 1).ShouldBe(ProcessLiveness.LowestAnswerablePid, "the product's pid floor moved and the mirror did not");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "backend", "src", "CodeSpace.Core"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root from test output.");
    }
}
