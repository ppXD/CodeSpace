using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>
/// Optional STREAMING capability a sandbox runner MAY implement alongside <see cref="ISandboxRunner"/>
/// (Rule 7 / ISP — a sibling interface, never a widening of the base contract). It runs a command and
/// invokes <paramref name="onStdoutLine"/> for each stdout line AS IT ARRIVES, so the agent layer can
/// turn a harness's native stream into normalized events live — <c>line → harness.ParseEvent →
/// AgentRunService.AppendEventAsync</c> — instead of waiting for process exit. A caller feature-detects
/// support with <c>runner is ISandboxStreamRunner</c> and falls back to
/// <see cref="ISandboxRunner.RunAsync"/> when a runner doesn't stream.
///
/// The callback is awaited per line, so a slow consumer (a DB write per event) naturally backpressures
/// the read — the OS pipe buffers absorb bursts, nothing is dropped. stderr is captured in full and
/// returned on the result; stdout is delivered ONLY through the callback, so the result's
/// <see cref="SandboxResult.Stdout"/> is empty in streaming mode — keeping memory bounded for a long run
/// (the caller already saw every line).
/// </summary>
public interface ISandboxStreamRunner
{
    /// <summary>
    /// Run <paramref name="spec"/>, invoking <paramref name="onStdoutLine"/> for each stdout line as it
    /// arrives, and return the terminal <see cref="SandboxResult"/>. Same outcome semantics as
    /// <see cref="ISandboxRunner.RunAsync"/>: a non-zero exit is <see cref="SandboxStatus.Failed"/>,
    /// exceeding the spec timeout is <see cref="SandboxStatus.TimedOut"/>, and caller cancellation throws.
    /// </summary>
    Task<SandboxResult> RunStreamingAsync(SandboxSpec spec, Func<string, CancellationToken, Task> onStdoutLine, CancellationToken cancellationToken);
}
