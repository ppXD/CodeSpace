using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// High-fidelity tests for the streaming surface of the v0 runner: a REAL OS process emits stdout over
/// time and the per-line callback must observe each line in order, with the same outcome semantics as
/// the batch path (exit code, stderr capture, timeout, caller cancellation). Cross-platform via
/// /bin/sh or cmd.exe.
/// </summary>
[Trait("Category", "Unit")]
public class LocalProcessStreamRunnerTests
{
    private static readonly LocalProcessRunner Runner = new();

    private static SandboxSpec Sh(string script, int timeoutSeconds = 30) => new()
    {
        Command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
        Args = OperatingSystem.IsWindows() ? new[] { "/c", script } : new[] { "-c", script },
        TimeoutSeconds = timeoutSeconds,
    };

    private static string MultiLine(params string[] xs) =>
        OperatingSystem.IsWindows() ? string.Join("& ", xs.Select(x => $"echo {x}")) : "printf '" + string.Join("\\n", xs) + "\\n'";

    private static string Exit(int code) => OperatingSystem.IsWindows() ? $"& exit {code}" : $"; exit {code}";

    private static string Sleep(int seconds) => OperatingSystem.IsWindows() ? $"ping -n {seconds + 1} 127.0.0.1 >nul" : $"sleep {seconds}";

    private static async Task<(SandboxResult Result, List<string> Lines)> RunCollectingAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        var lines = new List<string>();
        var result = await Runner.RunStreamingAsync(spec, (line, _) => { lines.Add(line.Trim()); return Task.CompletedTask; }, ct);
        return (result, lines);
    }

    [Fact]
    public async Task Streams_stdout_lines_in_order()
    {
        var (result, lines) = await RunCollectingAsync(Sh(MultiLine("alpha", "beta", "gamma")));

        result.Status.ShouldBe(SandboxStatus.Success);
        result.ExitCode.ShouldBe(0);
        lines.ShouldBe(new[] { "alpha", "beta", "gamma" });
        result.Stdout.ShouldBe("");   // delivered via the callback, not accumulated
    }

    [Fact]
    public async Task Nonzero_exit_reports_failed_after_streaming_what_it_emitted()
    {
        var (result, lines) = await RunCollectingAsync(Sh(MultiLine("partial") + Exit(2)));

        result.Status.ShouldBe(SandboxStatus.Failed);
        result.ExitCode.ShouldBe(2);
        lines.ShouldContain("partial");
    }

    [Fact]
    public async Task Captures_stderr_on_the_result_with_no_stdout_lines()
    {
        var (result, lines) = await RunCollectingAsync(Sh("echo oops 1>&2"));

        result.Stderr.ShouldContain("oops");
        lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Times_out_a_long_running_command()
    {
        var (result, _) = await RunCollectingAsync(Sh(Sleep(10), timeoutSeconds: 1));

        result.Status.ShouldBe(SandboxStatus.TimedOut);
        result.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public async Task Caller_cancellation_throws_rather_than_returning_a_result()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => RunCollectingAsync(Sh(Sleep(10), timeoutSeconds: 30), cts.Token));
    }
}
