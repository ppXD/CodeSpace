using System.Diagnostics;
using System.Text;
using CodeSpace.Core.Services.Agents.Commands;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class BoundedCommandOutputTests
{
    [Fact]
    public void Run_command_sets_a_server_owned_observation_budget()
    {
        var spec = RunCommandService.BuildSpec(new RunCommandRequest { Command = "echo" }, null);
        spec.CaptureBudget.ShouldNotBeNull().StdoutBytes.ShouldBe(SandboxCaptureBudget.DefaultBytes);
        spec.CaptureBudget.StderrBytes.ShouldBe(SandboxCaptureBudget.DefaultBytes);
        var fromJson = System.Text.Json.JsonSerializer.Deserialize<SandboxSpec>("{\"Command\":\"echo\",\"CaptureBudget\":{\"StdoutBytes\":2147483647}}");
        fromJson.ShouldNotBeNull().CaptureBudget.ShouldBeNull();
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    public async Task Batch_capture_honestly_reports_exact_boundaries_for_both_streams(int bytes)
    {
        var result = await RunAsync($"head -c {bytes} /dev/zero | tr '\\000' x; head -c {bytes} /dev/zero | tr '\\000' y >&2", new SandboxCaptureBudget { StdoutBytes = 4096, StderrBytes = 4096 });
        result.Status.ShouldBe(SandboxStatus.Success, result.Stderr);
        result.Stdout.ShouldBe(new string('x', Math.Min(bytes, 4096)));
        result.Stderr.ShouldBe(new string('y', Math.Min(bytes, 4096)));
        AssertObserved(result.Observation.ShouldNotBeNull().Stdout, bytes, bytes <= 4096);
        AssertObserved(result.Observation.Stderr, bytes, bytes <= 4096);
    }

    [Fact]
    public async Task Large_production_is_drained_while_only_the_bounded_prefix_is_retained()
    {
        const int produced = 64 * 1024 * 1024;
        var result = await RunAsync($"head -c {produced} /dev/zero | tr '\\000' x; printf committed >&2; exit 9", new SandboxCaptureBudget { StdoutBytes = 32768, StderrBytes = 32 });
        result.Status.ShouldBe(SandboxStatus.Failed);
        result.ExitCode.ShouldBe(9);
        result.Stdout.Length.ShouldBe(32768);
        result.Stderr.ShouldBe("committed");
        AssertObserved(result.Observation.ShouldNotBeNull().Stdout, produced, false);
        AssertObserved(result.Observation.Stderr, 9, true);
    }

    [Fact]
    public async Task Raw_invalid_utf8_counts_are_distinct_from_the_decoded_capture_and_never_claim_complete()
    {
        var result = await RunAsync("printf '\\377'; printf '\\303\\251' >&2", new SandboxCaptureBudget());
        result.Status.ShouldBe(SandboxStatus.Success);
        result.Stdout.ShouldBe("\uFFFD");
        Encoding.UTF8.GetByteCount(result.Stdout).ShouldBe(3);
        AssertObserved(result.Observation.ShouldNotBeNull().Stdout, 1, false);
        result.Stderr.ShouldBe("é");
        AssertObserved(result.Observation.Stderr, 2, true);
    }

    [Fact]
    public async Task Timeout_preserves_the_observed_prefix_and_execution_outcome()
    {
        var spec = Spec("printf emitted; sleep 5", new SandboxCaptureBudget { StdoutBytes = 3 }) with { TimeoutSeconds = 1 };
        var result = await new LocalProcessRunner().RunAsync(spec, CancellationToken.None);
        result.Status.ShouldBe(SandboxStatus.TimedOut);
        result.ExitCode.ShouldBe(-1);
        result.Stdout.ShouldBe("emi");
        result.Observation.ShouldNotBeNull().Stdout.ShouldNotBeNull().ObservedBytes.ShouldBe(7);
        result.Observation.Stdout.CaptureComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task Streaming_omits_an_oversized_record_instead_of_passing_partial_json_and_continues_delivery()
    {
        var lines = new List<string>();
        var spec = Spec("printf 'before\\n'; head -c 1048576 /dev/zero | tr '\\000' x; printf '\\nafter\\n'; head -c 1000 /dev/zero | tr '\\000' e >&2", new SandboxCaptureBudget { StdoutLineBytes = 32, StderrBytes = 7 });
        var result = await new LocalProcessRunner().RunStreamingAsync(spec, (line, _) => { lines.Add(line); return Task.CompletedTask; }, CancellationToken.None);
        result.Status.ShouldBe(SandboxStatus.Success);
        lines.ShouldBe(new[] { "before", "after" });
        result.Stdout.ShouldBeEmpty();
        var stdout = result.Observation.ShouldNotBeNull().Stdout.ShouldNotBeNull();
        stdout.ObservedBytes.ShouldBe(1048576 + 14);
        stdout.ReachedEndOfStream.ShouldBeTrue();
        stdout.CaptureComplete.ShouldBeFalse();
        stdout.DeliveryComplete.ShouldBe(false);
        result.Stderr.ShouldBe("eeeeeee");
        AssertObserved(result.Observation.Stderr, 1000, false);
    }

    [Fact]
    public async Task Streaming_complete_records_preserve_crlf_empty_and_final_partial_lines()
    {
        var lines = new List<string>();
        var result = await new LocalProcessRunner().RunStreamingAsync(Spec("printf 'first\\r\\n\\nlast'", new SandboxCaptureBudget { StdoutLineBytes = 6 }), (line, _) => { lines.Add(line); return Task.CompletedTask; }, CancellationToken.None);
        result.Status.ShouldBe(SandboxStatus.Success);
        lines.ShouldBe(new[] { "first", "", "last" });
        var observation = result.Observation.ShouldNotBeNull().Stdout.ShouldNotBeNull();
        observation.ObservedBytes.ShouldBe(12);
        observation.ReachedEndOfStream.ShouldBeTrue();
        observation.DeliveryComplete.ShouldBe(true);
    }

    [Fact]
    public async Task A_streaming_callback_fault_remains_the_primary_exception()
    {
        var expected = new InvalidOperationException("callback failed");
        var actual = await Should.ThrowAsync<InvalidOperationException>(() => new LocalProcessRunner().RunStreamingAsync(Spec("printf 'record\\n'; sleep 5", new SandboxCaptureBudget()), (_, _) => Task.FromException(expected), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        actual.ShouldBeSameAs(expected);
    }

    [Fact]
    public async Task A_callback_ignoring_cancellation_cannot_hold_the_observer_past_its_deadline()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        try
        {
            var spec = Spec("printf 'record\\n'; sleep 5", new SandboxCaptureBudget()) with { TimeoutSeconds = 1 };
            var result = await new LocalProcessRunner().RunStreamingAsync(spec, (_, _) => { calls++; return release.Task; }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            result.Status.ShouldBe(SandboxStatus.TimedOut);
            result.Observation.ShouldNotBeNull().Stdout.ShouldNotBeNull().DeliveryComplete.ShouldBe(false);
            calls.ShouldBe(1);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Invalid_streaming_utf8_is_omitted_as_a_whole_record()
    {
        var lines = new List<string>();
        var result = await new LocalProcessRunner().RunStreamingAsync(Spec("printf '\\377\\nok\\n'", new SandboxCaptureBudget()), (line, _) => { lines.Add(line); return Task.CompletedTask; }, CancellationToken.None);
        lines.ShouldBe(new[] { "ok" });
        result.Observation.ShouldNotBeNull().Stdout.ShouldNotBeNull().ObservedBytes.ShouldBe(5);
        result.Observation.Stdout.ReachedEndOfStream.ShouldBeTrue();
        result.Observation.Stdout.DeliveryComplete.ShouldBe(false);
    }

    [UnconfinedFact]
    public async Task A_root_exit_with_an_orphan_holding_pipes_keeps_counts_as_lower_bounds()
    {
        SandboxResult? result = null;
        try
        {
            var spec = Spec("sleep 12 & printf '%s' \"$!\" >&2; printf retained; exit 0", new SandboxCaptureBudget { StdoutBytes = 32 }) with { TimeoutSeconds = 1 };
            result = await new LocalProcessRunner().RunAsync(spec, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(6));
            result.Status.ShouldBe(SandboxStatus.TimedOut);
            result.Stdout.ShouldBe("retained");
            var observation = result.Observation.ShouldNotBeNull().Stdout.ShouldNotBeNull();
            observation.ObservedBytes.ShouldBe(8);
            observation.CaptureComplete.ShouldBeTrue("all bytes observed so far were kept");
            observation.ReachedEndOfStream.ShouldBeFalse("root exit and our bounded drain are not source EOF");
        }
        finally
        {
            if (result is not null && int.TryParse(result.Stderr, out var pid))
            {
                try { using var child = Process.GetProcessById(pid); if (!child.HasExited) child.Kill(); }
                catch (ArgumentException) { }
            }
        }
    }

    [Fact]
    public async Task Invalid_capture_budgets_are_rejected_before_process_launch()
    {
        var spec = new SandboxSpec { Command = "/does-not-exist", CaptureBudget = new SandboxCaptureBudget { StdoutBytes = SandboxCaptureBudget.MaximumBytes + 1 } };
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => new LocalProcessRunner().RunAsync(spec, CancellationToken.None));
    }

    private sealed class UnconfinedFactAttribute : FactAttribute
    {
        public UnconfinedFactAttribute()
        {
            if (BubblewrapSandbox.Available is not null) Skip = "This fault requires an unconfined orphan; PID namespace teardown closes its pipes on a confined host.";
        }
    }

    private static Task<SandboxResult> RunAsync(string script, SandboxCaptureBudget budget) => new LocalProcessRunner().RunAsync(Spec(script, budget), CancellationToken.None);
    private static SandboxSpec Spec(string script, SandboxCaptureBudget budget) => new() { Command = "/bin/sh", Args = new[] { "-c", script }, CaptureBudget = budget, TimeoutSeconds = 30 };
    private static void AssertObserved(SandboxStreamObservation? observation, long bytes, bool complete)
    {
        observation.ShouldNotBeNull().ObservedBytes.ShouldBe(bytes);
        observation.ReachedEndOfStream.ShouldBeTrue();
        observation.CaptureComplete.ShouldBe(complete);
        observation.DeliveryComplete.ShouldBeNull();
    }
}
