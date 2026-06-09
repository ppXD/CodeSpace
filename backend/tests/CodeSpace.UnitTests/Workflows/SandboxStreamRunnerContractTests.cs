using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The behavioral contract every <see cref="ISandboxStreamRunner"/> must satisfy: stdout lines arrive in
/// order as they're emitted, with the same terminal semantics as the batch path (exit code, stderr,
/// timeout, caller-cancel). A streaming runner gets full coverage by subclassing this. Real execution
/// environment via <see cref="ContractSpecs"/>.
/// </summary>
public abstract class SandboxStreamRunnerContractTests
{
    protected abstract ISandboxStreamRunner StreamRunner { get; }

    private async Task<(SandboxResult Result, List<string> Lines)> RunCollectingAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        var lines = new List<string>();
        var result = await StreamRunner.RunStreamingAsync(spec, (line, _) => { lines.Add(line.Trim()); return Task.CompletedTask; }, ct);
        return (result, lines);
    }

    [Fact]
    public async Task Streams_stdout_lines_in_order()
    {
        var (result, lines) = await RunCollectingAsync(ContractSpecs.MultiLine("alpha", "beta", "gamma"));

        result.Status.ShouldBe(SandboxStatus.Success);
        lines.ShouldBe(new[] { "alpha", "beta", "gamma" });
        result.Stdout.ShouldBe("");   // delivered via the callback, not accumulated
    }

    [Fact]
    public async Task Nonzero_exit_reports_failed_after_streaming_what_it_emitted()
    {
        var (result, lines) = await RunCollectingAsync(ContractSpecs.PrintThenExit("partial", 2));

        result.Status.ShouldBe(SandboxStatus.Failed);
        result.ExitCode.ShouldBe(2);
        lines.ShouldContain("partial");
    }

    [Fact]
    public async Task Stderr_is_captured_with_no_stdout_lines()
    {
        var (result, lines) = await RunCollectingAsync(ContractSpecs.PrintToStderr("err-stream"));

        result.Stderr.ShouldContain("err-stream");
        lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Times_out_with_exit_minus_one()
    {
        var (result, _) = await RunCollectingAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 1 });

        result.Status.ShouldBe(SandboxStatus.TimedOut);
        result.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public async Task Caller_cancellation_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => RunCollectingAsync(ContractSpecs.Sleep(10) with { TimeoutSeconds = 30 }, cts.Token));
    }
}
